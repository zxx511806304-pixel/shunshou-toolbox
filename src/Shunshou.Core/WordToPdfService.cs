using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using A = DocumentFormat.OpenXml.Drawing;
using W = DocumentFormat.OpenXml.Wordprocessing;
using WP = DocumentFormat.OpenXml.Drawing.Wordprocessing;

namespace Shunshou.Core;

/// <summary>
/// Lightweight DOCX → PDF conversion: paragraphs, heading levels 1–3, bold/italic runs,
/// simple bordered tables and inline images. Complex layout (text boxes, headers/footers,
/// floating objects) is intentionally approximated.
/// </summary>
public sealed class WordToPdfService
{
    private const double Margin = 56; // points
    private const string FontFamily = "SimHei"; // Resolved by PdfService.ShunshouFontResolver; see its comment about .ttc fonts.

    public Task<string> ConvertAsync(string input, string outputDir,
        IProgress<ToolProgress>? progress = null, CancellationToken ct = default) => Task.Run(() =>
    {
        if (!File.Exists(input)) throw new FileNotFoundException("找不到 Word 文件。", input);
        if (!Path.GetExtension(input).Equals(".docx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Word 转 PDF 目前支持 .docx 文件。", nameof(input));
        ct.ThrowIfCancellationRequested();
        PdfService.EnsureWindowsFonts();
        Directory.CreateDirectory(outputDir);
        string staging = PdfService.CreateStaging(outputDir);
        try
        {
            using var source = WordprocessingDocument.Open(input, false);
            var main = source.MainDocumentPart ?? throw new InvalidDataException("无法读取该 Word 文档。");
            var body = main.Document?.Body ?? throw new InvalidDataException("该 Word 文档没有正文。");
            var headings = HeadingLevels(main);
            using var writer = new PdfWriter();
            var elements = body.ChildElements.Where(e => e is W.Paragraph or W.Table).ToList();
            if (elements.Count == 0) throw new InvalidDataException("该 Word 文档没有可转换的内容。");
            for (int index = 0; index < elements.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                switch (elements[index])
                {
                    case W.Paragraph paragraph:
                        WriteParagraph(writer, main, paragraph, headings, ct);
                        break;
                    case W.Table table:
                        WriteTable(writer, table, ct);
                        break;
                }
                progress?.Report(new ToolProgress(10 + 85.0 * (index + 1) / elements.Count,
                    $"正在排版第 {index + 1}/{elements.Count} 个段落"));
            }
            string temporary = Path.Combine(staging, "converted.pdf");
            writer.Save(temporary);
            ct.ThrowIfCancellationRequested();
            string final = PdfService.UniquePath(outputDir, Path.GetFileNameWithoutExtension(input), ".pdf");
            File.Move(temporary, final);
            progress?.Report(new ToolProgress(100, $"已生成 PDF，共 {writer.PageCount} 页"));
            return final;
        }
        finally { PdfService.CleanupStaging(staging); }
    }, ct);

    /// <summary>Maps style id → heading level using the style name ("heading 1"…), so Chinese Word files work too.</summary>
    private static Dictionary<string, int> HeadingLevels(MainDocumentPart main)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var styles = main.StyleDefinitionsPart?.Styles;
        if (styles == null) return map;
        foreach (var style in styles.Elements<W.Style>())
        {
            string? id = style.StyleId?.Value;
            string? name = style.StyleName?.Val?.Value;
            if (id == null || name == null) continue;
            for (int level = 1; level <= 3; level++)
                if (name.Equals("heading " + level, StringComparison.OrdinalIgnoreCase)) { map[id] = level; break; }
        }
        return map;
    }

    private static void WriteParagraph(PdfWriter writer, MainDocumentPart main, W.Paragraph paragraph,
        IReadOnlyDictionary<string, int> headings, CancellationToken ct)
    {
        int heading = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value is { } id && headings.TryGetValue(id, out int level) ? level : 0;
        double fontSize = heading switch { 1 => 20, 2 => 16, 3 => 13, _ => 11 };
        double lineHeight = fontSize * 1.5;
        bool wroteAny = false;
        foreach (var item in ParagraphItems(main, paragraph))
        {
            ct.ThrowIfCancellationRequested();
            if (item.Image != null)
            {
                writer.EnsureSpace(item.ImageHeight);
                writer.DrawImage(item.Image, item.ImageWidth, item.ImageHeight);
                wroteAny = true;
                continue;
            }
            bool bold = heading > 0 || item.Bold;
            var font = writer.Font(fontSize, bold, item.Italic);
            foreach (string token in Tokenize(item.Text))
            {
                var size = writer.Measure(token, font);
                double width = size.Width;
                if (writer.CursorX > Margin && writer.CursorX + width > writer.RightEdge)
                    writer.NewLine(lineHeight);
                writer.EnsureSpace(lineHeight);
                writer.DrawText(token, font, width);
                wroteAny = true;
            }
        }
        writer.NewParagraph(lineHeight * (heading > 0 ? 0.9 : 0.5));
        if (!wroteAny) writer.EnsureSpace(lineHeight);
    }

