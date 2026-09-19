using System.Security.Cryptography;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Shunshou.Core;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Shunshou.SmokeTests;

public static class PdfLayoutTests
{
    public static async Task RunAsync(string root)
    {
        string folder = Path.Combine(root, "pdf-layout");
        Directory.CreateDirectory(folder);
        string source = Path.Combine(folder, "styled-table-picture-form.pdf");
        string graphics = Path.Combine(folder, "graphics-only.pdf");
        CreateFixture(source);
        byte[] originalHash = SHA256.HashData(File.ReadAllBytes(source));
        using (var input = UglyToad.PdfPig.PdfDocument.Open(source))
        {
            var page = input.GetPage(1);
            Check(input.NumberOfPages == 3 && !input.GetPage(2).Letters.Any(), "样例必须包含三页和中间空页");
            Check(!PdfOfficeLayout.RequiresOcr(page), "原生文字页面应走版式还原");
            var texts = PdfOfficeLayout.ExtractText(page);
            var title = texts.Single(t => t.Text == "Layout Title");
            Check(title.Bold && title.FontFamily == "Arial" && Math.Abs(title.FontSize - 24) < 0.1,
                "标题应保留粗体、字体和24pt字号");
            Check(title.ColorHex == "1A334C", "标题应保留原来的RGB文字颜色");
            Check(title.X is >= 49 and <= 51 && title.Y is > 20 and < 90,
                "标题应保留页面中的原位置");
            Check(texts.Any(t => t.Text == "Nested Form Text"), "Form XObject中的原生文字必须可编辑");
            Check(texts.Any(t => t.Text == "Left column first" && t.Italic)
                && texts.Any(t => t.Text == "Right column first" && t.X > 280), "两栏位置与斜体应保留");
            var vertical = texts.Single(t => t.Text == "VERTICAL");
            Check(Math.Abs(Math.Abs(vertical.Rotation) - 90) < 0.01 && vertical.Width > vertical.Height,
                "旋转文字应使用未旋转的文本框尺寸，不能二次旋转边界框");
            Check(!texts.Any(t => t.Text.Contains("HIDDEN OCR")), "不可把不可见OCR文字层叠加到图片上");
            Check(page.GetImages().Count() == 1, "样例必须包含真正的图片资产");
        }

        PdfOfficeLayout.WriteGraphicsOnlyPdf(source, graphics);
        using (var output = UglyToad.PdfPig.PdfDocument.Open(graphics))
        {
            var page = output.GetPage(1);
            Check(output.NumberOfPages == 3 && Math.Abs(output.GetPage(3).Width - 800) < 0.01,
                "分离图形不能丢掉空页或改变不同大小的页面");
            Check(page.Letters.Count > 0, "隐藏绘制应保留文字运算，不删除文本位置指令");
            Check(page.Letters.All(t => (int)t.RenderingMode == 3),
                "图形背景中不能残留可见的原生文字，包括嵌套Form");
            Check(page.GetImages().Count() == 1, "分离文字后必须保留原图片资产");
        }
        using (var result = PdfReader.Open(graphics, PdfDocumentOpenMode.Import))
        {
            string content = Encoding.ASCII.GetString(result.Pages[0].Contents.CreateSingleContent().Stream!.UnfilteredValue);
            Check(content.Contains("50 500 400 120 re S") && content.Contains("250 500 m 250 620 l S"),
                "表格线条和坐标不能丢失");
            Check(content.Contains("60 0 0 60 460 650 cm /Im1 Do"), "图片变换矩阵不能改变");
        }
        Check(originalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))), "转换不能修改源PDF");
        VerifyContentSafety();
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { PdfOfficeLayout.WriteGraphicsOnlyPdf(source, Path.Combine(folder, "cancelled.pdf"), cancelled.Token); }
            catch (OperationCanceledException) { goto CancellationPassed; }
            throw new InvalidOperationException("已取消的版式转换不能继续执行");
        }
        CancellationPassed:
        Console.WriteLine("PASS PDF layout: font/style/color/position, editable nested Form, hidden OCR excluded, picture/table artwork retained, stream safety, source unchanged and cancellation");
        await VerifyOfficeExportsAsync(source, folder);
        Check(originalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))), "Office导出不能修改源PDF");
    }

    private static async Task VerifyOfficeExportsAsync(string source, string folder)
    {
        var service = new PdfService();
        string outputFolder = Path.Combine(folder, "office");
        foreach (string format in new[] { "docx", "pptx" })
        {
            string output = await service.ExportEditableAsync(source, outputFolder, format);
            Check(Path.GetFileName(output).StartsWith("styled-table-picture-form_layout", StringComparison.Ordinal)
                && Path.GetExtension(output) == "." + format,
                "默认转换必须输出保留版式的文档");
            using OpenXmlPackage package = format == "docx"
                ? WordprocessingDocument.Open(output, false) : PresentationDocument.Open(output, false);
            var errors = new OpenXmlValidator(FileFormatVersions.Office2019).Validate(package).ToArray();
            Check(errors.Length == 0, format + " Office2019结构错误：" + string.Join("; ", errors.Select(e => e.Description + " " + e.Path?.XPath)));
            string text;
            if (package is WordprocessingDocument word)
            {
                var main = word.MainDocumentPart!;
                var document = main.Document!;
                var body = document.Body!;
                text = string.Concat(document.Descendants<W.Text>().Select(t => t.Text));
                Check(body.Elements<W.Paragraph>().Count() == 3 && body.Descendants<W.SectionProperties>().Count() == 3,
                    "Word必须保留三页，含中间空白页");
                var sizes = body.Descendants<W.PageSize>().ToArray();
                Check(sizes[0].Width?.Value == 12000 && sizes[0].Height?.Value == 16000
                    && sizes[2].Width?.Value == 16000 && sizes[2].Height?.Value == 8000,
                    "Word必须保留每页的方向和尺寸");
                Check(main.ImageParts.Count() == 3, "Word必须含页面图片与表格线条背景");
                var title = document.Descendants<W.Run>().Single(r => r.Elements<W.Text>().Any(t => t.Text == "Layout Title"));
                Check(title.RunProperties?.Bold is not null && title.RunProperties.FontSize?.Val?.Value == "48"
                    && title.RunProperties.Color?.Val?.Value == "1A334C", "Word标题应保留粗体、24pt和RGB颜色");
                var columns = document.Descendants<W.Run>().Where(r => r.Elements<W.Text>().Any(t => t.Text.Contains("column first"))).ToArray();
                Check(columns.Length == 2 && columns.All(r => r.RunProperties?.Italic is not null), "Word双栏斜体必须是可编辑文字");
            }
            else
            {
                var main = ((PresentationDocument)package).PresentationPart!;
                var slides = main.Presentation!.SlideIdList!.Elements<P.SlideId>()
                    .Select(id => (SlidePart)main.GetPartById(id.RelationshipId!)).ToArray();
                Check(slides.Length == 3 && !slides[1].Slide!.Descendants<A.Text>().Any(), "PPT必须保留中间空白页");
                Check(slides.All(s => s.ImageParts.Count() == 1), "PPT每页必须保留原版式的图形层");
                text = string.Concat(slides.SelectMany(s => s.Slide!.Descendants<A.Text>()).Select(t => t.Text));
                var title = slides[0].Slide!.Descendants<A.Run>().Single(r => r.Text?.Text == "Layout Title");
                var properties = title.RunProperties!;
                Check(properties.Bold?.Value == true && properties.FontSize?.Value == 2400
                    && properties.GetFirstChild<A.SolidFill>()?.RgbColorModelHex?.Val?.Value == "1A334C",
                    "PPT标题应保留粗体、24pt和RGB颜色");
                var finalPicture = slides[2].Slide!.Descendants<P.Picture>().Single();
                var extents = finalPicture.ShapeProperties!.Transform2D!.Extents!;
                Check(extents.Cx!.Value == 2 * extents.Cy!.Value, "不同页面比例在PPT中必须等比缩放，不能拉伸");
            }
            Check(text.Contains("Layout Title") && text.Contains("Nested Form Text") && text.Contains("Table Cell A")
                && text.Contains("Landscape final page") && !text.Contains("HIDDEN OCR") && !text.Contains("COVERED TEXT"),
                format + "必须包含真实可编辑文字，隐藏OCR层和被图片盖住的文字不能重新出现");
            Console.WriteLine($"PASS PDF layout: default {format} / Office2019 schema / editable style + graphics / 3 pages + blank + mixed sizes: {output}");
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool cancelledCorrectly = false;
        try { await service.ExportEditableAsync(source, outputFolder, "docx", ct: cancelled.Token); }
        catch (OperationCanceledException) { cancelledCorrectly = true; }
        Check(cancelledCorrectly, "默认版式转换必须响应取消");
        Check(!Directory.EnumerateDirectories(folder, ".shunshou-pdf-*", SearchOption.AllDirectories).Any(), "Office导出后应清理暂存目录");
    }

    private static void VerifyContentSafety()
    {
        const string source = "q 0 Tr BT /F1 12 Tf (literal 0 Tr and BI \\(nested\\)) Tj [(1 Tr) 12 <30205472>] TJ ET Q\n"
            + "/P << /Description (2 Tr) /Values [0 1 2] >> BDC EMC\n% 1 Tr in a comment\n2 Tr 4 Tr 5 Tr 6 Tr 7 Tr\n";
        string result = Encoding.ASCII.GetString(PdfOfficeLayout.SuppressVisibleText(Encoding.ASCII.GetBytes(source), true));
        Check(result.StartsWith("3 Tr\nq 3 Tr") && result.EndsWith("3 Tr 4 Tr 5 Tr 6 Tr 7 Tr\n"),
            "只有可见文字的绘制模式应被改为不可见；剪裁模式应保留");
        Check(result.Contains("(literal 0 Tr and BI \\(nested\\))") && result.Contains("[(1 Tr) 12 <30205472>]")
            && result.Contains("/Description (2 Tr)") && result.Contains("% 1 Tr in a comment"),
            "字符串、数组、字典和注释中的相似文字不能被误改");
        bool rejected = false;
        try { PdfOfficeLayout.SuppressVisibleText("BI /W 1 /H 1 ID x EI"u8.ToArray(), false); }
        catch (NotSupportedException) { rejected = true; }
        Check(rejected, "尚不支持的内联图像必须明确失败，不能静默丢失图片");
    }

    public static void CreateFixture(string path)
    {
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Width = XUnit.FromPoint(600); page.Height = XUnit.FromPoint(800);
        var font = new PdfDictionary(document);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/Type1");
        font.Elements.SetName("/BaseFont", "/Helvetica-Bold");
        document.Internals.AddObject(font);
        var fonts = new PdfDictionary(document); fonts.Elements["/F1"] = font.Reference!;
        var italic = new PdfDictionary(document);
        italic.Elements.SetName("/Type", "/Font"); italic.Elements.SetName("/Subtype", "/Type1");
        italic.Elements.SetName("/BaseFont", "/Helvetica-Oblique");
        document.Internals.AddObject(italic); fonts.Elements["/F2"] = italic.Reference!;
        var resources = new PdfDictionary(document); resources.Elements["/Font"] = fonts;
        var xObjects = new PdfDictionary(document); resources.Elements["/XObject"] = xObjects;
        page.Elements["/Resources"] = resources;

        var image = new PdfDictionary(document);
        image.Elements.SetName("/Type", "/XObject"); image.Elements.SetName("/Subtype", "/Image");
        image.Elements.SetInteger("/Width", 2); image.Elements.SetInteger("/Height", 2);
        image.Elements.SetInteger("/BitsPerComponent", 8); image.Elements.SetName("/ColorSpace", "/DeviceRGB");
        image.CreateStream([255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0]);
        document.Internals.AddObject(image); xObjects.Elements["/Im1"] = image.Reference!;

        var form = new PdfDictionary(document);
        form.Elements.SetName("/Type", "/XObject"); form.Elements.SetName("/Subtype", "/Form");
        form.Elements["/BBox"] = new PdfArray(document, new PdfInteger(0), new PdfInteger(0), new PdfInteger(300), new PdfInteger(60));
        var formResources = new PdfDictionary(document); formResources.Elements["/Font"] = fonts;
        form.Elements["/Resources"] = formResources;
        form.CreateStream(Encoding.ASCII.GetBytes("q 0.9 0.95 0.9 rg 0 0 300 60 re f Q BT 0 Tr /F1 16 Tf 10 20 Td (Nested Form Text) Tj ET"));
        document.Internals.AddObject(form); xObjects.Elements["/Fm1"] = form.Reference!;

        string content = "q 0.1 0.2 0.3 rg BT /F1 24 Tf 50 740 Td (Layout Title) Tj ET Q\n"
            + "q 0 0 0 RG 1 w 50 500 400 120 re S 250 500 m 250 620 l S 50 560 m 450 560 l S Q\n"
            + "BT /F1 12 Tf 70 590 Td (Table Cell A) Tj 0 -60 Td (Table Cell B) Tj ET\n"
            + "BT /F1 6 Tf 465 675 Td (COVERED TEXT) Tj ET\n"
            + "q 60 0 0 60 460 650 cm /Im1 Do Q\nq 1 0 0 1 50 390 cm /Fm1 Do Q\n"
            + "BT /F2 14 Tf 50 240 Td (Left column first) Tj 0 -24 Td (Left column second) Tj ET\n"
            + "BT /F2 14 Tf 320 240 Td (Right column first) Tj 0 -24 Td (Right column second) Tj ET\n"
            + "BT /F2 10 Tf 0 1 -1 0 560 80 Tm (VERTICAL) Tj ET\n"
            + "BT 3 Tr /F1 12 Tf 70 300 Td (HIDDEN OCR) Tj ET\n";
        page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(content));
        var blank = document.AddPage(); blank.Width = XUnit.FromPoint(600); blank.Height = XUnit.FromPoint(800);
        var landscape = document.AddPage(); landscape.Width = XUnit.FromPoint(800); landscape.Height = XUnit.FromPoint(400);
        landscape.Elements["/Resources"] = resources;
        landscape.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(
            "q 0.1 0.5 0.3 rg BT /F1 22 Tf 60 330 Td (Landscape final page) Tj ET Q\n"
            + "q 100 0 0 100 620 240 cm /Im1 Do Q\n"));
        document.Save(path);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
