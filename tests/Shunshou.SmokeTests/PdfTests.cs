using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using PdfSharp.Pdf.IO;
using PdfSharp.Drawing;
using Shunshou.Core;
using W = DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;

namespace Shunshou.SmokeTests;

public static class PdfTests
{
    public static async Task RunAsync(string root)
    {
        string folder = Path.Combine(root, "pdf"); Directory.CreateDirectory(folder);
        string input = Path.Combine(folder, "中英文示例.pdf");
        CreatePdf(input, 3, false);
        byte[] sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(input));
        var service = new PdfService();
        string imagesFolder = Path.Combine(folder, "逐页");
        var images = await service.ExportImagesAsync(input, imagesFolder, 300, false);
        Check(images.Count == 3, "逐页导出应包含全部 3 页");
        Check(Path.GetFileName(images[0]) == "中英文示例_page001_300dpi.png", "PDF 图片保留原名称，仅页码后缀使用英文");
        var first = ReadPngHeader(images[0]);
        Check(first.Width is >= 2480 and <= 2482 && first.Height is >= 3507 and <= 3509,
            $"A4 300 DPI 应约为 2481 × 3508，实际 {first.Width} × {first.Height}");
        var repeat = await service.ExportImagesAsync(input, imagesFolder, 300, false);
        Check(!repeat.Intersect(images).Any() && images.All(File.Exists), "重复导出不能覆盖已有文件");
        Console.WriteLine("PASS PDF: 3 页中英文 A4 / 300 DPI 逐页导出 + 不覆盖");

        var longResult = await service.ExportImagesAsync(input, Path.Combine(folder, "三页长图"), 300, true);
        Check(Path.GetFileName(longResult.Single()) == "中英文示例_long_300dpi.png", "长图使用英文附加后缀");
        var longHeader = ReadPngHeader(longResult.Single());
        Check(longHeader.Width == first.Width && longHeader.Height == images.Sum(p => ReadPngHeader(p).Height),
            "长图必须保持逐页分辨率，总高度等于各页高度之和");
        long decodedBytes = ValidatePngPayload(longResult.Single(), longHeader.Width, longHeader.Height);
        Check(decodedBytes == (long)(longHeader.Width * 3 + 1) * longHeader.Height, "长图应包含完整 RGB 行数据");
        Console.WriteLine("PASS PDF: 300 DPI 无损长图 PNG 数据、尺寸与 CRC");

        foreach (string format in new[] { "docx", "pptx" })
        {
            string output = await service.ExportEditableAsync(input, Path.Combine(folder, "可编辑"), format);
            Check(Path.GetFileName(output) == "中英文示例_text." + format, "可编辑文本导出保留原名称并使用英文后缀");
            using OpenXmlPackage package = format == "docx" ? WordprocessingDocument.Open(output, false) : PresentationDocument.Open(output, false);
            var errors = new OpenXmlValidator().Validate(package).ToArray();
            Check(errors.Length == 0, $"{format} OpenXml 验证失败: " + string.Join("; ", errors.Select(x => x.Description + " " + x.Path?.XPath)));
            string text;
            if (package is WordprocessingDocument word)
                text = string.Concat(word.MainDocumentPart!.Document!.Descendants<W.Text>().Select(x => x.Text));
            else
            {
                var presentation = (PresentationDocument)package;
                Check(presentation.PresentationPart!.SlideParts.All(s => !s.ImageParts.Any()), "可编辑 PPT 不能用整页图片冒充文本");
                text = string.Concat(presentation.PresentationPart.SlideParts.SelectMany(s => s.Slide!.Descendants<A.Text>()).Select(x => x.Text));
            }
            string normalized = Normalize(text);
            Check(normalized.Contains("学习办公文字可以编辑"), $"{format} 应保留可编辑中文文字");
            Check(normalized.Contains("Hello12345"), $"{format} 应保留可编辑英文和数字");
            Check(normalized.Contains("Page3"), $"{format} 应保留末页文字");
            Console.WriteLine($"PASS PDF: {format} 中英文可编辑文本 + OpenXmlValidator 零错误");
        }

