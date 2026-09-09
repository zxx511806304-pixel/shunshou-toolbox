using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using W = DocumentFormat.OpenXml.Wordprocessing;
using WindowsPdf = Windows.Data.Pdf.PdfDocument;

namespace Shunshou.Core;

public sealed class PdfService
{
    public async Task<IReadOnlyList<string>> ExportImagesAsync(string input, string outputDir, int dpi,
        bool longImage, IProgress<ToolProgress>? progress = null, CancellationToken ct = default)
    {
        ValidateInput(input);
        if (dpi is < 72 or > 600) throw new ArgumentOutOfRangeException(nameof(dpi), "导出分辨率应在 72–600 DPI 之间。");
        Directory.CreateDirectory(outputDir);
        string staging = CreateStaging(outputDir);
        var committed = new List<string>();
        try
        {
            var document = await WindowsPdf.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(Path.GetFullPath(input)));
            if (document.PageCount == 0) throw new InvalidDataException("PDF 没有可导出的页面。");
            var pages = new List<string>();
            for (uint index = 0; index < document.PageCount; index++)
            {
                ct.ThrowIfCancellationRequested();
                string path = Path.Combine(staging, $"page-{index + 1:D4}.png");
                await RenderPageAsync(document, index, path, dpi, ct);
                pages.Add(path);
                progress?.Report(new ToolProgress((index + 1) * (longImage ? 60.0 : 95.0) / document.PageCount,
                    $"正在以 {dpi} DPI 导出第 {index + 1}/{document.PageCount} 页"));
            }
            if (longImage)
            {
                string stagedImage = Path.Combine(staging, "long.png");
                await WriteLongPngAsync(pages, stagedImage, dpi, progress, ct);
                ct.ThrowIfCancellationRequested();
                string final = UniquePath(outputDir, Path.GetFileNameWithoutExtension(input) + $"_长图_{dpi}dpi", ".png");
                File.Move(stagedImage, final);
                committed.Add(final);
            }
            else
            {
                for (int i = 0; i < pages.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    string final = UniquePath(outputDir, Path.GetFileNameWithoutExtension(input) + $"_第{i + 1:D3}页_{dpi}dpi", ".png");
                    File.Move(pages[i], final);
                    committed.Add(final);
                }
            }
            progress?.Report(new ToolProgress(100, longImage ? "高清长图已导出；全部页面保留原设定分辨率" : $"已导出 {committed.Count} 张图片"));
            return committed;
        }
        catch
        {
            foreach (string path in committed) TryDelete(path);
            throw;
        }
        finally { CleanupStaging(staging); }
    }

    /// <summary>Exports real editable text. Page layout, charts and tables are not reconstructed.</summary>
    public async Task<string> ExportEditableAsync(string input, string outputDir, string format,
        IProgress<ToolProgress>? progress = null, CancellationToken ct = default)
    {
        ValidateInput(input);
        format = format.TrimStart('.').ToLowerInvariant();
        if (format is not ("docx" or "pptx")) throw new ArgumentException("可编辑导出只支持 DOCX 和 PPTX。", nameof(format));
        Directory.CreateDirectory(outputDir);
        string staging = CreateStaging(outputDir);
        try
        {
            var pages = new List<string>();
            WindowsPdf? raster = null;
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(input);
            foreach (var page in pdf.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                string text = ContentOrderTextExtractor.GetText(page, true);
                if (string.IsNullOrWhiteSpace(text))
                {
                    raster ??= await WindowsPdf.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(Path.GetFullPath(input)));
                    string png = Path.Combine(staging, $"ocr-{page.Number}.png");
                    progress?.Report(new ToolProgress(75.0 * (page.Number - 1) / pdf.NumberOfPages,
                        $"第 {page.Number} 页无文字层，正在本地识别"));
                    // 150 DPI preserves ordinary document text and keeps an A4 page within the OS OCR width limit.
                    await RenderPageAsync(raster, (uint)(page.Number - 1), png, 150, ct);
                    try { text = await new OcrService().RecognizeAsync(png, ct); }
                    catch (InvalidOperationException ex)
                    {
                        throw new InvalidOperationException($"第 {page.Number} 页无法取得可编辑文字。{ex.Message} 已停止导出，避免生成缺页文档。", ex);
                    }
                }
                pages.Add(CleanXmlText(text));
                progress?.Report(new ToolProgress(75.0 * page.Number / pdf.NumberOfPages,
                    $"已提取第 {page.Number}/{pdf.NumberOfPages} 页文字"));
            }
            if (pages.Count == 0) throw new InvalidDataException("PDF 没有可导出的页面。");
            string temporary = Path.Combine(staging, "editable." + format);
            if (format == "docx") WriteWord(temporary, pages, ct);
            else WritePresentation(temporary, pages, ct);
            ct.ThrowIfCancellationRequested();
            string final = UniquePath(outputDir, Path.GetFileNameWithoutExtension(input) + "_可编辑文字", "." + format);
            File.Move(temporary, final);
            progress?.Report(new ToolProgress(100, "可编辑文字已导出；复杂版式、图表与表格结构未还原"));
            return final;
        }
        finally { CleanupStaging(staging); }
    }

    public Task<string> MergeAsync(IEnumerable<string> inputs, string outputDir,
        IProgress<ToolProgress>? progress = null, CancellationToken ct = default) => Task.Run(() =>
    {
        var files = inputs.ToArray();
        if (files.Length < 2) throw new ArgumentException("请至少选择两个 PDF 文件进行合并。", nameof(inputs));
        foreach (string file in files) ValidateInput(file);
        Directory.CreateDirectory(outputDir);
        string staging = CreateStaging(outputDir);
        try
        {
            using var output = new PdfSharp.Pdf.PdfDocument();
            for (int i = 0; i < files.Length; i++)
            {
                using var source = PdfReader.Open(files[i], PdfDocumentOpenMode.Import);
                for (int p = 0; p < source.PageCount; p++) { ct.ThrowIfCancellationRequested(); output.AddPage(source.Pages[p]); }
                progress?.Report(new ToolProgress(90.0 * (i + 1) / files.Length, $"已合并 {i + 1}/{files.Length} 个文件"));
            }
            if (output.PageCount == 0) throw new InvalidDataException("所选 PDF 中没有页面。");
            string temporary = Path.Combine(staging, "merged.pdf");
            output.Save(temporary);
            ct.ThrowIfCancellationRequested();
            string final = UniquePath(outputDir, "合并文档", ".pdf");
            File.Move(temporary, final);
            progress?.Report(new ToolProgress(100, "PDF 已合并"));
            return final;
        }
        finally { CleanupStaging(staging); }
    }, ct);

    public Task<IReadOnlyList<string>> SplitAsync(string input, string outputDir,
        IProgress<ToolProgress>? progress = null, CancellationToken ct = default) => Task.Run<IReadOnlyList<string>>(() =>
    {
        ValidateInput(input);
        Directory.CreateDirectory(outputDir);
        string staging = CreateStaging(outputDir);
        var finals = new List<string>();
        try
        {
            using var source = PdfReader.Open(input, PdfDocumentOpenMode.Import);
            for (int p = 0; p < source.PageCount; p++)
            {
                ct.ThrowIfCancellationRequested();
                using var output = new PdfSharp.Pdf.PdfDocument();
                output.AddPage(source.Pages[p]);
                string temporary = Path.Combine(staging, $"page-{p + 1}.pdf");
                output.Save(temporary);
                string final = UniquePath(outputDir, Path.GetFileNameWithoutExtension(input) + $"_第{p + 1:D3}页", ".pdf");
                File.Move(temporary, final);
                finals.Add(final);
                progress?.Report(new ToolProgress(100.0 * (p + 1) / source.PageCount, $"已拆分第 {p + 1}/{source.PageCount} 页"));
            }
            if (finals.Count == 0) throw new InvalidDataException("PDF 没有可拆分的页面。");
            return finals;
        }
        catch { foreach (string path in finals) TryDelete(path); throw; }
        finally { CleanupStaging(staging); }
    }, ct);

    private static async Task RenderPageAsync(WindowsPdf document, uint index, string path, int dpi, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var page = document.GetPage(index);
        // WinRT exposes the page size in device-independent pixels: 96 DIP per inch.
        uint width = checked((uint)Math.Ceiling(page.Size.Width * dpi / 96.0));
        uint height = checked((uint)Math.Ceiling(page.Size.Height * dpi / 96.0));
        if (width == 0 || height == 0 || (ulong)width * height > 200_000_000)
            throw new InvalidOperationException($"第 {index + 1} 页在 {dpi} DPI 下过大，无法安全处理。请选择较低 DPI；软件未自动降低清晰度。");
        await File.WriteAllBytesAsync(path, [], ct);
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        using var output = await file.OpenAsync(FileAccessMode.ReadWrite);
        await page.RenderToStreamAsync(output, new PdfPageRenderOptions
        {
            DestinationWidth = width, DestinationHeight = height, BitmapEncoderId = BitmapEncoder.PngEncoderId,
            BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255)
        });
        await output.FlushAsync();
        ct.ThrowIfCancellationRequested();
    }

    private static async Task WriteLongPngAsync(IReadOnlyList<string> pages, string output, int dpi,
        IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        int width = 0;
        long height = 0;
        foreach (string path in pages)
        {
            ct.ThrowIfCancellationRequested();
            using var stream = await (await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path))).OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            width = Math.Max(width, checked((int)decoder.PixelWidth));
            height += decoder.PixelHeight;
        }
        if (height > int.MaxValue) throw new InvalidOperationException("长图高度超过 PNG 格式上限，请分批导出。");
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024);
        file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8; header[9] = 2; // RGB, 8-bit, non-interlaced.
        WriteChunk(file, "IHDR", header);
        byte[] density = new byte[9];
        uint pixelsPerMeter = (uint)Math.Round(dpi / 0.0254);
        BinaryPrimitives.WriteUInt32BigEndian(density, pixelsPerMeter);
        BinaryPrimitives.WriteUInt32BigEndian(density.AsSpan(4), pixelsPerMeter);
        density[8] = 1;
        WriteChunk(file, "pHYs", density);
        using (var idat = new PngDataStream(file))
        using (var zlib = new ZLibStream(idat, CompressionLevel.SmallestSize, true))
        {
            byte[] row = new byte[checked(width * 3 + 1)];
            for (int index = 0; index < pages.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                using var stream = await (await StorageFile.GetFileFromPathAsync(Path.GetFullPath(pages[index]))).OpenReadAsync();
                var decoder = await BitmapDecoder.CreateAsync(stream);
                var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Straight,
                    new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb)).DetachPixelData();
                int pageWidth = checked((int)decoder.PixelWidth), pageHeight = checked((int)decoder.PixelHeight);
                int offset = (width - pageWidth) / 2;
                for (int y = 0; y < pageHeight; y++)
                {
                    ct.ThrowIfCancellationRequested();
                    Array.Fill(row, (byte)255); row[0] = 0; // PNG filter None; preserves every source pixel.
                    int source = checked(y * pageWidth * 4), target = 1 + offset * 3;
                    for (int x = 0; x < pageWidth; x++, source += 4, target += 3)
                    {
                        int alpha = pixels[source + 3];
                        for (int c = 0; c < 3; c++) row[target + c] = (byte)((pixels[source + c] * alpha + 255 * (255 - alpha) + 127) / 255);
                    }
                    zlib.Write(row);
                }
                progress?.Report(new ToolProgress(60 + 38.0 * (index + 1) / pages.Count, $"正在无损拼接第 {index + 1}/{pages.Count} 页"));
            }
        }
        WriteChunk(file, "IEND", []);
    }

    private static void WriteWord(string path, IReadOnlyList<string> pages, CancellationToken ct)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        var body = new W.Body();
        main.Document = new W.Document(body);
        for (int p = 0; p < pages.Count; p++)
        {
            ct.ThrowIfCancellationRequested();
            if (p > 0) body.Append(new W.Paragraph(new W.Run(new W.Break { Type = W.BreakValues.Page })));
            foreach (string line in pages[p].Replace("\r", "").Split('\n'))
                body.Append(new W.Paragraph(new W.Run(new W.RunProperties(
                    new W.RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", EastAsia = "Microsoft YaHei" },
                    new W.FontSize { Val = "22" }), new W.Text(line) { Space = SpaceProcessingModeValues.Preserve })));
        }
        body.Append(new W.SectionProperties(new W.PageSize { Width = 11906, Height = 16838 },
            new W.PageMargin { Top = 1134, Right = 1134, Bottom = 1134, Left = 1134, Header = 567, Footer = 567, Gutter = 0 }));
        main.Document.Save();
    }

    private static void WritePresentation(string path, IReadOnlyList<string> pages, CancellationToken ct)
    {
        using var document = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        var part = document.AddPresentationPart();
        var master = part.AddNewPart<SlideMasterPart>();
        var layout = master.AddNewPart<SlideLayoutPart>();
        layout.SlideLayout = new P.SlideLayout(new P.CommonSlideData(EmptyShapeTree()),
            new P.ColorMapOverride(new A.MasterColorMapping())) { Type = P.SlideLayoutValues.Blank, Preserve = true };
        layout.AddPart(master);
        master.SlideMaster = new P.SlideMaster(new P.CommonSlideData(EmptyShapeTree()),
            new P.ColorMap { Background1 = A.ColorSchemeIndexValues.Light1, Text1 = A.ColorSchemeIndexValues.Dark1,
                Background2 = A.ColorSchemeIndexValues.Light2, Text2 = A.ColorSchemeIndexValues.Dark2,
                Accent1 = A.ColorSchemeIndexValues.Accent1, Accent2 = A.ColorSchemeIndexValues.Accent2,
                Accent3 = A.ColorSchemeIndexValues.Accent3, Accent4 = A.ColorSchemeIndexValues.Accent4,
                Accent5 = A.ColorSchemeIndexValues.Accent5, Accent6 = A.ColorSchemeIndexValues.Accent6,
                Hyperlink = A.ColorSchemeIndexValues.Hyperlink, FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink },
            new P.SlideLayoutIdList(new P.SlideLayoutId { Id = 2147483649, RelationshipId = master.GetIdOfPart(layout) }),
            new P.TextStyles(new P.TitleStyle(), new P.BodyStyle(), new P.OtherStyle()));
        var theme = master.AddNewPart<ThemePart>();
        theme.Theme = new A.Theme(ThemeElements()) { Name = "顺手工具箱" };
        var slideIds = new P.SlideIdList();
        part.Presentation = new P.Presentation(
            new P.SlideMasterIdList(new P.SlideMasterId { Id = 2147483648, RelationshipId = part.GetIdOfPart(master) }),
            slideIds, new P.SlideSize { Cx = 12192000, Cy = 6858000, Type = P.SlideSizeValues.Screen16x9 },
            new P.NotesSize { Cx = 6858000, Cy = 9144000 });
        uint nextId = 256;
        for (int p = 0; p < pages.Count; p++)
        {
            var lines = WrapPresentationText(pages[p]).ToArray();
            for (int chunk = 0; chunk < Math.Max(1, lines.Length); chunk += 14)
            {
                ct.ThrowIfCancellationRequested();
                var slide = part.AddNewPart<SlidePart>();
                slide.AddPart(layout);
                var tree = EmptyShapeTree();
                string suffix = chunk == 0 ? "" : $" · 续 {chunk / 14}";
                tree.Append(TextShape(2, $"第 {p + 1} 页{suffix}", [ $"第 {p + 1} 页{suffix}" ], 3000, 500000, 250000, 11192000, 650000));
                tree.Append(TextShape(3, "可编辑正文", lines.Skip(chunk).Take(14), 2000, 500000, 1100000, 11192000, 5350000));
                slide.Slide = new P.Slide(new P.CommonSlideData(tree), new P.ColorMapOverride(new A.MasterColorMapping()));
                slide.Slide.Save();
                slideIds.Append(new P.SlideId { Id = nextId++, RelationshipId = part.GetIdOfPart(slide) });
            }
        }
        theme.Theme.Save(); master.SlideMaster.Save(); layout.SlideLayout.Save(); part.Presentation.Save();
    }

    private static IEnumerable<string> WrapPresentationText(string text)
    {
        // Conservatively wrap by display width so that editable Chinese text is never silently clipped.
        foreach (string raw in text.Replace("\r", "").Split('\n'))
        {
            if (raw.Length == 0) { yield return ""; continue; }
            var line = new StringBuilder(); int units = 0;
            foreach (var rune in raw.EnumerateRunes())
            {
                int size = rune.Value < 0x2E80 ? 1 : 2;
                if (units + size > 72) { yield return line.ToString(); line.Clear(); units = 0; }
                line.Append(rune.ToString()); units += size;
            }
            if (line.Length > 0) yield return line.ToString();
        }
    }

    private static P.ShapeTree EmptyShapeTree() => new(
        new P.NonVisualGroupShapeProperties(new P.NonVisualDrawingProperties { Id = 1, Name = "" },
            new P.NonVisualGroupShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()),
        new P.GroupShapeProperties(new A.TransformGroup(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = 0, Cy = 0 },
            new A.ChildOffset { X = 0, Y = 0 }, new A.ChildExtents { Cx = 0, Cy = 0 })));

    private static P.Shape TextShape(uint id, string name, IEnumerable<string> lines, int size, long x, long y, long width, long height)
    {
        var body = new P.TextBody(new A.BodyProperties { Wrap = A.TextWrappingValues.Square }, new A.ListStyle());
        foreach (string line in lines)
            body.Append(new A.Paragraph(new A.ParagraphProperties(new A.LineSpacing(new A.SpacingPercent { Val = 115000 })),
                new A.Run(new A.RunProperties(new A.SolidFill(new A.RgbColorModelHex { Val = "20252B" }),
                    new A.LatinFont { Typeface = "Calibri" }, new A.EastAsianFont { Typeface = "Microsoft YaHei" }) { Language = "zh-CN", FontSize = size },
                    new A.Text(line)), new A.EndParagraphRunProperties { Language = "zh-CN", FontSize = size }));
        if (!body.Elements<A.Paragraph>().Any()) body.Append(new A.Paragraph());
        return new P.Shape(new P.NonVisualShapeProperties(new P.NonVisualDrawingProperties { Id = id, Name = name },
                new P.NonVisualShapeDrawingProperties { TextBox = true }, new P.ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(new A.Transform2D(new A.Offset { X = x, Y = y }, new A.Extents { Cx = width, Cy = height }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }, new A.NoFill()), body);
    }

    private static A.ThemeElements ThemeElements()
    {
        var colors = new A.ColorScheme(new A.Dark1Color(new A.SystemColor { Val = A.SystemColorValues.WindowText, LastColor = "000000" }),
            new A.Light1Color(new A.SystemColor { Val = A.SystemColorValues.Window, LastColor = "FFFFFF" }),
            new A.Dark2Color(new A.RgbColorModelHex { Val = "20252B" }), new A.Light2Color(new A.RgbColorModelHex { Val = "F7F8FA" }),
            new A.Accent1Color(new A.RgbColorModelHex { Val = "087F8C" }), new A.Accent2Color(new A.RgbColorModelHex { Val = "5671C1" }),
            new A.Accent3Color(new A.RgbColorModelHex { Val = "8BA463" }), new A.Accent4Color(new A.RgbColorModelHex { Val = "D29552" }),
            new A.Accent5Color(new A.RgbColorModelHex { Val = "A379A7" }), new A.Accent6Color(new A.RgbColorModelHex { Val = "619FB5" }),
            new A.Hyperlink(new A.RgbColorModelHex { Val = "0563C1" }), new A.FollowedHyperlinkColor(new A.RgbColorModelHex { Val = "954F72" })) { Name = "顺手工具箱" };
        var fonts = new A.FontScheme(new A.MajorFont(new A.LatinFont { Typeface = "Calibri" },
                new A.EastAsianFont { Typeface = "Microsoft YaHei" }, new A.ComplexScriptFont { Typeface = "Arial" }),
            new A.MinorFont(new A.LatinFont { Typeface = "Calibri" }, new A.EastAsianFont { Typeface = "Microsoft YaHei" },
                new A.ComplexScriptFont { Typeface = "Arial" })) { Name = "Office" };
        var fills = new A.FillStyleList(); var backgrounds = new A.BackgroundFillStyleList();
        var outlines = new A.LineStyleList(); var effects = new A.EffectStyleList();
        for (int i = 0; i < 3; i++)
        {
            fills.Append(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }));
            backgrounds.Append(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }));
            outlines.Append(new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                new A.PresetDash { Val = A.PresetLineDashValues.Solid }) { Width = 9525 * (i + 1) });
            effects.Append(new A.EffectStyle(new A.EffectList()));
        }
        return new A.ThemeElements(colors, fonts, new A.FormatScheme(fills, outlines, effects, backgrounds) { Name = "Office" });
    }

    private static string CleanXmlText(string text) => new(text.Where(c => c is '\t' or '\n' or '\r' || c >= ' ').ToArray());
    private static void ValidateInput(string input)
    {
        if (!File.Exists(input)) throw new FileNotFoundException("找不到 PDF 文件。", input);
        if (!Path.GetExtension(input).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择 PDF 文件。", nameof(input));
    }
    private static string CreateStaging(string outputDir)
    {
        string path = Path.Combine(Path.GetFullPath(outputDir), ".shunshou-pdf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    private static string UniquePath(string folder, string name, string extension)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        if (name.Length > 100) name = name[..100];
        string candidate = Path.Combine(Path.GetFullPath(folder), name + extension);
        for (int i = 2; File.Exists(candidate); i++) candidate = Path.Combine(Path.GetFullPath(folder), $"{name} ({i}){extension}");
        return candidate;
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private static void CleanupStaging(string path)
    {
        // The path is created by this service; no input-derived recursion or following of user folders.
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length); output.Write(length);
        byte[] name = Encoding.ASCII.GetBytes(type); output.Write(name); output.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (byte value in name) crc = Crc(crc, value);
        foreach (byte value in data) crc = Crc(crc, value);
        BinaryPrimitives.WriteUInt32BigEndian(length, crc ^ 0xFFFFFFFF); output.Write(length);
    }
    private static uint Crc(uint crc, byte value)
    {
        crc ^= value;
        for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320U : 0);
        return crc;
    }
    private sealed class PngDataStream(Stream output) : Stream
    {
        private readonly byte[] buffer = new byte[128 * 1024];
        private int count;
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] bytes, int offset, int length) => Write(bytes.AsSpan(offset, length));
        public override void Write(ReadOnlySpan<byte> bytes)
        {
            while (!bytes.IsEmpty)
            {
                int take = Math.Min(buffer.Length - count, bytes.Length);
                bytes[..take].CopyTo(buffer.AsSpan(count)); count += take; bytes = bytes[take..];
                if (count == buffer.Length) Flush();
            }
        }
        public override void Flush() { if (count > 0) { WriteChunk(output, "IDAT", buffer.AsSpan(0, count)); count = 0; } }
        protected override void Dispose(bool disposing) { if (disposing) Flush(); base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
