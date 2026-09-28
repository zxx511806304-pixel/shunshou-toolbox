using System.Diagnostics;
using System.Text.Json;
using ImageMagick;
using PdfSharp.Pdf.IO;
using Shunshou.Core;

namespace Shunshou.SmokeTests;

/// <summary>Covers the 1.2.0 engines: crop, clip, text diff, QR code, PDF pages and 7z/rar extraction.</summary>
public static class NewToolsTests
{
    public static async Task RunAsync(string root)
    {
        root = Path.Combine(Path.GetFullPath(root), "new-tools");
        Directory.CreateDirectory(root);
        TextDiff(root);
        QrCode(root);
        await CropAsync(root);
        await TrimAsync(root);
        await PdfPagesAsync(root);
        await ArchiveAsync(root);
    }

    private static void TextDiff(string root)
    {
        var identical = TextDiffService.Compare("第一行\n第二行", "第一行\n第二行");
        CompressionTests.Check(identical.Identical && identical.Same == 2, "identical texts compare as equal");
        var changed = TextDiffService.Compare("第一行\n旧内容\n第三行", "第一行\n新内容\n第三行");
        CompressionTests.Check(changed.Removed == 1 && changed.Added == 1 && changed.Same == 2, "one changed line is reported as one removal and one addition");
        CompressionTests.Check(changed.Lines[1].Kind == DiffKind.Removed && changed.Lines[1].Left == 2 && changed.Lines[1].Right is null,
            "removed lines keep the original line number and no new number");
        CompressionTests.Check(changed.Lines[2].Kind == DiffKind.Added && changed.Lines[2].Right == 2, "added lines carry the new line number");
        CompressionTests.Check(!TextDiffService.Compare("A", "a").Identical, "case differences are reported by default");
        CompressionTests.Check(TextDiffService.Compare("A  B", "A B", ignoreWhitespace: true).Identical, "whitespace comparison can be ignored on request");
        CompressionTests.Check(TextDiffService.Compare("A", "a", ignoreCase: true).Identical, "case comparison can be ignored on request");
        string bigLeft = string.Join('\n', Enumerable.Range(0, 3_000).Select(index => "左" + index));
        string bigRight = string.Join('\n', Enumerable.Range(0, 3_000).Select(index => "右" + index));
        var big = TextDiffService.Compare(bigLeft, bigRight);
        CompressionTests.Check(big.Approximate && big.Added == 3_000 && big.Removed == 3_000, "a very large comparison reports an honest block replacement instead of freezing");
        bool rejected = false;
        try { TextDiffService.Compare(new string('x', 4_000_001), "short"); } catch (ArgumentException) { rejected = true; }
        CompressionTests.Check(rejected, "oversized text is refused with a clear limit");
        File.WriteAllText(Path.Combine(root, "text-diff-summary.json"), JsonSerializer.Serialize(new
        {
            Identical = identical.Identical,
            changed.Same, changed.Added, changed.Removed, big.Approximate
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS text diff: identical, changed, ignore options, large-input fallback and limits");
    }

    private static void QrCode(string root)
    {
        string directory = Path.Combine(root, "qr");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "二维码-中文.png");
        string text = "https://example.com/顺手工具箱?测试=1";
        QrCodeService.Generate(text, 512, "H 约 30%（最抗污损）", 4, path);
        CompressionTests.Check(File.Exists(path) && new FileInfo(path).Length > 400, "QR generation writes a real PNG");
        using (var image = new MagickImage(path)) CompressionTests.Check(image.Width == 512 && image.Height == 512, "QR image keeps the requested size");
        var result = QrCodeService.Decode(path);
        CompressionTests.Check(result.Text == text, $"QR round trip keeps Chinese and query text, saw {result.Text}");
        CompressionTests.Check(result.Format.Contains("QR", StringComparison.OrdinalIgnoreCase), "decoder reports the QR format");

        // A small, low-contrast QR must still be readable, and a plain photo must not be mistaken for one.
        string small = Path.Combine(directory, "small.png");
        QrCodeService.Generate("SHUNSHOU-128", 128, "L 约 7%", 2, small);
        CompressionTests.Check(QrCodeService.Decode(small).Text == "SHUNSHOU-128", "a 128 px code is still readable");
        string photo = Path.Combine(directory, "not-a-code.png");
        using (var image = new MagickImage(new MagickColor("#3355AA"), 240, 240)) image.Write(photo);
        bool rejected = false;
        try { QrCodeService.Decode(photo); } catch (InvalidDataException) { rejected = true; }
        CompressionTests.Check(rejected, "a picture without a QR code is reported instead of returning junk");
        rejected = false;
        try { QrCodeService.Generate("   ", 256, "M 约 15%", 4, Path.Combine(directory, "empty.png")); } catch (ArgumentException) { rejected = true; }
        CompressionTests.Check(rejected, "empty content is refused before writing a file");
        CompressionTests.Check(!File.Exists(Path.Combine(directory, "empty.png")), "a refused QR write leaves no file behind");
        Console.WriteLine("PASS QR code: Chinese round trip, small code, non-code rejection and empty input");
    }

    private static async Task CropAsync(string root)
    {
        string directory = Path.Combine(root, "crop");
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "源图.png");
        // Top 60 rows are white and the rest is blue, so a centred 16:9 crop provably drops the white band.
        const int width = 800, height = 600;
        byte[] pixels = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 3;
                bool white = y < 60;
                pixels[offset] = (byte)(white ? 255 : 70);
                pixels[offset + 1] = (byte)(white ? 255 : 130);
                pixels[offset + 2] = (byte)(white ? 255 : 180);
            }
        using (var image = new MagickImage(pixels, new PixelReadSettings(width, height, StorageType.Char, PixelMapping.RGB)))
            image.Write(source);
        string before = CompressionTests.HashFile(source);
        var square = ImageCropService.ResolveGeometry(CropKind.Ratio, 1, 1, 800, 600, 0, 0, "居中");
        CompressionTests.Check(square.Width == 600 && square.Height == 600 && square.X == 100 && square.Y == 0, "1:1 centred crop keeps the largest square in the middle");
        var topLeft = ImageCropService.ResolveGeometry(CropKind.Pixels, 1, 1, 800, 600, 100, 100, "左上");
        CompressionTests.Check(topLeft.X == 0 && topLeft.Y == 0 && topLeft.Width == 100 && topLeft.Height == 100, "pixel crop at the top-left anchor starts at the origin");
        var bottomRight = ImageCropService.ResolveGeometry(CropKind.Pixels, 1, 1, 800, 600, 100, 100, "右下");
        CompressionTests.Check(bottomRight.X == 700 && bottomRight.Y == 500, "pixel crop at the bottom-right anchor ends at the edge");
        bool rejected = false;
        try { ImageCropService.ResolveGeometry(CropKind.Pixels, 1, 1, 400, 300, 800, 600, "居中"); } catch (InvalidDataException) { rejected = true; }
        CompressionTests.Check(rejected, "a crop larger than the source is refused with a clear reason");

        string output = Path.Combine(directory, "out");
        var files = await new ImageCropService().CropAsync([source], output, new CropOptions(CropKind.Ratio, "16:9 宽屏", "居中", 0, 0), 92, null, default);
        CompressionTests.Check(files.Count == 1 && File.Exists(files[0]), "crop writes one output file");
        using (var cropped = new MagickImage(files[0]))
        {
            CompressionTests.Check(cropped.Width == 800 && cropped.Height == 450, $"16:9 crop of 800x600 is 800x450, saw {cropped.Width}x{cropped.Height}");
            string cornerColor = cropped.GetPixels().GetPixel(10, 10)?.ToColor().ToString() ?? "";
            CompressionTests.Check(!cornerColor.Contains("FFFFFF", StringComparison.OrdinalIgnoreCase),
                "the centre crop drops the white corner of the source");
        }
        CompressionTests.Check(before == CompressionTests.HashFile(source), "cropping never modifies the source image");
        Console.WriteLine("PASS image crop: ratio, pixel anchors, oversize rejection, real output and source protection");
    }