        // A pure raster PDF exercises the portable default model path, not a test-only injected engine.
        string scanned = Path.Combine(folder, "扫描件.pdf");
        using (var scanDocument = new PdfSharp.Pdf.PdfDocument())
        {
            var scanPage = scanDocument.AddPage(); scanPage.Size = PdfSharp.PageSize.A4;
            using (var graphics = XGraphics.FromPdfPage(scanPage))
            using (var sourceImage = XImage.FromFile(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ocr-zh-en.png")))
                graphics.DrawImage(sourceImage, 40, 60, 515, 515 * sourceImage.PixelHeight / sourceImage.PixelWidth);
            scanDocument.Save(scanned);
        }
        foreach (string format in new[] { "docx", "pptx" })
        {
            string output = await service.ExportEditableAsync(scanned, Path.Combine(folder, "扫描件文字"), format);
            using OpenXmlPackage package = format == "docx" ? WordprocessingDocument.Open(output, false) : PresentationDocument.Open(output, false);
            Check(!new OpenXmlValidator().Validate(package).Any(), $"扫描件 {format} 必须通过 OpenXml 验证");
            string text = package is WordprocessingDocument word
                ? string.Concat(word.MainDocumentPart!.Document!.Descendants<W.Text>().Select(x => x.Text))
                : string.Concat(((PresentationDocument)package).PresentationPart!.SlideParts.SelectMany(s => s.Slide!.Descendants<A.Text>()).Select(x => x.Text));
            string normalized = Normalize(text);
            Check(normalized.Contains("文字识别") && normalized.Contains("Hello12345"), "扫描 PDF 必须通过随包 OCR 生成可编辑中英文文字。结果：" + text);
            Console.WriteLine($"PASS PDF: 扫描件 → {format} 默认随包 OCR / 中英文真文本 / OpenXmlValidator");
        }

        var split = await service.SplitAsync(input, Path.Combine(folder, "拆分"));
        Check(split.Count == 3, "拆分应输出 3 个 PDF");
        Check(Path.GetFileName(split[0]) == "中英文示例_page001.pdf", "拆分结果仅附加英文页码后缀");
        foreach (string single in split) { using var pdf = UglyToad.PdfPig.PdfDocument.Open(single); Check(pdf.NumberOfPages == 1, "拆分页数应为 1"); }
        string merged = await service.MergeAsync(split, Path.Combine(folder, "合并"));
        Check(Path.GetFileName(merged) == "Merged.pdf", "合并文档的自动名称使用英文");
        using (var pdf = UglyToad.PdfPig.PdfDocument.Open(merged)) Check(pdf.NumberOfPages == 3, "合并后应有 3 页");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await MustThrow<OperationCanceledException>(() => service.ExportImagesAsync(input, Path.Combine(folder, "取消"), 300, false, null, cancelled.Token));
            await MustThrow<OperationCanceledException>(() => service.ExportEditableAsync(input, Path.Combine(folder, "取消"), "docx", null, cancelled.Token));
        }
        string damaged = Path.Combine(folder, "损坏.pdf"); await File.WriteAllTextAsync(damaged, "%PDF-1.7\ninvalid");
        await MustThrow<Exception>(() => service.ExportEditableAsync(damaged, Path.Combine(folder, "无效"), "docx"));
        string encrypted = Path.Combine(folder, "加密.pdf"); CreatePdf(encrypted, 1, true);
        await MustThrow<Exception>(() => service.ExportImagesAsync(encrypted, Path.Combine(folder, "无效"), 150, false));
        byte[] updatedSourceHash = SHA256.HashData(await File.ReadAllBytesAsync(input));
        Check(sourceHash.SequenceEqual(updatedSourceHash), "所有操作必须保持源 PDF 不变");
        Check(!Directory.EnumerateDirectories(folder, ".shunshou-pdf-*", SearchOption.AllDirectories).Any(), "完成或失败后应清理临时文件夹");
        Console.WriteLine("PASS PDF: 合并 / 拆分 / 取消 / 损坏和加密错误 / 原文件保持");

        string hundred = Path.Combine(folder, "100页A4.pdf"); CreatePdf(hundred, 100, false);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var hundredPages = await service.ExportImagesAsync(hundred, Path.Combine(folder, "100页逐页"), 150, false);
        Check(hundredPages.Count == 100, "100 页 PDF 逐页导出不能漏页");
        var hundredLong = await service.ExportImagesAsync(hundred, Path.Combine(folder, "100页长图"), 150, true);
        var hundredHeader = ReadPngHeader(hundredLong.Single());
        Check(hundredHeader.Width == hundredPages.Max(p => ReadPngHeader(p).Width)
              && hundredHeader.Height == hundredPages.Sum(p => ReadPngHeader(p).Height), "100 页长图需完整保留所有页面像素尺寸");
        Check(hundredHeader.Height > 65535, "测试必须覆盖超过普通 JPEG 高度限制的长图");
        ValidatePngPayload(hundredLong.Single(), hundredHeader.Width, hundredHeader.Height);
        Console.WriteLine($"PASS PDF: 100 页 A4 / 150 DPI，长图 {hundredHeader.Width} × {hundredHeader.Height}，耗时 {watch.Elapsed.TotalSeconds:F1} 秒，PNG CRC 和完整解压验证通过");
    }

