using Shunshou.Core;

namespace Shunshou.SmokeTests;

/// <summary>Covers the service-layer helpers the PDF reader workspace is built on.</summary>
public static class PdfReaderTests
{
    public static async Task RunAsync(string root)
    {
        string folder = Path.Combine(root, "pdf-reader");
        Directory.CreateDirectory(folder);
        PdfService.EnsureWindowsFonts();
        string input = Path.Combine(folder, "reader.pdf");
        var document = new PdfSharp.Pdf.PdfDocument();
        for (int i = 1; i <= 3; i++)
        {
            var page = document.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            using var graphics = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
            var font = new PdfSharp.Drawing.XFont("Microsoft YaHei", 12, PdfSharp.Drawing.XFontStyleEx.Regular,
                new PdfSharp.Drawing.XPdfFontOptions(PdfSharp.Pdf.PdfFontEmbedding.EmbedCompleteFontFile));
            graphics.DrawString($"PDF 阅读测试，第 {i} 页。", font, PdfSharp.Drawing.XBrushes.Black, 40, 100);
        }
        document.Save(input);

        int pages = await PdfService.CountPagesAsync(input);
        CompressionTests.Check(pages == 3, $"页数应为 3，实际 {pages}");
        Console.WriteLine("PASS PDF reader: page count");

        var rendered = await PdfService.RenderPagePngAsync(input, 1, 96);
        CompressionTests.Check(rendered.Png.Length > 100, "渲染应产生非空 PNG");
        CompressionTests.Check(rendered.Png[0] == 0x89 && rendered.Png[1] == 0x50 && rendered.Png[2] == 0x4E && rendered.Png[3] == 0x47,
            "渲染结果应是 PNG");
        CompressionTests.Check(rendered.WidthDip > 500 && rendered.HeightDip > 700, "A4 页面尺寸应按 DIP 返回");
        CompressionTests.Check(rendered.PixelWidth == (int)Math.Ceiling(rendered.WidthDip) &&
            rendered.PixelHeight == (int)Math.Ceiling(rendered.HeightDip), "96 DPI 下像素尺寸应等于 DIP 尺寸");
        Console.WriteLine("PASS PDF reader: render page");

        var zoomed = await PdfService.RenderPagePngAsync(input, 0, 192);
        CompressionTests.Check(zoomed.PixelWidth > rendered.PixelWidth * 1.9, "更高 DPI 应渲染出更大位图");
        Console.WriteLine("PASS PDF reader: dpi scaling");

        bool threw = false;
        try { await PdfService.RenderPagePngAsync(input, 5, 96); }
        catch (ArgumentOutOfRangeException) { threw = true; }
        CompressionTests.Check(threw, "页码越界应抛出 ArgumentOutOfRangeException");

        threw = false;
        try { await PdfService.RenderPagePngAsync(input, 0, 10); }
        catch (ArgumentOutOfRangeException) { threw = true; }
        CompressionTests.Check(threw, "DPI 越界应抛出 ArgumentOutOfRangeException");
        Console.WriteLine("PASS PDF reader: argument validation");
    }
}
