using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WindowsPdf = Windows.Data.Pdf.PdfDocument;

namespace Shunshou.Core;

/// <summary>Watermark and recompression helpers that share the staging helpers of PdfService.</summary>
public sealed partial class PdfService
{
    // PDFsharp core build resolves no Windows fonts unless a resolver is registered; done once
    // for both the watermark text and the Word → PDF converter.
    private static int _windowsFontsEnabled;

    internal static void EnsureWindowsFonts()
    {
        if (Interlocked.Exchange(ref _windowsFontsEnabled, 1) == 0)
            GlobalFontSettings.FontResolver = new ShunshouFontResolver();
    }

    /// <summary>
    /// PDFsharp 6 cannot parse TrueType collections (.ttc), which rules out Microsoft YaHei
    /// (msyh.ttc). SimHei (simhei.ttf) is a plain TrueType font present on every Windows install
    /// and covers Simplified Chinese; bold/italic are simulated by PDFsharp when needed.
    /// </summary>
    private sealed class ShunshouFontResolver : IFontResolver
    {
        private static readonly string FontsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        public string DefaultFontName => "SimHei";

        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            string path = Path.Combine(FontsDir, "simhei.ttf");
            if (!File.Exists(path)) throw new InvalidOperationException("找不到系统字体 SimHei，无法嵌入中文字体。");
            return new FontResolverInfo("SimHei" + (isBold ? "#Bold" : isItalic ? "#Italic" : "#Regular"),
                mustSimulateBold: isBold, mustSimulateItalic: isItalic);
        }