    private static void CreatePdf(string path, int pages, bool encrypt)
    {
        using var document = new PdfSharp.Pdf.PdfDocument();
        using var source = PdfReader.Open(Path.Combine(AppContext.BaseDirectory, "Fixtures", "zh-en.pdf"), PdfDocumentOpenMode.Import);
        for (int index = 1; index <= pages; index++)
            document.AddPage(source.Pages[(index - 1) % source.PageCount]);
        if (encrypt) document.SecuritySettings.UserPassword = "shunshou-test-password";
        document.Save(path);
    }

    private static (int Width, int Height) ReadPngHeader(string path)
    {
        using var stream = File.OpenRead(path); Span<byte> header = stackalloc byte[24]; stream.ReadExactly(header);
        Check(header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "输出必须为真实 PNG");
        return (checked((int)BinaryPrimitives.ReadUInt32BigEndian(header[16..20])), checked((int)BinaryPrimitives.ReadUInt32BigEndian(header[20..24])));
    }

    private static long ValidatePngPayload(string path, int width, int height)
    {
        using var file = File.OpenRead(path); file.Position = 8;
        using var packed = new MemoryStream();
        byte[] number = new byte[4];
        bool ended = false;
        while (file.Position < file.Length)
        {
            file.ReadExactly(number); int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(number));
            byte[] type = new byte[4]; file.ReadExactly(type);
            byte[] data = new byte[length]; file.ReadExactly(data); file.ReadExactly(number);
            uint expected = BinaryPrimitives.ReadUInt32BigEndian(number), crc = uint.MaxValue;
            foreach (byte b in type.Concat(data))
            {
                crc ^= b;
                for (int i = 0; i < 8; i++) crc = crc >> 1 ^ ((crc & 1) == 0 ? 0U : 0xEDB88320U);
            }
            Check((crc ^ uint.MaxValue) == expected, "每个 PNG 数据块 CRC 必须正确");
            string kind = System.Text.Encoding.ASCII.GetString(type);
            if (kind == "IDAT") packed.Write(data);
            if (kind == "IEND") { ended = true; break; }
        }
        Check(ended, "PNG 必须完整写出 IEND");
        packed.Position = 0;
        using var zlib = new ZLibStream(packed, CompressionMode.Decompress);
        byte[] row = new byte[checked(width * 3 + 1)];
        for (int y = 0; y < height; y++) { zlib.ReadExactly(row); Check(row[0] == 0, "当前流式 PNG 每行应使用明确的 None filter"); }
        Check(zlib.ReadByte() == -1, "长图不能多写或少写像素行");
        return (long)row.Length * height;
    }
    private static string Normalize(string value) => new(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task MustThrow<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException($"预期出现 {typeof(T).Name}，操作却成功了。");
    }
}