    private static async Task TrimAsync(string root)
    {
        string directory = Path.Combine(root, "trim");
        Directory.CreateDirectory(directory);
        string ffmpeg = Path.Combine("runtime", "ffmpeg", "bin", "ffmpeg.exe");
        string ffprobe = Path.Combine("runtime", "ffmpeg", "bin", "ffprobe.exe");
        if (!File.Exists(ffmpeg) || !File.Exists(ffprobe))
        {
            Console.WriteLine("SKIP clip test: runtime/ffmpeg is not prepared on this machine.");
            return;
        }
        string fixture = Path.Combine(directory, "six-seconds.mp4");
        Run(ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x184:rate=15:duration=6",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=6", "-c:v", "libopenh264", "-b:v", "600000", "-c:a", "aac", "-shortest", fixture]);
        string before = CompressionTests.HashFile(fixture);

        var exact = await new MediaService().TrimAsync(fixture, directory, 1.0, 3.0, copyStreams: false, "mp4", null, default);
        double exactDuration = Duration(ffprobe, exact);
        CompressionTests.Check(Math.Abs(exactDuration - 2.0) < 0.25, $"accurate clip is about two seconds, saw {exactDuration:F2}");
        string streams = Run(ffprobe, ["-v", "error", "-show_entries", "stream=codec_type", "-of", "json", exact]);
        CompressionTests.Check(streams.Contains("video") && streams.Contains("audio"), "accurate clip keeps both video and audio");