        public byte[] GetFont(string faceName)
        {
            string path = Path.Combine(FontsDir, "simhei.ttf");
            return File.Exists(path) ? File.ReadAllBytes(path) : [];
        }
    }

    /// <summary>Draws a semi-transparent gray watermark rotated 45° at the center of every page.</summary>
    public Task<string> AddWatermarkAsync(string input, string outputDir, string text, double opacity,
        IProgress<ToolProgress>? progress = null, CancellationToken ct = default) => Task.Run(() =>
    {
        ValidateInput(input);
        text = (text ?? "").Trim();
        if (text.Length == 0) throw new ArgumentException("请填写水印文字。", nameof(text));
        if (text.Length > 80) throw new ArgumentException("水印文字最多 80 个字符。", nameof(text));
        if (opacity is < 0.05 or > 0.5) throw new ArgumentOutOfRangeException(nameof(opacity), "水印透明度应在 5%–50% 之间。");
        ct.ThrowIfCancellationRequested();
        EnsureWindowsFonts();
        Directory.CreateDirectory(outputDir);
        string staging = CreateStaging(outputDir);
        try
        {
            using var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
            if (document.PageCount == 0) throw new InvalidDataException("PDF 没有可加水印的页面。");
            int alpha = (int)Math.Round(opacity * 255);
            var brush = new XSolidBrush(XColor.FromArgb(alpha, 128, 128, 128));
            var options = new XPdfFontOptions(PdfFontEmbedding.EmbedCompleteFontFile);
            for (int index = 0; index < document.PageCount; index++)
            {
                ct.ThrowIfCancellationRequested();
                var page = document.Pages[index];
                double width = page.Width.Point, height = page.Height.Point;
                double diagonal = Math.Sqrt(width * width + height * height);
                double size = Math.Min(72, diagonal * 0.9 / Math.Max(1, text.Length) * 1.6);
                size = Math.Clamp(size, 14, 120);
                var font = new XFont("SimHei", size, XFontStyleEx.Bold, options);
                using var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                var measured = graphics.MeasureString(text, font);
                while (measured.Width > diagonal * 0.75 && size > 12)
                {
                    size -= 2;
                    font = new XFont("SimHei", size, XFontStyleEx.Bold, options);
                    measured = graphics.MeasureString(text, font);
                }
                var center = new XPoint(width / 2, height / 2);
                graphics.RotateAtTransform(-45, center);
                graphics.DrawString(text, font, brush, new XRect(center.X - measured.Width / 2,
                    center.Y - measured.Height / 2, measured.Width, measured.Height), XStringFormats.Center);
                progress?.Report(new ToolProgress(90.0 * (index + 1) / document.PageCount,
                    $"正在为第 {index + 1}/{document.PageCount} 页加水印"));
            }
            string temporary = Path.Combine(staging, "watermarked.pdf");
            document.Save(temporary);
            ct.ThrowIfCancellationRequested();
            string final = UniquePath(outputDir, Path.GetFileNameWithoutExtension(input) + "_watermarked", ".pdf");
            File.Move(temporary, final);
            progress?.Report(new ToolProgress(100, "水印已写入每一页"));
            return final;
        }
        finally { CleanupStaging(staging); }
    }, ct);

    /// <summary>
    /// Recompresses each page as a 150 DPI JPEG (quality 75) and rebuilds the PDF around the images.
    /// Vector text becomes raster; when the result would be larger than the source the original file
    /// is copied instead and the report says so.
    /// </summary>
    public Task<string> CompressAsync(string input, string outputDir,
        IProgress<ToolProgress>? progress = null, CancellationToken ct = default) => Task.Run(async () =>
    {
        ValidateInput(input);
        Directory.CreateDirectory(outputDir);
        string staging = CreateStaging(outputDir);
        try
        {
            const int dpi = 150;
            var document = await WindowsPdf.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(Path.GetFullPath(input))).AsTask(ct);
            if (document.PageCount == 0) throw new InvalidDataException("PDF 没有可压缩的页面。");
            using var output = new PdfSharp.Pdf.PdfDocument();
            for (uint index = 0; index < document.PageCount; index++)
            {
                ct.ThrowIfCancellationRequested();
                using var page = document.GetPage(index);
                uint width = checked((uint)Math.Ceiling(page.Size.Width * dpi / 96.0));
                uint height = checked((uint)Math.Ceiling(page.Size.Height * dpi / 96.0));
                if (width == 0 || height == 0 || (ulong)width * height > 200_000_000)
                    throw new InvalidOperationException($"第 {index + 1} 页过大，无法安全压缩。");
                string jpegPath = Path.Combine(staging, $"page-{index + 1:D4}.jpg");
                using (var rendered = new InMemoryRandomAccessStream())
                {
                    await page.RenderToStreamAsync(rendered, new Windows.Data.Pdf.PdfPageRenderOptions
                    {
                        DestinationWidth = width, DestinationHeight = height,
                        BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255)
                    }).AsTask(ct);
                    rendered.Seek(0);
                    var decoder = await BitmapDecoder.CreateAsync(rendered);
                    var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                        new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
                    await File.WriteAllBytesAsync(jpegPath, [], ct);
                    var file = await StorageFile.GetFileFromPathAsync(jpegPath);
                    using var encoded = await file.OpenAsync(FileAccessMode.ReadWrite);
                    var properties = new BitmapPropertySet { { "ImageQuality", new BitmapTypedValue(0.75f, Windows.Foundation.PropertyType.Single) } };
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, encoded, properties);
                    encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, dpi, dpi, pixels);
                    await encoder.FlushAsync();
                    await encoded.FlushAsync();
                }
                var outPage = output.AddPage();
                outPage.Width = XUnit.FromPoint(page.Size.Width * 72.0 / 96.0);
                outPage.Height = XUnit.FromPoint(page.Size.Height * 72.0 / 96.0);
                using (var graphics = XGraphics.FromPdfPage(outPage))
                using (var image = XImage.FromFile(jpegPath))
                    graphics.DrawImage(image, 0, 0, outPage.Width.Point, outPage.Height.Point);
                progress?.Report(new ToolProgress(90.0 * (index + 1) / document.PageCount,
                    $"正在以 {dpi} DPI 压缩第 {index + 1}/{document.PageCount} 页"));
            }
            string temporary = Path.Combine(staging, "compressed.pdf");
            output.Save(temporary);
            ct.ThrowIfCancellationRequested();
            long originalBytes = new FileInfo(input).Length;
            long compressedBytes = new FileInfo(temporary).Length;
            string final = UniquePath(outputDir, Path.GetFileNameWithoutExtension(input) + "_compressed", ".pdf");
            if (compressedBytes >= originalBytes)
            {
                // Already compact: keep the original content instead of publishing a bigger file.
                File.Copy(input, final);
                progress?.Report(new ToolProgress(100,
                    $"原文件已经很小（{FormatSize(originalBytes)}），重压缩后为 {FormatSize(compressedBytes)}，已保留原文件内容"));
            }
            else
            {
                File.Move(temporary, final);
                progress?.Report(new ToolProgress(100,
                    $"压缩前 {FormatSize(originalBytes)} → 压缩后 {FormatSize(compressedBytes)}，减小 {100 - compressedBytes * 100.0 / originalBytes:0}%"));
            }
            return final;
        }
        finally { CleanupStaging(staging); }
    }, ct);

    private static string FormatSize(long bytes) => bytes >= 1_000_000
        ? $"{bytes / 1_000_000d:0.00} MB"
        : $"{bytes / 1_000d:0.0} KB";
}
