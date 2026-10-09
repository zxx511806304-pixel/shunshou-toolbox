using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WindowsPdf = Windows.Data.Pdf.PdfDocument;

namespace Shunshou.Core;

/// <summary>One rendered page for the reading view: PNG bytes plus the logical page size in DIPs.</summary>
public sealed record PdfRenderedPage(byte[] Png, double WidthDip, double HeightDip, int PixelWidth, int PixelHeight);

public sealed partial class PdfService
{
    /// <summary>Opens the document only to count pages; used by the reader before any rendering happens.</summary>
    public static async Task<int> CountPagesAsync(string input, CancellationToken ct = default)
    {
        ValidateInput(input);
        var document = await WindowsPdf.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(Path.GetFullPath(input))).AsTask(ct);
        if (document.PageCount == 0) throw new InvalidDataException("该 PDF 没有页面。");
        return checked((int)document.PageCount);
    }

    /// <summary>
    /// Renders one page (0-based <paramref name="index"/>) to PNG at the given DPI. The same 200-megapixel
    /// safety ceiling as the batch exporter applies, so a huge zoom can never exhaust memory.
    /// </summary>
    public static async Task<PdfRenderedPage> RenderPagePngAsync(string input, uint index, int dpi, CancellationToken ct = default)
    {
        ValidateInput(input);
        if (dpi is < 48 or > 600) throw new ArgumentOutOfRangeException(nameof(dpi), "阅读渲染分辨率应在 48–600 DPI 之间。");
        var document = await WindowsPdf.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(Path.GetFullPath(input))).AsTask(ct);
        if (index >= document.PageCount) throw new ArgumentOutOfRangeException(nameof(index), $"第 {index + 1} 页不存在。");
        ct.ThrowIfCancellationRequested();
        using var page = document.GetPage(index);
        // WinRT exposes the page size in device-independent pixels: 96 DIP per inch.
        uint width = checked((uint)Math.Ceiling(page.Size.Width * dpi / 96.0));
        uint height = checked((uint)Math.Ceiling(page.Size.Height * dpi / 96.0));
        if (width == 0 || height == 0 || (ulong)width * height > 200_000_000)
            throw new InvalidOperationException($"第 {index + 1} 页在该缩放下过大，无法安全渲染。");
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
        {
            DestinationWidth = width, DestinationHeight = height, BitmapEncoderId = BitmapEncoder.PngEncoderId,
            BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255)
        }).AsTask(ct);
        ct.ThrowIfCancellationRequested();
        byte[] bytes = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size).AsTask(ct);
        reader.ReadBytes(bytes);
        return new PdfRenderedPage(bytes, page.Size.Width, page.Size.Height, (int)width, (int)height);
    }
}
