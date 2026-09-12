using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
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
                string final = UniquePath(outputDir, Path.GetFileNameWithoutExtension(input) + $"_long_{dpi}dpi", ".png");
                File.Move(stagedImage, final);
                committed.Add(final);
            }
            else
            {
                for (int i = 0; i < pages.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    string final = UniquePath(outputDir, Path.GetFileNameWithoutExtension(input) + $"_page{i + 1:D3}_{dpi}dpi", ".png");
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

    /// <summary>Exports editable text in reading order, preserving page structure and text positions in slides.</summary>
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
            var pages = new List<TextLayoutPage>();
            WindowsPdf? raster = null;
            var ocr = new OcrService();
            using var ocrSession = ocr.CreateSession();
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(input);
            foreach (var page in pdf.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                var native = ExtractNativeText(page);
                var regions = page.GetImages().Select(image => image.BoundingBox)
                    .Select(b => new SKRect((float)Math.Max(0, b.Left), (float)Math.Max(0, page.Height - b.Top),
                        (float)Math.Min(page.Width, b.Right), (float)Math.Min(page.Height, page.Height - b.Bottom)))
                    .Where(b => b.Width >= 12 && b.Height >= 12).ToList();
                bool suspectText = page.Letters.Any(x => IsSuspectText(x.Value));
                if (native.Count == 0 || suspectText)
                    regions = [new SKRect(0, 0, (float)page.Width, (float)page.Height)];
                // Render once per page, then OCR only raster regions. Native
                // headers must not prevent text inside screenshots from being read.
                if (regions.Count > 0)
                {
                    raster ??= await WindowsPdf.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(Path.GetFullPath(input)));
                    string png = Path.Combine(staging, $"ocr-{page.Number}.png");
                    progress?.Report(new ToolProgress(75.0 * (page.Number - 1) / pdf.NumberOfPages,
                        $"正在补充第 {page.Number} 页图片区域的文字"));
                    int dpi = ocr.HasBundledModels ? 200 : 150;
                    await RenderPageAsync(raster, (uint)(page.Number - 1), png, dpi, ct);
                    using var bitmap = SKBitmap.Decode(png) ?? throw new InvalidDataException("无法读取 PDF 页面图像。");
                    double scaleX = bitmap.Width / page.Width, scaleY = bitmap.Height / page.Height;
                    foreach (var region in MergeImageRegions(regions))
                    {
                        ct.ThrowIfCancellationRequested();
                        var bounds = new SKRectI(Math.Max(0, (int)Math.Floor(region.Left * scaleX)),
                            Math.Max(0, (int)Math.Floor(region.Top * scaleY)),
                            Math.Min(bitmap.Width, (int)Math.Ceiling(region.Right * scaleX)),
                            Math.Min(bitmap.Height, (int)Math.Ceiling(region.Bottom * scaleY)));
                        if (bounds.Width <= 0 || bounds.Height <= 0) continue;
                        using var crop = new SKBitmap();
                        if (!bitmap.ExtractSubset(crop, bounds)) throw new InvalidDataException("无法读取 PDF 图片区域。");
                        string cropPath = Path.Combine(staging, "region.png");
                        using (var image = SKImage.FromBitmap(crop))
                        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
                        using (var file = File.Create(cropPath)) data.SaveTo(file);
                        var result = await ocr.RecognizeLayoutAsync(cropPath, ocrSession, ct);
                        foreach (var block in result.Blocks)
                            native.Add(block with { Text = CleanXmlText(block.Text), X = (bounds.Left + block.X) / scaleX,
                                Y = (bounds.Top + block.Y) / scaleY, Width = block.Width / scaleX,
                                Height = block.Height / scaleY, FontSize = Math.Max(5, block.Height / scaleY * 0.85) });
                    }
                    TryDelete(png);
                }
                pages.Add(new TextLayoutPage(page.Width, page.Height, TextLayout.Merge(native)));
                progress?.Report(new ToolProgress(75.0 * page.Number / pdf.NumberOfPages,
                    $"已提取第 {page.Number}/{pdf.NumberOfPages} 页文字"));
            }
            if (pages.Count == 0) throw new InvalidDataException("PDF 没有可导出的页面。");
            string temporary = Path.Combine(staging, "editable." + format);
            if (format == "docx") WriteWord(temporary, pages, ct);
            else WritePresentation(temporary, pages, ct);
            ct.ThrowIfCancellationRequested();
            string final = UniquePath(outputDir, Path.GetFileNameWithoutExtension(input) + "_text", "." + format);
            File.Move(temporary, final);
            int emptyPages = pages.Count(p => p.Blocks.Count == 0);
            int review = pages.Sum(p => p.Blocks.Count(b => b.Confidence is < 0.8));
            string note = emptyPages > 0 ? $"；{emptyPages} 页未检测到文字，已保留空页" : "";
            if (review > 0) note += $"；{review} 处识别置信度较低，请对照原文复核";
            progress?.Report(new ToolProgress(100, "可编辑文字与页面结构已导出；图表和复杂表格未重建" + note));
            return final;
        }
        finally { CleanupStaging(staging); }
    }

    private static List<TextLayoutBlock> ExtractNativeText(UglyToad.PdfPig.Content.Page page)
    {
        var letters = page.Letters.Where(l => !IsSuspectText(l.Value))
            .GroupBy(l => (l.Value, X: Math.Round(l.StartBaseLine.X, 1), Y: Math.Round(l.StartBaseLine.Y, 1)))
            .Select(g => g.First()).ToList();
        var words = NearestNeighbourWordExtractor.Instance.GetWords(letters).ToList();
        var result = new List<TextLayoutBlock>();
        if (words.Count == 0) return result;
        foreach (var paragraph in DocstrumBoundingBoxes.Instance.GetBlocks(words))
        foreach (var line in paragraph.TextLines)
        {
            var box = line.BoundingBox;
            string text = System.Text.RegularExpressions.Regex.Replace(line.Text, @"(?<=[\u3400-\u9fff])[ \t]+(?=[\u3400-\u9fff])", "");
            var sizes = line.Words.SelectMany(w => w.Letters).Select(l => l.PointSize).Where(s => s > 0).Order().ToArray();
            double size = sizes.Length > 0 ? sizes[sizes.Length / 2] : box.Height;
            result.Add(new TextLayoutBlock(CleanXmlText(text), Math.Max(0, box.Left), Math.Max(0, page.Height - box.Top),
                box.Width, box.Height, Math.Clamp(size, 5, 96), IsOcr: false));
        }
        return result;
    }

    private static bool IsSuspectText(string value) => value.Any(c => c == '\uFFFD'
        || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.PrivateUse
        || (char.IsControl(c) && c is not '\r' and not '\n' and not '\t'));

    private static IReadOnlyList<SKRect> MergeImageRegions(List<SKRect> input)
    {
        var merged = new List<SKRect>();
        foreach (var source in input)
        {
            var region = source;
            for (int i = merged.Count - 1; i >= 0; i--)
            {
                if (!region.IntersectsWith(merged[i])) continue;
                region = new SKRect(Math.Min(region.Left, merged[i].Left), Math.Min(region.Top, merged[i].Top),
                    Math.Max(region.Right, merged[i].Right), Math.Max(region.Bottom, merged[i].Bottom));
                merged.RemoveAt(i); i = merged.Count;
            }
            merged.Add(region);
        }
        return merged;
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
            string final = UniquePath(outputDir, "Merged", ".pdf");
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
                string final = UniquePath(outputDir, Path.GetFileNameWithoutExtension(input) + $"_page{p + 1:D3}", ".pdf");
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

    private static void WriteWord(string path, IReadOnlyList<TextLayoutPage> pages, CancellationToken ct)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        var body = new W.Body();
        main.Document = new W.Document(body);
        for (int p = 0; p < pages.Count; p++)
        {
            ct.ThrowIfCancellationRequested();
            var paragraphs = BuildParagraphs(pages[p].Blocks);
            if (paragraphs.Count == 0) paragraphs.Add(("", 11));
            double median = pages[p].Blocks.Select(b => b.FontSize).Where(s => s > 0).DefaultIfEmpty(11).Order().ElementAt(pages[p].Blocks.Count(b => b.FontSize > 0) / 2);
            for (int i = 0; i < paragraphs.Count; i++)
            {
                var item = paragraphs[i];
                var properties = new W.ParagraphProperties(new W.SpacingBetweenLines { After = "100", Line = "260", LineRule = W.LineSpacingRuleValues.Auto });
                if (p > 0 && i == 0) properties.PrependChild(new W.PageBreakBefore());
                var runProperties = new W.RunProperties(
                    new W.RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", EastAsia = "Microsoft YaHei" },
                    new W.FontSize { Val = Math.Round(Math.Clamp(item.Size, 8, 36) * 2).ToString(System.Globalization.CultureInfo.InvariantCulture) });
                if (item.Size > median * 1.2) runProperties.InsertAt(new W.Bold(), 1);
                body.Append(new W.Paragraph(properties, new W.Run(runProperties, new W.Text(item.Text) { Space = SpaceProcessingModeValues.Preserve })));
            }
        }
        body.Append(new W.SectionProperties(new W.PageSize { Width = (uint)Math.Clamp(Math.Round(pages[0].Width * 20), 2880, 31680),
                Height = (uint)Math.Clamp(Math.Round(pages[0].Height * 20), 2880, 31680) },
            new W.PageMargin { Top = 1134, Right = 1134, Bottom = 1134, Left = 1134, Header = 567, Footer = 567, Gutter = 0 }));
        main.Document.Save();
    }

    private static List<(string Text, double Size)> BuildParagraphs(IReadOnlyList<TextLayoutBlock> blocks)
    {
        var result = new List<(string Text, double Size)>();
        TextLayoutBlock? previous = null;
        foreach (var block in blocks)
        {
            double size = block.FontSize > 0 ? block.FontSize : 11;
            bool continuation = previous is not null && result.Count > 0 && block.Y >= previous.Y
                && block.Y - previous.Bottom <= Math.Max(3, previous.Height * 0.7)
                && Math.Abs(block.X - previous.X) <= Math.Max(6, previous.Height * 0.5)
                && Math.Abs(size - (previous.FontSize > 0 ? previous.FontSize : 11)) < 2
                && !previous.Text.TrimEnd().EndsWith('。') && !previous.Text.TrimEnd().EndsWith('.')
                && !previous.Text.TrimEnd().EndsWith('：') && !previous.Text.TrimEnd().EndsWith(':');
            if (continuation)
                result[^1] = (TextLayout.JoinLines([result[^1].Text, block.Text]), result[^1].Size);
            else result.Add((block.Text, size));
            previous = block;
        }
        return result;
    }

    private static void WritePresentation(string path, IReadOnlyList<TextLayoutPage> pages, CancellationToken ct)
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
        int slideWidth = (int)Math.Clamp(Math.Round(pages[0].Width * 12700), 914400, 51206400);
        int slideHeight = (int)Math.Clamp(Math.Round(pages[0].Height * 12700), 914400, 51206400);
        part.Presentation = new P.Presentation(
            new P.SlideMasterIdList(new P.SlideMasterId { Id = 2147483648, RelationshipId = part.GetIdOfPart(master) }),
            slideIds, new P.SlideSize { Cx = slideWidth, Cy = slideHeight, Type = P.SlideSizeValues.Custom },
            new P.NotesSize { Cx = 6858000, Cy = 9144000 });
        uint nextId = 256;
        for (int p = 0; p < pages.Count; p++)
        {
            ct.ThrowIfCancellationRequested();
            var slide = part.AddNewPart<SlidePart>();
            slide.AddPart(layout);
            var tree = EmptyShapeTree();
            double scale = Math.Min(slideWidth / pages[p].Width, slideHeight / pages[p].Height);
            double offsetX = (slideWidth - pages[p].Width * scale) / 2, offsetY = (slideHeight - pages[p].Height * scale) / 2;
            uint shapeId = 2;
            foreach (var block in pages[p].Blocks)
            {
                ct.ThrowIfCancellationRequested();
                long x = (long)Math.Max(0, offsetX + block.X * scale), y = (long)Math.Max(0, offsetY + block.Y * scale);
                long width = (long)Math.Max(12700, Math.Min(slideWidth - x, block.Width * scale * 1.06));
                long height = (long)Math.Max(12700, Math.Min(slideHeight - y, block.Height * scale * 1.4));
                int font = (int)Math.Clamp(Math.Round((block.FontSize > 0 ? block.FontSize : block.Height * 0.85) * scale / 127), 400, 9600);
                string name = block.Confidence is < 0.8 ? "可编辑文字（识别待复核）" : "可编辑文字";
                tree.Append(TextShape(shapeId++, name, [block.Text], font, x, y, width, height));
            }
            slide.Slide = new P.Slide(new P.CommonSlideData(tree), new P.ColorMapOverride(new A.MasterColorMapping()));
            slide.Slide.Save();
            slideIds.Append(new P.SlideId { Id = nextId++, RelationshipId = part.GetIdOfPart(slide) });
        }
        theme.Theme.Save(); master.SlideMaster.Save(); layout.SlideLayout.Save(); part.Presentation.Save();
    }

    private static P.ShapeTree EmptyShapeTree() => new(
        new P.NonVisualGroupShapeProperties(new P.NonVisualDrawingProperties { Id = 1, Name = "" },
            new P.NonVisualGroupShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()),
        new P.GroupShapeProperties(new A.TransformGroup(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = 0, Cy = 0 },
            new A.ChildOffset { X = 0, Y = 0 }, new A.ChildExtents { Cx = 0, Cy = 0 })));

    private static P.Shape TextShape(uint id, string name, IEnumerable<string> lines, int size, long x, long y, long width, long height)
    {
        var body = new P.TextBody(new A.BodyProperties(new A.NormalAutoFit()) { Wrap = A.TextWrappingValues.None,
            LeftInset = 0, RightInset = 0, TopInset = 0, BottomInset = 0 }, new A.ListStyle());
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