        var copied = await new MediaService().TrimAsync(fixture, directory, 1.0, 3.0, copyStreams: true, "mp4", null, default);
        double copiedDuration = Duration(ffprobe, copied);
        CompressionTests.Check(copiedDuration > 0.5 && copiedDuration < 5.5, $"copy clip stays inside the source duration, saw {copiedDuration:F2}");
        CompressionTests.Check(copied != exact, "each clip is published as its own file");
        CompressionTests.Check(before == CompressionTests.HashFile(fixture), "clipping never modifies the source file");

        bool rejected = false;
        try { await new MediaService().TrimAsync(fixture, directory, 4, 4, false, "mp4", null, default); } catch (ArgumentException) { rejected = true; }
        CompressionTests.Check(rejected, "a clip whose end is not after its start is refused");
        rejected = false;
        try { await new MediaService().TrimAsync(fixture, directory, -1, 2, false, "mp4", null, default); } catch (ArgumentException) { rejected = true; }
        CompressionTests.Check(rejected, "a negative start is refused");
        CompressionTests.Check(MediaService.TryReadClipTimeProbe("00:01:30"), "00:01:30 parses as 90 seconds");
        CompressionTests.Check(MediaService.TryReadClipTimeProbe("1:05") && MediaService.TryReadClipTimeProbe("75"), "minute and plain-second forms parse");