    private sealed record ParagraphItem(string Text, bool Bold, bool Italic, XImage? Image, double ImageWidth, double ImageHeight);

    private static IEnumerable<ParagraphItem> ParagraphItems(MainDocumentPart main, W.Paragraph paragraph)
    {
        foreach (var run in paragraph.Elements<W.Run>())
        {
            bool bold = run.RunProperties?.Bold != null;
            bool italic = run.RunProperties?.Italic != null;
            var drawing = run.Descendants<W.Drawing>().FirstOrDefault();
            if (drawing != null && TryLoadImage(main, drawing, out var image, out double w, out double h))
                yield return new ParagraphItem("", false, false, image, w, h);
            string text = string.Concat(run.Descendants<W.Text>().Select(t => t.Text));
            if (text.Length > 0) yield return new ParagraphItem(text, bold, italic, null, 0, 0);
            if (run.Descendants<W.Break>().Any(b => b.Type?.Value == W.BreakValues.Page))
                yield return new ParagraphItem("\f", false, false, null, 0, 0);
        }
    }

    private static bool TryLoadImage(MainDocumentPart main, W.Drawing drawing, out XImage image, out double width, out double height)
    {
        image = null!;
        width = height = 0;
        var blip = drawing.Descendants<A.Blip>().FirstOrDefault();
        string? embed = blip?.Embed?.Value;
        if (embed == null || main.GetPartById(embed) is not ImagePart part) return false;
        try
        {
            var bytes = new MemoryStream();
            using (var stream = part.GetStream()) stream.CopyTo(bytes);
            bytes.Position = 0;
            image = XImage.FromStream(bytes);
        }
        catch (Exception) { return false; } // Unreadable or unsupported image: skip it, keep the text.
        var extent = drawing.Descendants<WP.Extent>().FirstOrDefault();
        if (extent?.Cx is { } cx && cx > 0 && extent?.Cy is { } cy && cy > 0)
        {
            width = (long)cx / 914400.0 * 72; // EMU → points
            height = (long)cy / 914400.0 * 72;
        }
        else
        {
            width = image.PixelWidth * 72.0 / 96.0;
            height = image.PixelHeight * 72.0 / 96.0;
        }
        return true;
    }

