using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using PdfSharp.Pdf.IO;
using Shunshou.Core;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Shunshou.SmokeTests;

public static class PdfEnhanceTests
{
    public static async Task RunAsync(string root)
    {
        string folder = Path.Combine(root, "pdf-enhance");
        Directory.CreateDirectory(folder);
        PdfService.EnsureWindowsFonts();
        string input = Path.Combine(folder, "sample.pdf");
        CreateSamplePdf(input, "PDF 加水印与压缩测试。Hello world。");
        var pdf = new PdfService();

        // 1) Watermark
        string watermarked = await pdf.AddWatermarkAsync(input, Path.Combine(folder, "水印"), "顺手水印", 0.2);
        CompressionTests.Check(File.Exists(watermarked) && new FileInfo(watermarked).Length > 0, "加水印应生成非空 PDF");
        using (var doc = PdfReader.Open(watermarked, PdfDocumentOpenMode.Import))
            CompressionTests.Check(doc.PageCount == 1, "加水印后页数不变");
        Console.WriteLine("PASS PDF enhance: watermark");

        // 2) Compress
        string compressed = await pdf.CompressAsync(input, Path.Combine(folder, "压缩"));
        CompressionTests.Check(File.Exists(compressed) && new FileInfo(compressed).Length > 0, "压缩应生成非空 PDF");
        using (var doc = PdfReader.Open(compressed, PdfDocumentOpenMode.Import))
            CompressionTests.Check(doc.PageCount == 1, "压缩后页数不变");
        Console.WriteLine("PASS PDF enhance: compress");

        // 3) Word to PDF
        string docx = Path.Combine(folder, "test.docx");
        CreateSampleDocx(docx, "Word 转 PDF 测试。Hello。");
        string converted = await new WordToPdfService().ConvertAsync(docx, Path.Combine(folder, "转换"));
        CompressionTests.Check(File.Exists(converted) && new FileInfo(converted).Length > 0, "Word 转 PDF 应生成非空 PDF");
        using (var doc = PdfReader.Open(converted, PdfDocumentOpenMode.Import))
            CompressionTests.Check(doc.PageCount >= 1, "Word 转 PDF 应至少有一页");
        Console.WriteLine("PASS PDF enhance: Word to PDF");
    }

    private static void CreateSamplePdf(string path, string text)
    {
        var doc = new PdfSharp.Pdf.PdfDocument();
        var page = doc.AddPage();
        page.Size = PdfSharp.PageSize.A4;
        using var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
        var font = new PdfSharp.Drawing.XFont("Microsoft YaHei", 12, PdfSharp.Drawing.XFontStyleEx.Regular,
            new PdfSharp.Drawing.XPdfFontOptions(PdfSharp.Pdf.PdfFontEmbedding.EmbedCompleteFontFile));
        gfx.DrawString(text, font, PdfSharp.Drawing.XBrushes.Black, 40, 100);
        doc.Save(path);
    }

    private static void CreateSampleDocx(string path, string text)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var body = new W.Body();
        var main = doc.AddMainDocumentPart();
        main.Document = new W.Document(body);
        body.Append(new W.Paragraph(
            new W.ParagraphProperties(new W.ParagraphStyleId { Val = "Heading1" }),
            new W.Run(new W.Text("标题示例"))));
        body.Append(new W.Paragraph(
            new W.Run(new W.RunProperties(new W.Bold()), new W.Text(text))));
        main.Document.Save();
    }
}
