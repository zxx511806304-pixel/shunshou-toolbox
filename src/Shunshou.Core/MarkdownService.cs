using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Shunshou.Core;

public abstract record MarkdownBlock;
public sealed record MarkdownHeading(int Level, IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock;
public sealed record MarkdownParagraph(IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock;
public sealed record MarkdownCode(string? Language, string Code) : MarkdownBlock;
public sealed record MarkdownQuote(IReadOnlyList<MarkdownBlock> Children) : MarkdownBlock;
public sealed record MarkdownList(bool Ordered, IReadOnlyList<IReadOnlyList<MarkdownInline>> Items) : MarkdownBlock;
public sealed record MarkdownRule : MarkdownBlock;
public sealed record MarkdownTable(IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows) : MarkdownBlock;

public sealed record MarkdownInline(string Text, bool Bold = false, bool Italic = false, bool Code = false, string? Link = null, bool Image = false);

/// <summary>
/// Lightweight Markdown → block model → HTML / PDF pipeline. Supports ATX headings, bold, italic,
/// inline code, fenced code blocks, unordered/ordered lists, quotes, rules, links, tables and image
/// alt placeholders. All literal text is HTML-escaped and unsafe link schemes are dropped.
/// </summary>
public static class MarkdownService
{
    private const string Css = """
        body { font-family: "Microsoft YaHei UI", "Microsoft YaHei", "PingFang SC", sans-serif; margin: 26px 32px; color: #1f1f1f; line-height: 1.85; font-size: 15px; }
        h1, h2, h3, h4, h5, h6 { line-height: 1.4; margin: 1.1em 0 .55em; font-weight: 600; }
        h1 { font-size: 1.9em; border-bottom: 1px solid #e3e3e3; padding-bottom: .3em; }
        h2 { font-size: 1.55em; border-bottom: 1px solid #eeeeee; padding-bottom: .25em; }
        h3 { font-size: 1.3em; } h4 { font-size: 1.15em; } h5 { font-size: 1em; } h6 { font-size: .95em; color: #666; }
        p { margin: .6em 0; }
        code { font-family: Consolas, "Cascadia Mono", monospace; background: #f3f2f1; padding: 2px 5px; border-radius: 4px; font-size: .92em; }
        pre { background: #f6f6f6; border: 1px solid #e8e8e8; border-radius: 8px; padding: 12px 14px; overflow: auto; }
        pre code { background: none; padding: 0; }
        blockquote { margin: .8em 0; padding: .2em 1em; border-left: 4px solid #d0d0d0; color: #555; background: #fafafa; }
        ul, ol { margin: .5em 0; padding-left: 2em; }
        hr { border: none; border-top: 1px solid #ddd; margin: 1.4em 0; }
        table { border-collapse: collapse; margin: .8em 0; }
        th, td { border: 1px solid #d8d8d8; padding: 6px 12px; }
        th { background: #f3f2f1; }
        a { color: #0f6cbd; text-decoration: none; }
        .img-ph { display: inline-block; background: #f3f2f1; border: 1px dashed #c8c8c8; border-radius: 6px; color: #605e5c; padding: 4px 10px; font-size: .9em; }
        """;

    public static IReadOnlyList<MarkdownBlock> Parse(string markdown)
    {
        string[] lines = (markdown ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return ParseBlocks(lines, 0, lines.Length);
    }

    /// <summary>Full offline preview document with the Chinese typography stylesheet embedded.</summary>
    public static string RenderHtml(string markdown) =>
        "<!DOCTYPE html>\n<html lang=\"zh-CN\"><head><meta charset=\"utf-8\"/>\n<style>" + Css + "</style>\n</head><body>"
        + RenderBodyHtml(Parse(markdown)) + "</body></html>";

    internal static string RenderBodyHtml(IReadOnlyList<MarkdownBlock> blocks)
    {
        var html = new StringBuilder();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case MarkdownHeading heading:
                    html.Append("<h").Append(heading.Level).Append('>').Append(InlinesHtml(heading.Inlines)).Append("</h").Append(heading.Level).Append('>');
                    break;
                case MarkdownParagraph paragraph:
                    html.Append("<p>").Append(InlinesHtml(paragraph.Inlines)).Append("</p>");
                    break;
                case MarkdownCode code:
                    html.Append("<pre><code>").Append(EscapeHtml(code.Code)).Append("</code></pre>");
                    break;
                case MarkdownQuote quote:
                    html.Append("<blockquote>").Append(RenderBodyHtml(quote.Children)).Append("</blockquote>");
                    break;
                case MarkdownList list:
                    html.Append(list.Ordered ? "<ol>" : "<ul>");
                    foreach (var item in list.Items) html.Append("<li>").Append(InlinesHtml(item)).Append("</li>");
                    html.Append(list.Ordered ? "</ol>" : "</ul>");
                    break;
                case MarkdownRule:
                    html.Append("<hr/>");
                    break;
                case MarkdownTable table:
                    html.Append("<table><thead><tr>");
                    foreach (string cell in table.Header) html.Append("<th>").Append(EscapeHtml(cell)).Append("</th>");
                    html.Append("</tr></thead><tbody>");
                    foreach (var row in table.Rows)
                    {
                        html.Append("<tr>");
                        foreach (string cell in row) html.Append("<td>").Append(EscapeHtml(cell)).Append("</td>");
                        html.Append("</tr>");
                    }
                    html.Append("</tbody></table>");
                    break;
            }
        }
        return html.ToString();
    }

    private static string InlinesHtml(IReadOnlyList<MarkdownInline> inlines)
    {
        var html = new StringBuilder();
        foreach (var inline in inlines)
        {
            if (inline.Image)
            {
                string alt = EscapeHtml(string.IsNullOrWhiteSpace(inline.Text) ? "未命名图片" : inline.Text);
                html.Append("<span class=\"img-ph\">图片：").Append(alt).Append("（离线预览不加载）</span>");
                continue;
            }
            string content = inline.Code ? "<code>" + EscapeHtml(inline.Text) + "</code>" : EscapeHtml(inline.Text);
            if (inline.Bold) content = "<strong>" + content + "</strong>";
            if (inline.Italic) content = "<em>" + content + "</em>";
            if (inline.Link is { } url && IsSafeUrl(url))
                content = "<a href=\"" + EscapeHtml(url) + "\">" + content + "</a>";
            html.Append(content);
        }
        return html.ToString();
    }

    private static bool IsSafeUrl(string url)
    {
        if (url.StartsWith('#') || url.StartsWith("./") || url.StartsWith("../")) return true;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto";
    }

    internal static string EscapeHtml(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            switch (c)
            {
                case '&': escaped.Append("&amp;"); break;
                case '<': escaped.Append("&lt;"); break;
                case '>': escaped.Append("&gt;"); break;
                case '"': escaped.Append("&quot;"); break;
                case '\'': escaped.Append("&#39;"); break;
                default: escaped.Append(c); break;
            }
        }
        return escaped.ToString();
    }

    private static List<MarkdownBlock> ParseBlocks(string[] lines, int start, int end)
    {
        var blocks = new List<MarkdownBlock>();
        int i = start;
        while (i < end)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }
            string trimmed = line.Trim();
            if (TryFenceStart(trimmed, out char fence, out int fenceLength, out string? language))
            {
                var code = new StringBuilder();
                i++;
                while (i < end && !IsFenceEnd(lines[i].Trim(), fence, fenceLength))
                {
                    if (code.Length > 0) code.Append('\n');
                    code.Append(lines[i]);
                    i++;
                }
                if (i < end) i++; // consume the closing fence
                blocks.Add(new MarkdownCode(language, code.ToString()));
                continue;
            }
            if (TryHeading(line, out int level, out string? headingText))
            {
                blocks.Add(new MarkdownHeading(level, ParseInlines(headingText)));
                i++;
                continue;
            }
            if (IsRule(trimmed))
            {
                blocks.Add(new MarkdownRule());
                i++;
                continue;
            }
            if (trimmed.StartsWith('>'))
            {
                var inner = new List<string>();
                while (i < end && lines[i].TrimStart().StartsWith('>'))
                {
                    string content = lines[i].TrimStart()[1..];
                    if (content.StartsWith(' ')) content = content[1..];
                    inner.Add(content);
                    i++;
                }
                blocks.Add(new MarkdownQuote(ParseBlocks(inner.ToArray(), 0, inner.Count)));
                continue;
            }
            if (TryListItem(line, false, out _) || TryListItem(line, true, out _))
            {
                bool ordered = TryListItem(line, true, out _);
                var items = new List<IReadOnlyList<MarkdownInline>>();
                while (i < end && TryListItem(lines[i], ordered, out string? itemText))
                {
                    items.Add(ParseInlines(itemText));
                    i++;
                }
                blocks.Add(new MarkdownList(ordered, items));
                continue;
            }
            if (i + 1 < end && TrySplitTableRow(line, out var header) && IsTableDelimiter(lines[i + 1], header.Count))
            {
                var rows = new List<IReadOnlyList<string>>();
                i += 2;
                while (i < end && !string.IsNullOrWhiteSpace(lines[i]) && TrySplitTableRow(lines[i], out var row) && row.Count == header.Count)
                {
                    rows.Add(row);
                    i++;
                }
                blocks.Add(new MarkdownTable(header, rows));
                continue;
            }
            var paragraph = new StringBuilder();
            while (i < end && !string.IsNullOrWhiteSpace(lines[i]))
            {
                if (paragraph.Length > 0)
                {
                    string next = lines[i].Trim();
                    if (TryFenceStart(next, out _, out _, out _) || TryHeading(next, out _, out _) || IsRule(next)
                        || next.StartsWith('>') || TryListItem(next, false, out _) || TryListItem(next, true, out _)) break;
                }
                if (paragraph.Length > 0) paragraph.Append(' ');
                paragraph.Append(lines[i].Trim());
                i++;
            }
            blocks.Add(new MarkdownParagraph(ParseInlines(paragraph.ToString())));
        }
        return blocks;
    }

    private static bool TryFenceStart(string trimmed, out char fence, out int length, out string? language)
    {
        fence = '\0';
        length = 0;
        language = null;
        if (trimmed.Length < 3 || trimmed[0] is not ('`' or '~')) return false;
        char c = trimmed[0];
        int i = 0;
        while (i < trimmed.Length && trimmed[i] == c) i++;
        if (i < 3) return false;
        string info = trimmed[i..].Trim();
        if (c == '`' && info.Contains('`')) return false;
        fence = c;
        length = i;
        language = info.Length == 0 ? null : info;
        return true;
    }

    private static bool IsFenceEnd(string trimmed, char fence, int length)
    {
        int i = 0;
        while (i < trimmed.Length && trimmed[i] == fence) i++;
        return i >= length && trimmed[i..].Trim().Length == 0;
    }

    private static bool TryHeading(string line, out int level, out string text)
    {
        level = 0;
        text = "";
        string trimmed = line.TrimStart();
        int i = 0;
        while (i < trimmed.Length && trimmed[i] == '#') i++;
        if (i is < 1 or > 6 || i >= trimmed.Length || trimmed[i] is not (' ' or '\t')) return false;
        level = i;
        text = trimmed[(i + 1)..].Trim();
        int end = text.Length;
        while (end > 0 && text[end - 1] == '#') end--;
        if (end < text.Length && (end == 0 || text[end - 1] is ' ' or '\t')) text = text[..end].TrimEnd();
        return true;
    }

    private static bool IsRule(string trimmed)
    {
        if (trimmed.Length < 3 || trimmed[0] is not ('-' or '*' or '_')) return false;
        char marker = trimmed[0];
        int count = 0;
        foreach (char c in trimmed)
        {
            if (c == marker) count++;
            else if (c is not (' ' or '\t')) return false;
        }
        return count >= 3;
    }

    private static bool TryListItem(string line, bool ordered, out string text)
    {
        text = "";
        string trimmed = line.TrimStart();
        if (line.Length - trimmed.Length > 3) return false; // nested lists are out of scope
        if (!ordered)
        {
            if (trimmed.Length > 2 && trimmed[0] is '-' or '*' or '+' && trimmed[1] is ' ' or '\t')
            {
                text = trimmed[2..];
                return true;
            }
            return false;
        }
        int digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits])) digits++;
        if (digits is < 1 or > 9 || digits >= trimmed.Length || trimmed[digits] is not ('.' or ')')) return false;
        if (digits + 1 >= trimmed.Length || trimmed[digits + 1] is not (' ' or '\t')) return false;
        text = trimmed[(digits + 2)..];
        return true;
    }

    private static bool TrySplitTableRow(string line, out List<string> cells)
    {
        cells = [];
        string trimmed = line.Trim();
        if (!trimmed.Contains('|')) return false;
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|')) trimmed = trimmed[..^1];
        cells = trimmed.Split('|').Select(cell => cell.Trim()).ToList();
        return cells.Count > 0;
    }

    private static bool IsTableDelimiter(string line, int columns)
    {
        if (!TrySplitTableRow(line, out var cells) || cells.Count != columns) return false;
        foreach (string cell in cells)
        {
            string c = cell;
            if (c.StartsWith(':')) c = c[1..];
            if (c.EndsWith(':')) c = c[..^1];
            if (c.Length == 0 || c.Any(ch => ch != '-')) return false;
        }
        return true;
    }

    private static List<MarkdownInline> ParseInlines(string text)
    {
        var result = new List<MarkdownInline>();
        var literal = new StringBuilder();
        int i = 0;
        void Flush()
        {
            if (literal.Length > 0)
            {
                result.Add(new MarkdownInline(literal.ToString()));
                literal.Clear();
            }
        }
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '`')
            {
                int run = 1;
                while (i + run < text.Length && text[i + run] == '`') run++;
                int close = text.IndexOf(new string('`', run), i + run, StringComparison.Ordinal);
                if (close > i + run)
                {
                    Flush();
                    result.Add(new MarkdownInline(text[(i + run)..close], Code: true));
                    i = close + run;
                    continue;
                }
                literal.Append(c);
                i++;
                continue;
            }
            if (c == '!' && i + 1 < text.Length && text[i + 1] == '[')
            {
                if (TryParseLink(text, i + 1, out string? alt, out string? source, out int end))
                {
                    Flush();
                    result.Add(new MarkdownInline(alt, Link: source, Image: true));
                    i = end;
                    continue;
                }
                literal.Append(c);
                i++;
                continue;
            }
            if (c == '[')
            {
                if (TryParseLink(text, i, out string? label, out string? url, out int end))
                {
                    Flush();
                    foreach (var inner in ParseInlines(label)) result.Add(inner with { Link = url });
                    i = end;
                    continue;
                }
                literal.Append(c);
                i++;
                continue;
            }
            if (c is '*' or '_')
            {
                int run = 1;
                while (i + run < text.Length && text[i + run] == c) run++;
                if (run >= 2)
                {
                    int close = text.IndexOf(new string(c, 2), i + 2, StringComparison.Ordinal);
                    if (close > i + 2)
                    {
                        Flush();
                        foreach (var inner in ParseInlines(text[(i + 2)..close])) result.Add(inner with { Bold = true });
                        i = close + 2;
                        continue;
                    }
                }
                int singleClose = text.IndexOf(c, i + 1);
                if (singleClose > i + 1)
                {
                    Flush();
                    foreach (var inner in ParseInlines(text[(i + 1)..singleClose])) result.Add(inner with { Italic = true });
                    i = singleClose + 1;
                    continue;
                }
                literal.Append(c);
                i++;
                continue;
            }
            literal.Append(c);
            i++;
        }
        Flush();
        return result;
    }

    private static bool TryParseLink(string text, int open, out string label, out string? url, out int end)
    {
        label = "";
        url = null;
        end = open;
        int close = text.IndexOf(']', open + 1);
        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(') return false;
        int closeParen = text.IndexOf(')', close + 2);
        if (closeParen < 0) return false;
        label = text[(open + 1)..close];
        string target = text[(close + 2)..closeParen].Trim();
        int space = target.IndexOfAny([' ', '\t']); // an optional "title" may follow the URL
        if (space > 0) target = target[..space];
        url = target;
        end = closeParen + 1;
        return true;
    }

    public static Task<string> ExportPdfAsync(string markdown, string outputPath, CancellationToken ct = default) => Task.Run(() =>
    {
        outputPath = Path.GetFullPath(outputPath ?? throw new ArgumentNullException(nameof(outputPath)));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        PdfService.EnsureWindowsFonts();
        var blocks = Parse(markdown ?? "");
        string temporary = Path.Combine(Path.GetDirectoryName(outputPath)!, ".markdown-pdf-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var writer = new PdfWriter())
            {
                foreach (var block in blocks) WriteBlock(writer, block, ct);
                ct.ThrowIfCancellationRequested();
                writer.Save(temporary);
            }
            File.Move(temporary, outputPath, overwrite: true);
            return outputPath;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }, ct);

    private static readonly XBrush LinkBrush = new XSolidBrush(XColor.FromArgb(255, 15, 108, 189));
    private static readonly XBrush QuoteBrush = new XSolidBrush(XColor.FromArgb(255, 85, 85, 85));
    private static readonly XBrush CodeBackground = new XSolidBrush(XColor.FromArgb(255, 246, 246, 246));

    private static double HeadingSize(int level) => level switch { 1 => 22, 2 => 18, 3 => 15, 4 => 13, 5 => 12, _ => 11.5 };

    private static void WriteBlock(PdfWriter writer, MarkdownBlock block, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        switch (block)
        {
            case MarkdownHeading heading:
                WriteRuns(writer, heading.Inlines, HeadingSize(heading.Level), baseBold: true, spacingFactor: 0.9);
                break;
            case MarkdownParagraph paragraph:
                WriteRuns(writer, paragraph.Inlines, 11, baseBold: false, spacingFactor: 0.5);
                break;
            case MarkdownCode code:
                WriteCode(writer, code.Code, ct);
                break;
            case MarkdownQuote quote:
                double indent = writer.Indent;
                var brush = writer.TextBrush;
                writer.Indent += 18;
                writer.TextBrush = QuoteBrush;
                foreach (var child in quote.Children) WriteBlock(writer, child, ct);
                writer.Indent = indent;
                writer.TextBrush = brush;
                break;
            case MarkdownList list:
                for (int index = 0; index < list.Items.Count; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    writer.Indent += 24;
                    writer.EnsureSpace(11 * 1.5);
                    string marker = list.Ordered ? $"{index + 1}." : "•";
                    var markerFont = writer.Font(11, false, false);
                    writer.DrawMarker(marker, markerFont, writer.Measure(marker, markerFont).Width);
                    WriteRuns(writer, list.Items[index], 11, baseBold: false, spacingFactor: 0.15);
                    writer.Indent -= 24;
                }
                writer.EndBlock(6);
                break;
            case MarkdownRule:
                writer.DrawRule();
                break;
            case MarkdownTable table:
                WriteTable(writer, table, ct);
                break;
        }
    }

    private static void WriteRuns(PdfWriter writer, IReadOnlyList<MarkdownInline> inlines, double fontSize, bool baseBold, double spacingFactor)
    {
        double lineHeight = fontSize * 1.5;
        bool wroteAny = false;
        foreach (var inline in inlines)
        {
            var font = writer.Font(fontSize, baseBold || inline.Bold, inline.Italic);
            string text = inline.Image ? "[图片：" + (string.IsNullOrWhiteSpace(inline.Text) ? "未命名图片" : inline.Text) + "]" : inline.Text;
            var brush = inline.Link is not null ? LinkBrush : writer.TextBrush;
            foreach (string token in Tokenize(text))
            {
                double width = writer.Measure(token, font).Width;
                if (writer.CursorX > writer.LeftEdge && writer.CursorX + width > writer.RightEdge)
                    writer.NewLine(lineHeight);
                writer.EnsureSpace(lineHeight);
                writer.DrawToken(token, font, width, brush);
                wroteAny = true;
            }
        }
        writer.EndBlock(lineHeight * spacingFactor);
        if (!wroteAny) writer.EnsureSpace(lineHeight);
    }

    private static void WriteCode(PdfWriter writer, string code, CancellationToken ct)
    {
        var font = writer.Font(10, false, false);
        double lineHeight = font.Height * 1.35;
        foreach (string raw in code.Split('\n'))
        {
            ct.ThrowIfCancellationRequested();
            foreach (string line in writer.Wrap(raw.Length == 0 ? " " : raw, font, writer.UsableWidth - 16))
            {
                writer.EnsureSpace(lineHeight);
                writer.DrawCodeLine(line, font, lineHeight, CodeBackground);
            }
        }
        writer.EndBlock(8);
    }

    private static void WriteTable(PdfWriter writer, MarkdownTable table, CancellationToken ct)
    {
        int columns = table.Header.Count;
        if (columns == 0) return;
        double[] widths = Enumerable.Range(0, columns).Select(_ => writer.UsableWidth / columns).ToArray();
        WriteTableRow(writer, table.Header, widths, header: true);
        foreach (var row in table.Rows)
        {
            ct.ThrowIfCancellationRequested();
            WriteTableRow(writer, row, widths, header: false);
        }
        writer.EndBlock(8);
    }

    private static void WriteTableRow(PdfWriter writer, IReadOnlyList<string> cells, double[] widths, bool header)
    {
        const double padding = 4;
        var font = writer.Font(10.5, header, false);
        var wrapped = new List<string[]>();
        double rowHeight = 0;
        for (int c = 0; c < widths.Length; c++)
        {
            string[] lines = writer.Wrap(c < cells.Count ? cells[c] : "", font, widths[c] - padding * 2);
            wrapped.Add(lines);
            rowHeight = Math.Max(rowHeight, lines.Length * font.Height * 1.35 + padding * 2);
        }
        writer.EnsureSpace(rowHeight);
        writer.DrawTableRow(wrapped, widths, rowHeight, font, padding);
    }

    /// <summary>CJK characters wrap anywhere; Latin text wraps on word boundaries.</summary>
    private static IEnumerable<string> Tokenize(string text)
    {
        var word = new StringBuilder();
        foreach (char c in text)
        {
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

    /// <summary>Stateful A4 writer mirroring the Word → PDF layout: cursor, page breaks, fonts and measurement.</summary>
    private sealed class PdfWriter : IDisposable
    {
        private const double Margin = 56; // points
        private const string FontFamily = "SimHei"; // Resolved by PdfService.ShunshouFontResolver; see its comment about .ttc fonts.
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

        public double Indent { get; set; }
        public double CursorX { get; private set; } = Margin;
        public XBrush TextBrush { get; set; } = XBrushes.Black;
        public double LeftEdge => Margin + Indent;
        public double RightEdge => _page.Width.Point - Margin;
        public double UsableWidth => RightEdge - LeftEdge;

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

        private void NewPage()
        {
            _graphics.Dispose();
            _page = _document.AddPage();
            _page.Size = PdfSharp.PageSize.A4;
            _graphics = XGraphics.FromPdfPage(_page);
            CursorX = LeftEdge;
            _y = Margin;
        }

        public void NewLine(double lineHeight)
        {
            CursorX = LeftEdge;
            _y += lineHeight;
            EnsureSpace(lineHeight);
        }

        public void EndBlock(double spacing)
        {
            CursorX = LeftEdge;
            _y += spacing;
        }

        public void DrawToken(string token, XFont font, double width, XBrush brush)
        {
            if (string.IsNullOrWhiteSpace(token) && CursorX == LeftEdge) { CursorX += width; return; }
            _graphics.DrawString(token, font, brush, CursorX, _y + font.Height * 0.85);
            CursorX += width;
        }

        public void DrawMarker(string marker, XFont font, double width) =>
            _graphics.DrawString(marker, font, TextBrush, LeftEdge - width - 10, _y + font.Height * 0.85);

        public void DrawCodeLine(string text, XFont font, double lineHeight, XBrush background)
        {
            _graphics.DrawRectangle(background, LeftEdge - 6, _y, UsableWidth + 12, lineHeight);
            _graphics.DrawString(text, font, XBrushes.Black, LeftEdge, _y + font.Height * 0.85);
            _y += lineHeight;
            CursorX = LeftEdge;
        }

        public void DrawRule()
        {
            EnsureSpace(14);
            _graphics.DrawLine(XPens.LightGray, LeftEdge, _y + 7, RightEdge, _y + 7);
            _y += 14;
            CursorX = LeftEdge;
        }

        public string[] Wrap(string text, XFont font, double width)
        {
            if (text.Length == 0) return [""];
            var lines = new List<string>();
            var current = new StringBuilder();
            foreach (string token in Tokenize(text))
            {
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
            double x = LeftEdge;
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
            CursorX = LeftEdge;
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