    private static void WriteTable(PdfWriter writer, W.Table table, CancellationToken ct)
    {
        var rows = table.Elements<W.TableRow>().ToList();
        if (rows.Count == 0) return;
        int columns = rows.Max(r => r.Elements<W.TableCell>().Count());
        if (columns == 0) return;
        double[] widths = ColumnWidths(table, writer.UsableWidth, columns);
        const double padding = 4;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var cells = row.Elements<W.TableCell>().ToList();
            // Wrap every cell first so the row height fits the tallest cell.
            var wrapped = new List<string[]>();
            double rowHeight = 0;
            var font = writer.Font(10.5, false, false);
            for (int c = 0; c < columns; c++)
            {
                string text = c < cells.Count
                    ? string.Concat(cells[c].Elements<W.Paragraph>().SelectMany(p => p.Elements<W.Run>()).SelectMany(r => r.Descendants<W.Text>()).Select(t => t.Text))
                    : "";
                var lines = writer.Wrap(text, font, widths[c] - padding * 2);
                wrapped.Add(lines);
                rowHeight = Math.Max(rowHeight, lines.Length * font.Height * 1.35 + padding * 2);
            }
            writer.EnsureSpace(rowHeight);
            writer.DrawTableRow(wrapped, widths, rowHeight, font, padding);
        }
        writer.NewParagraph(8);
    }

    private static double[] ColumnWidths(W.Table table, double usableWidth, int columns)
    {
        var grid = table.Elements<W.TableGrid>().FirstOrDefault();
        var declared = grid?.Elements<W.GridColumn>()
            .Select(g => double.TryParse(g.Width?.Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double w) ? w : 0).ToList();
        if (declared is { Count: > 0 } && declared.Count == columns && declared.All(w => w > 0))
        {
            double total = declared.Sum();
            return declared.Select(w => w / total * usableWidth).ToArray();
        }
        return Enumerable.Range(0, columns).Select(_ => usableWidth / columns).ToArray();
    }

    /// <summary>CJK characters wrap anywhere; Latin text wraps on word boundaries.</summary>
    private static IEnumerable<string> Tokenize(string text)
    {
        if (text == "\f") { yield return "\f"; yield break; }
        var word = new System.Text.StringBuilder();
        foreach (char c in text)
        {
            if (c == '\f') { if (word.Length > 0) { yield return word.ToString(); word.Clear(); } yield return "\f"; continue; }
            bool cjk = c >= '⺀';
            if (cjk || char.IsWhiteSpace(c))
            {
                if (word.Length > 0) { yield return word.ToString(); word.Clear(); }
                yield return c.ToString();
            }
            else word.Append(c);
        }
        if (word.Length > 0) yield return word.ToString();
    }

    /// <summary>Stateful A4 writer: cursor position, page breaks, fonts and measurement.</summary>
    private sealed class PdfWriter : IDisposable
    {
        private readonly PdfDocument _document = new();
        private readonly XPdfFontOptions _options = new(PdfFontEmbedding.EmbedCompleteFontFile);
        private PdfPage _page;
        private XGraphics _graphics;
        private double _y = Margin;
        private bool _disposed;

        public PdfWriter()
        {
            _page = _document.AddPage();
            _page.Size = PdfSharp.PageSize.A4;
            _graphics = XGraphics.FromPdfPage(_page);
        }

        public int PageCount => _document.PageCount;
        public double CursorX { get; private set; } = Margin;
        public double RightEdge => _page.Width.Point - Margin;
        public double UsableWidth => _page.Width.Point - Margin * 2;

        public XFont Font(double size, bool bold, bool italic)
        {
            var style = (bold ? XFontStyleEx.Bold : XFontStyleEx.Regular) | (italic ? XFontStyleEx.Italic : XFontStyleEx.Regular);
            return new XFont(FontFamily, size, style, _options);
        }

        public XSize Measure(string text, XFont font) => _graphics.MeasureString(text, font);

        public void EnsureSpace(double height)
        {
            if (_y + height > _page.Height.Point - Margin) NewPage();
        }

        public void NewPage()
        {
            _graphics.Dispose();
            _page = _document.AddPage();
            _page.Size = PdfSharp.PageSize.A4;
            _graphics = XGraphics.FromPdfPage(_page);
            CursorX = Margin;
            _y = Margin;
        }

        public void NewLine(double lineHeight)
        {
            CursorX = Margin;
            _y += lineHeight;
            EnsureSpace(lineHeight);
        }

        public void NewParagraph(double spacing)
        {
            CursorX = Margin;
            _y += spacing;
        }

        public void DrawText(string token, XFont font, double width)
        {
            if (token == "\f") { NewPage(); return; }
            if (string.IsNullOrWhiteSpace(token) && CursorX == Margin) { CursorX += width; return; }
            _graphics.DrawString(token, font, XBrushes.Black, CursorX, _y + font.Height * 0.85);
            CursorX += width;
        }

        public void DrawImage(XImage image, double width, double height)
        {
            double maxWidth = UsableWidth;
            if (width > maxWidth) { double scale = maxWidth / width; width = maxWidth; height *= scale; }
            double maxHeight = _page.Height.Point - Margin * 2;
            if (height > maxHeight) { double scale = maxHeight / height; height = maxHeight; width *= scale; }
            EnsureSpace(height);
            _graphics.DrawImage(image, Margin + (UsableWidth - width) / 2, _y, width, height);
            _y += height + 6;
            CursorX = Margin;
        }

        public string[] Wrap(string text, XFont font, double width)
        {
            if (text.Length == 0) return [""];
            var lines = new List<string>();
            var current = new System.Text.StringBuilder();
            foreach (string token in Tokenize(text))
            {
                if (token == "\f") continue;
                if (current.Length > 0 && Measure(current.ToString() + token, font).Width > width)
                {
                    lines.Add(current.ToString().TrimEnd());
                    current.Clear();
                    if (string.IsNullOrWhiteSpace(token)) continue;
                }
                current.Append(token);
            }
            if (current.Length > 0) lines.Add(current.ToString().TrimEnd());
            return lines.Count == 0 ? [""] : lines.ToArray();
        }

        public void DrawTableRow(List<string[]> cells, double[] widths, double rowHeight, XFont font, double padding)
        {
            double x = Margin;
            double lineHeight = font.Height * 1.35;
            for (int c = 0; c < widths.Length; c++)
            {
                _graphics.DrawRectangle(XPens.Gray, x, _y, widths[c], rowHeight);
                double textY = _y + padding + font.Height * 0.9;
                foreach (string line in cells[c])
                {
                    if (textY > _y + rowHeight - padding) break;
                    _graphics.DrawString(line, font, XBrushes.Black, x + padding, textY);
                    textY += lineHeight;
                }
                x += widths[c];
            }
            _y += rowHeight;
            CursorX = Margin;
        }

        public void Save(string path)
        {
            if (!_disposed) _graphics.Dispose();
            _document.Save(path);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _graphics.Dispose();
            _document.Dispose();
        }
    }
}
