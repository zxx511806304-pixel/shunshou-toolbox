using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace Shunshou.Core;

internal sealed record PdfOfficeText(string Text, double X, double Y, double Width, double Height,
    double FontSize, string FontFamily, bool Bold, bool Italic, string ColorHex, double Rotation = 0);

internal sealed record PdfOfficePage(double Width, double Height, IReadOnlyList<PdfOfficeText> Texts,
    string? BackgroundImage);

/// <summary>
/// Separates ordinary native text from page artwork. The background keeps pictures, paths and table
/// rules; visible text is supplied as independent Office text objects. Text used as a clipping mask
/// stays in the artwork because replacing its glyph outline would change the graphics it clips.
/// </summary>
internal static class PdfOfficeLayout
{
    public static bool RequiresOcr(Page page)
    {
        var visible = page.Letters.Where(IsEditable).ToArray();
        return visible.Length == 0 || visible.Any(l => IsSuspect(l.Value));
    }

    public static IReadOnlyList<PdfOfficeText> ExtractText(Page page)
    {
        var visible = page.Letters.Where(IsEditable).ToArray();
        if (visible.Any(l => IsSuspect(l.Value)))
            throw new InvalidDataException($"第 {page.Number} 页的字体编码无法还原，需要使用文字识别。");
        var letters = visible
            .GroupBy(l => (l.Value, X: Math.Round(l.StartBaseLine.X, 2), Y: Math.Round(l.StartBaseLine.Y, 2)))
            .Select(g => g.First()).ToArray();
        var words = NearestNeighbourWordExtractor.Instance.GetWords(letters).ToList();
        var result = new List<PdfOfficeText>();
        if (words.Count == 0) return result;

        foreach (var paragraph in DocstrumBoundingBoxes.Instance.GetBlocks(words))
        foreach (var line in paragraph.TextLines)
        {
            var run = new List<Letter>();
            var text = new StringBuilder();
            TextStyle? style = null;
            bool newWord = false;
            foreach (var word in line.Words)
            {
                foreach (var letter in word.Letters)
                {
                    var next = StyleFor(letter);
                    if (run.Count > 0 && (next != style || !SameBaseline(run[^1], letter))) Flush();
                    if (run.Count > 0 && newWord && NeedsSpace(text, letter.Value)) text.Append(' ');
                    style = next;
                    run.Add(letter);
                    text.Append(CleanXml(letter.Value));
                    newWord = false;
                }
                newWord = true;
            }
            Flush();

            void Flush()
            {
                if (run.Count == 0 || style is null) return;
                string value = text.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    // Loose font bounds include ascent/descent. Ink-only bounds would put text
                    // too low in Office and vary the vertical position between capitals and commas.
                    double left = run.Min(l => Math.Min(l.StartBaseLine.X, l.GlyphRectangleLoose.Left));
                    double top = page.Height - run.Max(l => l.GlyphRectangleLoose.Top);
                    double right = run.Max(l => Math.Max(l.EndBaseLine.X, l.GlyphRectangleLoose.Right));
                    double bottom = page.Height - run.Min(l => l.GlyphRectangleLoose.Bottom);
                    if (right <= left || bottom <= top)
                    {
                        left = run.Min(l => l.BoundingBox.Left);
                        right = run.Max(l => l.BoundingBox.Right);
                        top = page.Height - run.Max(l => l.BoundingBox.Top);
                        bottom = page.Height - run.Min(l => l.BoundingBox.Bottom);
                    }
                    if (Math.Abs(style.Rotation) > 0.01)
                    {
                        // Office rotates the unrotated rectangle around its center. Supplying the
                        // PDF's already-rotated axis-aligned box would rotate its geometry twice.
                        double radians = style.Rotation * Math.PI / 180;
                        double cos = Math.Cos(radians), sin = Math.Sin(radians);
                        var projected = run.SelectMany(l => new[] { l.GlyphRectangleLoose.TopLeft,
                            l.GlyphRectangleLoose.TopRight, l.GlyphRectangleLoose.BottomLeft,
                            l.GlyphRectangleLoose.BottomRight }).Select(p =>
                            (U: p.X * cos + (page.Height - p.Y) * sin,
                             V: -p.X * sin + (page.Height - p.Y) * cos)).ToArray();
                        double minU = projected.Min(p => p.U), maxU = projected.Max(p => p.U);
                        double minV = projected.Min(p => p.V), maxV = projected.Max(p => p.V);
                        double width = maxU - minU, height = Math.Max(style.Size, maxV - minV);
                        double centerX = (minU + maxU) / 2 * cos - (minV + maxV) / 2 * sin;
                        double centerY = (minU + maxU) / 2 * sin + (minV + maxV) / 2 * cos;
                        left = centerX - width / 2; right = left + width;
                        top = centerY - height / 2; bottom = top + height;
                    }
                    result.Add(new PdfOfficeText(value, left, top, Math.Max(1, right - left),
                        Math.Max(style.Size, bottom - top), style.Size, style.Font, style.Bold,
                        style.Italic, style.Color, style.Rotation));
                }
                run.Clear(); text.Clear(); style = null;
            }
        }
        return result.OrderBy(t => t.Y).ThenBy(t => t.X).ToArray();
    }

    /// <summary>
    /// Native text can have an ordinary paint mode but be hidden by a later opaque image. Only
    /// include a text object if suppressing native text changes visible pixels in its bounds.
    /// Partially covered lines still require review; this does not claim to reconstruct all z-order.
    /// </summary>
    public static IReadOnlyList<PdfOfficeText> FilterVisibleText(IReadOnlyList<PdfOfficeText> texts,
        string originalPng, string graphicsPng, double pageWidth, double pageHeight, CancellationToken ct = default)
    {
        if (texts.Count == 0) return texts;
        using var original = SKBitmap.Decode(originalPng) ?? throw new InvalidDataException("无法读取原始PDF页面。");
        using var graphics = SKBitmap.Decode(graphicsPng) ?? throw new InvalidDataException("无法读取PDF图形页面。");
        if (original.Width != graphics.Width || original.Height != graphics.Height)
            throw new InvalidDataException("PDF图形层与原页面尺寸不一致，不能可靠地还原版式。");
        double sx = original.Width / pageWidth, sy = original.Height / pageHeight;
        var visible = new List<PdfOfficeText>(texts.Count);
        foreach (var text in texts)
        {
            ct.ThrowIfCancellationRequested();
            double radians = text.Rotation * Math.PI / 180;
            double width = Math.Abs(text.Width * Math.Cos(radians)) + Math.Abs(text.Height * Math.Sin(radians));
            double height = Math.Abs(text.Width * Math.Sin(radians)) + Math.Abs(text.Height * Math.Cos(radians));
            double centerX = text.X + text.Width / 2, centerY = text.Y + text.Height / 2;
            int left = Math.Clamp((int)Math.Floor((centerX - width / 2) * sx), 0, original.Width);
            int right = Math.Clamp((int)Math.Ceiling((centerX + width / 2) * sx), 0, original.Width);
            int top = Math.Clamp((int)Math.Floor((centerY - height / 2) * sy), 0, original.Height);
            int bottom = Math.Clamp((int)Math.Ceiling((centerY + height / 2) * sy), 0, original.Height);
            int differences = 0;
            for (int y = top; y < bottom && differences < 3; y++)
            for (int x = left; x < right && differences < 3; x++)
            {
                var before = original.GetPixel(x, y);
                var after = graphics.GetPixel(x, y);
                if (Math.Abs(before.Red - after.Red) + Math.Abs(before.Green - after.Green)
                    + Math.Abs(before.Blue - after.Blue) > 3) differences++;
            }
            if (differences >= 3) visible.Add(text);
        }
        return visible;
    }

    private static bool IsEditable(Letter letter) => (int)letter.RenderingMode is >= 0 and <= 2;

    private static bool IsSuspect(string value) => value.Any(c => c == '\uFFFD'
        || char.GetUnicodeCategory(c) == UnicodeCategory.PrivateUse
        || (char.IsControl(c) && c is not '\r' and not '\n' and not '\t'));

    private static string CleanXml(string value) => new(value.Where(c => c is '\t' or '\n' or '\r'
        || c >= ' ' && c != '\uFFFE' && c != '\uFFFF').ToArray());

    private static bool NeedsSpace(StringBuilder current, string next) => current.Length > 0 && next.Length > 0
        && !char.IsWhiteSpace(current[^1]) && !char.IsWhiteSpace(next[0])
        && !(IsCjk(current[^1]) && IsCjk(next[0]));

    private static bool IsCjk(char value) => value is >= '\u2E80' and <= '\u9FFF'
        or >= '\uF900' and <= '\uFAFF';

    private static bool SameBaseline(Letter left, Letter right)
    {
        double dx = left.EndBaseLine.X - left.StartBaseLine.X;
        double dy = left.EndBaseLine.Y - left.StartBaseLine.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.01) return true;
        double cross = (right.StartBaseLine.X - left.StartBaseLine.X) * dy
            - (right.StartBaseLine.Y - left.StartBaseLine.Y) * dx;
        return Math.Abs(cross) / length <= Math.Max(0.8, left.PointSize * 0.1);
    }

    private sealed record TextStyle(string Font, double Size, bool Bold, bool Italic, string Color, double Rotation);

    private static TextStyle StyleFor(Letter letter)
    {
        string name = Regex.Replace(letter.FontName ?? "Arial", @"^[A-Z]{6}\+", "");
        bool bold = letter.FontDetails.IsBold || Regex.IsMatch(name, "bold|black|heavy|demi", RegexOptions.IgnoreCase);
        bool italic = letter.FontDetails.IsItalic || Regex.IsMatch(name, "italic|oblique", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"[-,](BoldItalic|BoldOblique|Bold|Italic|Oblique|Regular|Roman)(MT)?$", "", RegexOptions.IgnoreCase);
        name = name switch
        {
            "ArialMT" => "Arial", "Arial-BoldMT" => "Arial", "TimesNewRomanPSMT" => "Times New Roman",
            "TimesNewRomanPS" => "Times New Roman", "TimesNewRomanPS-BoldMT" => "Times New Roman",
            "Helvetica" => "Arial", "Times" => "Times New Roman", "Courier" => "Courier New",
            "MicrosoftYaHei" or "MicrosoftYaHeiUI" => "Microsoft YaHei", "SimSun" => "SimSun",
            _ => name
        };
        if (string.IsNullOrWhiteSpace(name)) name = "Arial";
        string color = "000000";
        try
        {
            var (r, g, b) = letter.Color.ToRGBValues();
            static int Channel(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 255);
            color = $"{Channel(r):X2}{Channel(g):X2}{Channel(b):X2}";
        }
        catch (NotSupportedException) { }
        double angle = Math.Atan2(-(letter.EndBaseLine.Y - letter.StartBaseLine.Y),
            letter.EndBaseLine.X - letter.StartBaseLine.X) * 180 / Math.PI;
        double size = double.IsFinite(letter.PointSize) && letter.PointSize > 0 ? letter.PointSize : letter.BoundingBox.Height;
        return new TextStyle(name, Math.Round(Math.Clamp(size, 1, 400), 2), bold, italic, color, Math.Round(angle, 2));
    }

    public static void WriteGraphicsOnlyPdf(string input, string output, CancellationToken ct = default)
    {
        using var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        var visited = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        foreach (PdfPage page in document.Pages)
        {
            ct.ThrowIfCancellationRequested();
            byte[] content = page.Contents.CreateSingleContent().Stream!.UnfilteredValue;
            byte[] suppressed = SuppressVisibleText(content, prefixDefaultMode: true);
            page.Contents.Elements.Clear();
            page.Contents.AppendContent().CreateStream(suppressed);
            Walk(page);
        }
        ct.ThrowIfCancellationRequested();
        document.Save(output);

        void Walk(PdfItem? item)
        {
            ct.ThrowIfCancellationRequested();
            if (item is PdfReference reference) item = reference.Value;
            if (item is not PdfObject obj || !visited.Add(obj)) return;
            if (obj is PdfDictionary dictionary)
            {
                string subtype = dictionary.Elements.GetName("/Subtype");
                if (subtype == "/Type3")
                    throw new NotSupportedException("此 PDF 使用特殊字形字体，暂不能可靠地保留版式，请改用仅提取文字。");
                bool form = subtype == "/Form";
                bool pattern = dictionary.Elements.GetInteger("/PatternType") == 1;
                if ((form || pattern) && dictionary.Stream is not null)
                {
                    byte[] bytes = SuppressVisibleText(dictionary.Stream.UnfilteredValue, prefixDefaultMode: false);
                    dictionary.Elements.Remove("/Filter");
                    dictionary.Elements.Remove("/DecodeParms");
                    dictionary.Stream.Value = bytes;
                    dictionary.Elements.SetInteger("/Length", bytes.Length);
                }
                foreach (var value in dictionary.Elements.Values.ToArray()) Walk(value);
            }
            else if (obj is PdfArray array)
            {
                foreach (var value in array.Elements) Walk(value);
            }
        }
    }

    /// <summary>
    /// Changes only integer text-rendering-mode operands, leaving all image/path/transform bytes
    /// untouched. PDFsharp's content reader discards inline-image payloads when serializing, so it
    /// must not be used here. Unknown inline-image streams fail explicitly rather than lose assets.
    /// </summary>
    internal static byte[] SuppressVisibleText(byte[] content, bool prefixDefaultMode)
    {
        var replacements = new List<(int Start, int Length)>();
        int index = 0, previousStart = -1, previousLength = 0;
        while (index < content.Length)
        {
            byte current = content[index];
            if (IsWhite(current)) { index++; continue; }
            if (current == '%') { SkipComment(content, ref index); continue; }
            if (current == '(') { SkipLiteral(content, ref index); previousStart = -1; continue; }
            if (current is (byte)'[' or (byte)'<')
            {
                SkipComposite(content, ref index);
                previousStart = -1; continue;
            }
            if (current == '/')
            {
                index++;
                while (index < content.Length && !IsDelimiter(content[index])) index++;
                previousStart = -1; continue;
            }
            int start = index;
            while (index < content.Length && !IsDelimiter(content[index])) index++;
            if (index == start) throw new InvalidDataException("PDF 内容流包含无效分隔符，不能安全地还原版式。");
            string token = Encoding.ASCII.GetString(content, start, index - start);
            if (token == "BI")
                throw new NotSupportedException("此 PDF 包含内联图像，暂不能可靠地保留版式，请改用仅提取文字。");
            if (token == "Tr")
            {
                if (previousStart < 0 || !int.TryParse(Encoding.ASCII.GetString(content, previousStart, previousLength),
                        NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int mode) || mode is < 0 or > 7)
                    throw new InvalidDataException("PDF 文字绘制指令无效，不能安全地还原版式。");
                // Clipping modes4..7 are deliberately retained as artwork and never emitted as
                // editable overlays. Already invisible OCR text (3) also remains invisible.
                if (mode <= 2) replacements.Add((previousStart, previousLength));
            }
            previousStart = start; previousLength = index - start;
        }
        using var result = new MemoryStream(content.Length + 16);
        if (prefixDefaultMode) result.Write("3 Tr\n"u8);
        int copied = 0;
        foreach (var (start, length) in replacements)
        {
            result.Write(content, copied, start - copied);
            result.WriteByte((byte)'3');
            copied = start + length;
        }
        result.Write(content, copied, content.Length - copied);
        return result.ToArray();
    }

    private static bool IsWhite(byte value) => value is 0 or 9 or 10 or 12 or 13 or 32;
    private static bool IsDelimiter(byte value) => IsWhite(value) || value is (byte)'(' or (byte)')'
        or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    private static void SkipComment(byte[] bytes, ref int index)
    {
        while (index < bytes.Length && bytes[index] is not (10 or 13)) index++;
    }

    private static void SkipLiteral(byte[] bytes, ref int index)
    {
        int depth = 1; index++;
        while (index < bytes.Length && depth > 0)
        {
            byte current = bytes[index++];
            if (current == '\\')
            {
                if (index < bytes.Length && bytes[index++] == 13 && index < bytes.Length && bytes[index] == 10) index++;
            }
            else if (current == '(') depth++;
            else if (current == ')') depth--;
        }
        if (depth != 0) throw new InvalidDataException("PDF 内容流的文字字符串未闭合。");
    }

    private static void SkipComposite(byte[] bytes, ref int index, int depth = 0)
    {
        if (depth > 128) throw new InvalidDataException("PDF 内容流的嵌套层级过深，不能安全地还原版式。");
        var endings = new Stack<byte>();
        if (bytes[index] == '[') { endings.Push((byte)']'); index++; }
        else if (index + 1 < bytes.Length && bytes[index + 1] == '<') { endings.Push((byte)'>'); index += 2; }
        else
        {
            index++;
            while (index < bytes.Length && bytes[index] != '>') index++;
            if (index == bytes.Length) throw new InvalidDataException("PDF 十六进制字符串未闭合。");
            index++; return;
        }
        while (index < bytes.Length && endings.Count > 0)
        {
            byte current = bytes[index];
            if (current == '(') { SkipLiteral(bytes, ref index); continue; }
            if (current == '%') { SkipComment(bytes, ref index); continue; }
            if (current is (byte)'[' or (byte)'<') { SkipComposite(bytes, ref index, depth + 1); continue; }
            if (current == endings.Peek())
            {
                index++;
                if (current == '>' && (index >= bytes.Length || bytes[index++] != '>'))
                    throw new InvalidDataException("PDF 字典未闭合。");
                endings.Pop();
            }
            else index++;
        }
        if (endings.Count != 0) throw new InvalidDataException("PDF 数组或字典未闭合。");
    }
}
