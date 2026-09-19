using Windows.Storage;
using WindowsPdf = Windows.Data.Pdf.PdfDocument;

namespace Shunshou.Core;

public sealed partial class PdfService
{
    private async Task<string> ExportLayoutAsync(string input, string outputDir, string format,
        IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        ValidateInput(input);
        format = format.TrimStart('.').ToLowerInvariant();
        if (format is not ("docx" or "pptx")) throw new ArgumentException("请选择 Word 或 PPT 格式。", nameof(format));
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(outputDir);
        string staging = CreateStaging(outputDir);
        try
        {
            using var source = UglyToad.PdfPig.PdfDocument.Open(input);
            if (source.NumberOfPages == 0) throw new InvalidDataException("PDF 没有可导出的页面。");
            var pages = new List<PdfOfficePage>();
            var graphicsPath = Path.Combine(staging, "graphics.pdf");
            progress?.Report(new ToolProgress(2, "正在分离文字与页面图形…"));
            await Task.Run(() => PdfOfficeLayout.WriteGraphicsOnlyPdf(input, graphicsPath, ct), ct);
            // WinRT's PdfDocument has no Dispose; loading the staged file directly keeps it locked
            // beyond conversion and prevents cleanup. Its source stream owns only memory here.
            using var graphicsBytes = new MemoryStream(await File.ReadAllBytesAsync(graphicsPath, ct));
            using var graphicsStream = graphicsBytes.AsRandomAccessStream();
            var graphics = await WindowsPdf.LoadFromStreamAsync(graphicsStream);
            WindowsPdf? original = null;
            var ocr = new OcrService();
            using var session = ocr.CreateSession(); // Lazy model initialization, shared across scanned pages.
            int ocrPages = 0;
            foreach (var page in source.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                // Scans contain no independent graphic/text layers. Do not place the scan behind OCR
                // text: that would leave the original words visible after the user edits them.
                bool ocrNeeded = PdfOfficeLayout.RequiresOcr(page)
                    && (page.GetImages().Any() || page.Letters.Count > 0);
                IReadOnlyList<PdfOfficeText> texts;
                string? background = null;
                if (ocrNeeded)
                {
                    ocrPages++;
                    original ??= await WindowsPdf.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(Path.GetFullPath(input)));
                    string rasterPath = Path.Combine(staging, $"scan-{page.Number}.png");
                    await RenderPageAsync(original, (uint)(page.Number - 1), rasterPath, 200, ct);
                    var recognized = await ocr.RecognizeLayoutAsync(rasterPath, session, ct);
                    double sx = page.Width / recognized.Width, sy = page.Height / recognized.Height;
                    texts = recognized.Blocks.Select(b => new PdfOfficeText(CleanXmlText(b.Text), b.X * sx,
                        b.Y * sy, b.Width * sx, b.Height * sy * 1.2, Math.Max(5, b.Height * sy * .85),
                        "Microsoft YaHei", false, false, "000000")).ToArray();
                    TryDelete(rasterPath);
                }
                else
                {
                    texts = PdfOfficeLayout.ExtractText(page);
                    background = Path.Combine(staging, $"graphics-{page.Number}.png");
                    await RenderPageAsync(graphics, (uint)(page.Number - 1), background, 200, ct);
                    if (texts.Count > 0)
                    {
                        original ??= await WindowsPdf.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(Path.GetFullPath(input)));
                        string visiblePage = Path.Combine(staging, $"visible-{page.Number}.png");
                        await RenderPageAsync(original, (uint)(page.Number - 1), visiblePage, 200, ct);
                        texts = PdfOfficeLayout.FilterVisibleText(texts, visiblePage, background, page.Width, page.Height, ct);
                        TryDelete(visiblePage);
                    }
                }
                pages.Add(new PdfOfficePage(page.Width, page.Height, texts, background));
                progress?.Report(new ToolProgress(5 + 85.0 * page.Number / source.NumberOfPages,
                    $"正在还原第 {page.Number}/{source.NumberOfPages} 页版式"));
            }
            string temporary = Path.Combine(staging, "layout." + format);
            if (format == "docx") PdfWordLayoutWriter.Write(temporary, pages, ct);
            else WriteLayoutPresentation(temporary, pages, ct);
            ct.ThrowIfCancellationRequested();
            string final = UniquePath(outputDir, Path.GetFileNameWithoutExtension(input) + "_layout", "." + format);
            File.Move(temporary, final);
            string message = "已保留页面版式；文字可编辑，图片与表格线条保留为图形";
            if (ocrPages > 0) message += $"；{ocrPages} 页为扫描件或特殊字体，已按文字位置识别，图形和样式需人工核对";
            progress?.Report(new ToolProgress(100, message));
            return final;
        }
        finally { CleanupStaging(staging); }
    }
}