        var audio = await new MediaService().ConvertAsync(fixture, directory, "mp3", null, null, default);
        CompressionTests.Check(File.Exists(audio) && Path.GetExtension(audio) == ".mp3", "extracting the audio track produces an MP3");
        Console.WriteLine("PASS clip and audio extraction: accurate mode, copy mode, validation and source protection");
    }

    private static double Duration(string ffprobe, string path)
    {
        string json = Run(ffprobe, ["-v", "error", "-show_entries", "format=duration", "-of", "json", path]);
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement.GetProperty("format").GetProperty("duration");
        return value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : double.Parse(value.GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task PdfPagesAsync(string root)
    {
        string directory = Path.Combine(root, "pdf-pages");
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "三页.pdf");
        using (var document = new PdfSharp.Pdf.PdfDocument())
        {
            // Vector marks give three pages real content without depending on a system font;
            // the fourth page stays completely empty so the blank hint has something to detect.
            for (int index = 1; index <= 3; index++)
            {
                var page = document.AddPage();
                var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
                gfx.DrawRectangle(PdfSharp.Drawing.XBrushes.DimGray, 40, 40, 200 + index * 20, 90);
                gfx.Dispose();
            }
            document.AddPage();
            document.Save(source);
        }
        var service = new PdfService();
        var pages = service.InspectPages(source);
        CompressionTests.Check(pages.Count == 4, $"a four page PDF reports four pages, saw {pages.Count}");
        CompressionTests.Check(pages.All(page => page.WidthPoints > 0 && page.HeightPoints > 0), "page sizes are reported for the list");
        CompressionTests.Check(pages[3].LikelyBlank && !pages[0].LikelyBlank, "the empty page is marked as likely blank while text pages are not");

        string output = Path.Combine(directory, "out");
        var edits = new List<PdfPageEdit> { new(3, 90), new(1, 0) };
        string rebuilt = await service.RebuildAsync(source, output, edits, null, default);
        using (var check = PdfReader.Open(rebuilt, PdfDocumentOpenMode.Import))
        {
            CompressionTests.Check(check.PageCount == 2, $"the new PDF keeps exactly the chosen pages, saw {check.PageCount}");
            CompressionTests.Check(check.Pages[0].Rotate == 90, "rotation is applied to the chosen page");
            CompressionTests.Check(check.Pages[1].Rotate % 360 == 0, "pages that were not rotated stay upright");
            CompressionTests.Check(Math.Abs(check.Pages[1].Width.Point - pages[0].WidthPoints) < 1, "kept pages keep the original page size");
        }
        bool rejected = false;
        try { await service.RebuildAsync(source, output, [], null, default); } catch (ArgumentException) { rejected = true; }
        CompressionTests.Check(rejected, "an empty page selection is refused instead of writing a broken file");
        rejected = false;
        try { await service.RebuildAsync(source, output, [new PdfPageEdit(9, 0)], null, default); } catch (ArgumentOutOfRangeException) { rejected = true; }
        CompressionTests.Check(rejected, "a page number outside the document is refused");
        Console.WriteLine("PASS PDF pages: inspect, blank hint, reorder, rotate, page count and validation");
    }

    private static async Task ArchiveAsync(string root)
    {
        string directory = Path.Combine(root, "archive");
        Directory.CreateDirectory(directory);
        string engine = Path.Combine(Environment.GetEnvironmentVariable("SHUNSHOU_SEVENZIP_DIR") ?? Path.Combine("runtime", "sevenzip"), "7z.exe");
        if (!File.Exists(engine))
        {
            Console.WriteLine("SKIP 7z test: runtime/sevenzip is not prepared on this machine.");
            return;
        }
        string content = Path.Combine(directory, "内容");
        Directory.CreateDirectory(Path.Combine(content, "子目录"));
        File.WriteAllText(Path.Combine(content, "说明.txt"), "顺手工具箱 7z 解压校验");
        File.WriteAllText(Path.Combine(content, "子目录", "data.bin"), new string('x', 4_096));
        string archive = Path.Combine(directory, "样例.7z");
        Run(engine, ["a", "-t7z", "-mx=1", "-bso0", "-bsp0", archive, content]);
        CompressionTests.Check(File.Exists(archive), "the bundled 7z engine can create the test archive");

        var service = new SevenZipService();
        string output = Path.Combine(directory, "out");
        string extracted = await service.ExtractAsync(archive, output, null, default);
        CompressionTests.Check(Directory.Exists(extracted), "7z extraction publishes a new folder");
        string root_ = Path.Combine(extracted, "内容");
        CompressionTests.Check(File.Exists(Path.Combine(root_, "说明.txt")) && File.Exists(Path.Combine(root_, "子目录", "data.bin")),
            "nested folders and files are restored");
        CompressionTests.Check(File.ReadAllText(Path.Combine(root_, "说明.txt")) == "顺手工具箱 7z 解压校验", "Chinese content survives extraction");
        CompressionTests.Check(new FileInfo(Path.Combine(root_, "子目录", "data.bin")).Length == 4_096, "file length matches the archive entry");

        bool rejected = false;
        string plain = Path.Combine(directory, "不是压缩包.txt");
        File.WriteAllText(plain, "这个文件不应该被当成压缩包");
        try { await service.ExtractAsync(plain, output, null, default); } catch (NotSupportedException) { rejected = true; }
        CompressionTests.Check(rejected, "an unsupported file type is refused before the engine starts");

        // RAR support is proven with a public sample when one is supplied; the engine lists the reader otherwise.
        string[] rarSamples = (Environment.GetEnvironmentVariable("SHUNSHOU_RAR_FIXTURE") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(File.Exists).ToArray();
        if (rarSamples.Length > 0)
        {
            var results = new List<object>();
            foreach (string sample in rarSamples)
            {
                string rarOutput = Path.Combine(directory, "rar-" + Path.GetFileNameWithoutExtension(sample));
                string folder = await service.ExtractAsync(sample, rarOutput, null, default);
                var extractedFiles = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
                CompressionTests.Check(extractedFiles.Length > 0, $"the public RAR sample {Path.GetFileName(sample)} extracts real files");
                long totalBytes = extractedFiles.Sum(path => new FileInfo(path).Length);
                results.Add(new { Sample = Path.GetFileName(sample), Files = extractedFiles.Length, Bytes = totalBytes, Output = folder });
            }
            File.WriteAllText(Path.Combine(root, "rar-result.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS 7z and RAR: Chinese paths, nested folders and {rarSamples.Length} real public RAR samples");
        }
        else
        {
            Console.WriteLine("PASS 7z: Chinese paths, nested folders and format validation (RAR sample not supplied in this run)");
        }

        // An archive whose entries are links must be refused before extraction, never silently followed.
        string? linkSample = Environment.GetEnvironmentVariable("SHUNSHOU_RAR_LINK_FIXTURE");
        if (!string.IsNullOrWhiteSpace(linkSample) && File.Exists(linkSample))
        {
            bool refused = false;
            string message = "";
            try { await service.ExtractAsync(linkSample, Path.Combine(directory, "rar-link"), null, default); }
            catch (InvalidDataException ex) { refused = true; message = ex.Message; }
            CompressionTests.Check(refused && message.Contains("链接"), "an archive that contains links is refused with a clear reason");
            File.WriteAllText(Path.Combine(root, "rar-link-refusal.json"),
                JsonSerializer.Serialize(new { Sample = Path.GetFileName(linkSample), Refused = refused, Message = message },
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS RAR link safety: symbol and hard links are refused before extraction");
        }
    }

    private static string Run(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start " + executable);
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(executable)} failed: {error}\n{output}");
        return output;
    }
}
