using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Shunshou.Core;

/// <summary>Writes fixed PDF page geometry with editable text, above a graphics-only page layer.</summary>
internal static class PdfWordLayoutWriter
{
    private static readonly XNamespace Word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Drawing = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Anchor = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace Picture = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private static readonly XNamespace Shape = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
    private static readonly XNamespace Relationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Compatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    public static void Write(string path, IReadOnlyList<PdfOfficePage> pages, CancellationToken ct)
    {
        if (pages.Count == 0) throw new InvalidDataException("PDF 没有可导出的页面。");
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        var settings = main.AddNewPart<DocumentSettingsPart>();
        settings.Settings = new W.Settings(new W.Compatibility(new W.CompatibilitySetting
        {
            Name = W.CompatSettingNameValues.CompatibilityMode,
            Uri = "http://schemas.microsoft.com/office/word",
            Val = "15"
        }));
        var body = new XElement(Word + "body");
        uint id = 1;
        for (int index = 0; index < pages.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var page = pages[index];
            if (!double.IsFinite(page.Width) || !double.IsFinite(page.Height) || page.Width <= 0 || page.Height <= 0)
                throw new InvalidDataException("PDF 页面尺寸无效。");
            // Word's page limit is 22 inches. Preserve the aspect ratio on oversized PDF sheets.
            double scale = Math.Min(1, 1584 / Math.Max(page.Width, page.Height));
            var properties = new XElement(Word + "pPr",
                new XElement(Word + "widowControl", Value("0")),
                new XElement(Word + "snapToGrid", Value("0")),
                new XElement(Word + "spacing", Attribute("before", 0), Attribute("after", 0), Attribute("line", 20), Attribute("lineRule", "exact")),
                new XElement(Word + "rPr", new XElement(Word + "sz", Value("2")), new XElement(Word + "szCs", Value("2"))));
            if (index < pages.Count - 1) properties.Add(Section(page, scale));
            var paragraph = new XElement(Word + "p", properties);
            if (page.BackgroundImage is not null)
            {
                var image = main.AddImagePart(ImagePartType.Png);
                using var source = File.OpenRead(page.BackgroundImage);
                image.FeedData(source);
                paragraph.Add(new XElement(Word + "r", Background(main.GetIdOfPart(image), id++, page.Width * scale, page.Height * scale)));
            }
            foreach (var text in page.Texts)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(text.Text)) continue;
                paragraph.Add(new XElement(Word + "r", TextBox(text, id++, scale)));
            }
            // One short anchor paragraph per PDF page also preserves intentionally blank pages.
            body.Add(paragraph);
            if (index == pages.Count - 1) body.Add(Section(page, scale));
        }
        var root = new XElement(Word + "document",
            new XAttribute(XNamespace.Xmlns + "w", Word),
            new XAttribute(XNamespace.Xmlns + "a", Drawing),
            new XAttribute(XNamespace.Xmlns + "wp", Anchor),
            new XAttribute(XNamespace.Xmlns + "pic", Picture),
            new XAttribute(XNamespace.Xmlns + "wps", Shape),
            new XAttribute(XNamespace.Xmlns + "r", Relationship),
            new XAttribute(XNamespace.Xmlns + "mc", Compatibility),
            new XAttribute(Compatibility + "Ignorable", "wps"), body);
        using var stream = main.GetStream(FileMode.Create, FileAccess.Write);
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false) });
        root.Save(writer);
    }

    private static XElement Section(PdfOfficePage page, double scale) => new(Word + "sectPr",
        new XElement(Word + "type", Value("nextPage")),
        new XElement(Word + "pgSz", Attribute("w", Twips(page.Width * scale)), Attribute("h", Twips(page.Height * scale)),
            Attribute("orient", page.Width > page.Height ? "landscape" : "portrait")),
        new XElement(Word + "pgMar", Attribute("top", 0), Attribute("right", 0), Attribute("bottom", 0), Attribute("left", 0),
            Attribute("header", 0), Attribute("footer", 0), Attribute("gutter", 0)),
        new XElement(Word + "cols", Attribute("space", 0)));

    private static XElement Background(string relationship, uint id, double width, double height)
    {
        long cx = Emu(width), cy = Emu(height);
        var picture = new XElement(Picture + "pic",
            new XElement(Picture + "nvPicPr",
                new XElement(Picture + "cNvPr", new XAttribute("id", id), new XAttribute("name", "PDF 页面图形")),
                new XElement(Picture + "cNvPicPr", new XElement(Drawing + "picLocks", new XAttribute("noChangeAspect", 1)))),
            new XElement(Picture + "blipFill",
                new XElement(Drawing + "blip", new XAttribute(Relationship + "embed", relationship)),
                new XElement(Drawing + "stretch", new XElement(Drawing + "fillRect"))),
            new XElement(Picture + "spPr", Transform(cx, cy), Rectangle()));
        return PositionedDrawing(id, "PDF 页面图形", 0, 0, cx, cy, true, Picture.NamespaceName, picture);
    }

    private static XElement TextBox(PdfOfficeText text, uint id, double scale)
    {
        double fontSize = Math.Clamp(text.FontSize * scale, 1, 1638);
        long cx = Emu(Math.Max(1, text.Width * scale));
        long cy = Emu(Math.Max(text.Height * scale, fontSize));
        long x = Emu(text.X * scale), y = Emu(text.Y * scale);
        double rotation = double.IsFinite(text.Rotation) ? (text.Rotation % 360 + 360) % 360 : 0;
        bool quarterTurn = Math.Abs(rotation - 90) < 0.01 || Math.Abs(rotation - 270) < 0.01;
        if (quarterTurn)
        {
            // Word readers reliably preserve textbox text direction; shape rotation alone can
            // keep the text upright. The vertical frame keeps the original PDF rotation center.
            x += (cx - cy) / 2;
            y += (cy - cx) / 2;
            (cx, cy) = (cy, cx);
            // Office's vertical text layout can wrap an italic final glyph despite wrap=none.
            // Extend the trailing edge while keeping the first glyph's origin fixed.
            long trailingRoom = Emu(Math.Max(2, fontSize * 0.5));
            cy += trailingRoom;
            if (rotation > 180) y -= trailingRoom;
        }
        string font = CleanText(string.IsNullOrWhiteSpace(text.FontFamily) ? "Arial" : text.FontFamily);
        string color = text.ColorHex.Length == 6 && text.ColorHex.All(Uri.IsHexDigit) ? text.ColorHex : "000000";
        var runProperties = new XElement(Word + "rPr",
            new XElement(Word + "rFonts", Attribute("ascii", font), Attribute("hAnsi", font), Attribute("eastAsia", font), Attribute("cs", font)),
            text.Bold ? new XElement(Word + "b") : null,
            text.Bold ? new XElement(Word + "bCs") : null,
            text.Italic ? new XElement(Word + "i") : null,
            text.Italic ? new XElement(Word + "iCs") : null,
            new XElement(Word + "noProof"),
            new XElement(Word + "snapToGrid", Value("0")),
            new XElement(Word + "color", Value(color)),
            new XElement(Word + "sz", Value(Math.Round(fontSize * 2).ToString(CultureInfo.InvariantCulture))),
            new XElement(Word + "szCs", Value(Math.Round(fontSize * 2).ToString(CultureInfo.InvariantCulture))),
            // PDF font substitutions can change advance widths; fit the editable run to its original line width.
            new XElement(Word + "fitText", Attribute("val", Math.Clamp(Twips(text.Width * scale), 1, 31680)), Attribute("id", id)),
            new XElement(Word + "lang", Value("en-US"), Attribute("eastAsia", "zh-CN")));
        var paragraph = new XElement(Word + "p",
            new XElement(Word + "pPr",
                new XElement(Word + "widowControl", Value("0")),
                new XElement(Word + "suppressAutoHyphens"),
                new XElement(Word + "snapToGrid", Value("0")),
                new XElement(Word + "spacing", Attribute("before", 0), Attribute("after", 0), Attribute("line", Twips(fontSize)), Attribute("lineRule", "exact")),
                new XElement(Word + "ind", Attribute("left", 0), Attribute("right", 0), Attribute("firstLine", 0)),
                new XElement(Word + "jc", Value("left"))),
            new XElement(Word + "r", runProperties,
                new XElement(Word + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), CleanText(text.Text))));
        var transform = Transform(cx, cy);
        if (!quarterTurn && rotation > 0.01)
            transform.Add(new XAttribute("rot", (int)Math.Round(rotation * 60000)));
        var shape = new XElement(Shape + "wsp",
            new XElement(Shape + "cNvSpPr", new XAttribute("txBox", 1)),
            new XElement(Shape + "spPr", transform, Rectangle(), new XElement(Drawing + "noFill"),
                new XElement(Drawing + "ln", new XElement(Drawing + "noFill"))),
            new XElement(Shape + "txbx", new XElement(Word + "txbxContent", paragraph)),
            new XElement(Shape + "bodyPr", new XAttribute("vert", quarterTurn ? rotation < 180 ? "vert" : "vert270" : "horz"),
                new XAttribute("upright", 0), new XAttribute("wrap", "none"),
                new XAttribute("lIns", 0), new XAttribute("tIns", 0), new XAttribute("rIns", 0), new XAttribute("bIns", 0),
                new XAttribute("anchor", "t"), new XAttribute("vertOverflow", "overflow"), new XAttribute("horzOverflow", "overflow"),
                new XElement(Drawing + "noAutofit")));
        return PositionedDrawing(id, "可编辑 PDF 文字", x, y, cx, cy, false, Shape.NamespaceName, shape);
    }

    private static XElement PositionedDrawing(uint id, string name, long x, long y, long width, long height, bool behind, string uri, XElement content) =>
        new(Word + "drawing", new XElement(Anchor + "anchor",
            new XAttribute("distT", 0), new XAttribute("distB", 0), new XAttribute("distL", 0), new XAttribute("distR", 0),
            new XAttribute("simplePos", 0), new XAttribute("relativeHeight", behind ? 0 : id), new XAttribute("behindDoc", behind ? 1 : 0),
            new XAttribute("locked", 0), new XAttribute("layoutInCell", 0), new XAttribute("allowOverlap", 1),
            new XElement(Anchor + "simplePos", new XAttribute("x", 0), new XAttribute("y", 0)),
            new XElement(Anchor + "positionH", new XAttribute("relativeFrom", "page"), new XElement(Anchor + "posOffset", x)),
            new XElement(Anchor + "positionV", new XAttribute("relativeFrom", "page"), new XElement(Anchor + "posOffset", y)),
            new XElement(Anchor + "extent", new XAttribute("cx", width), new XAttribute("cy", height)),
            new XElement(Anchor + "effectExtent", new XAttribute("l", 0), new XAttribute("t", 0), new XAttribute("r", 0), new XAttribute("b", 0)),
            new XElement(Anchor + "wrapNone"),
            new XElement(Anchor + "docPr", new XAttribute("id", id), new XAttribute("name", name)),
            new XElement(Anchor + "cNvGraphicFramePr"),
            new XElement(Drawing + "graphic", new XElement(Drawing + "graphicData", new XAttribute("uri", uri), content))));

    private static XElement Transform(long width, long height) => new(Drawing + "xfrm",
        new XElement(Drawing + "off", new XAttribute("x", 0), new XAttribute("y", 0)),
        new XElement(Drawing + "ext", new XAttribute("cx", width), new XAttribute("cy", height)));
    private static XElement Rectangle() => new(Drawing + "prstGeom", new XAttribute("prst", "rect"), new XElement(Drawing + "avLst"));
    private static XAttribute Attribute(string name, object value) => new(Word + name, value);
    private static XAttribute Value(string value) => Attribute("val", value);
    private static long Emu(double points) => checked((long)Math.Round(points * 12700));
    private static int Twips(double points) => Math.Max(1, checked((int)Math.Round(points * 20)));
    private static string CleanText(string text)
    {
        var result = new System.Text.StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
            if (rune.Value > 0xFFFF || XmlConvert.IsXmlChar((char)rune.Value)) result.Append(rune.ToString());
        return result.ToString();
    }
}
