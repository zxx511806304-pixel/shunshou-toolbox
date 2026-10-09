using System.Runtime.InteropServices;
using ImageMagick;
using SkiaSharp;

namespace Shunshou.Core;

public enum ScreenshotTool { Rectangle, Arrow, Mosaic, Sequence, Text }

/// <summary>One committed annotation, in image pixels. Colors are opaque "#RRGGBB".</summary>
public abstract record ScreenshotShape(string Color, double StrokeWidth);

public sealed record ScreenshotRectangleShape(double X, double Y, double Width, double Height, string Color, double StrokeWidth)
    : ScreenshotShape(Color, StrokeWidth);

public sealed record ScreenshotArrowShape(double StartX, double StartY, double EndX, double EndY, string Color, double StrokeWidth)
    : ScreenshotShape(Color, StrokeWidth);

public sealed record ScreenshotMosaicShape(double X, double Y, double Width, double Height, int BlockSize)
    : ScreenshotShape("#000000", 0);

public sealed record ScreenshotSequenceShape(double CenterX, double CenterY, int Number, double Radius, string Color)
    : ScreenshotShape(Color, 0);

public sealed record ScreenshotTextShape(double X, double Y, string Text, double FontSize, string Color)
    : ScreenshotShape(Color, 0);

/// <summary>
/// Local screenshot annotation engine: a shape list with undo/redo, plus the pixel operations
/// (mosaic, arrows, numbers, text) rendered onto the snapshot without ever touching the source file.
/// </summary>
public sealed class ScreenshotAnnotationService
{
    private const int MaxShapes = 500;
    private const int MaxTextLength = 500;

    private readonly List<ScreenshotShape> _shapes = [];
    private readonly Stack<ScreenshotShape> _redo = [];

    public IReadOnlyList<ScreenshotShape> Shapes => _shapes;
    public bool CanUndo => _shapes.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Add(ScreenshotShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        Validate(shape);
        if (_shapes.Count >= MaxShapes) throw new InvalidOperationException($"一张截图最多添加 {MaxShapes} 个标注。");
        _shapes.Add(shape);
        _redo.Clear();
    }

    public ScreenshotShape? Undo()
    {
        if (_shapes.Count == 0) return null;
        var shape = _shapes[^1];
        _shapes.RemoveAt(_shapes.Count - 1);
        _redo.Push(shape);
        return shape;
    }

    public ScreenshotShape? Redo()
    {
        if (_redo.Count == 0) return null;
        var shape = _redo.Pop();
        _shapes.Add(shape);
        return shape;
    }

    public void Clear()
    {
        _shapes.Clear();
        _redo.Clear();
    }

    private static void Validate(ScreenshotShape shape)
    {
        switch (shape)
        {
            case ScreenshotRectangleShape rectangle:
                if (rectangle.Width < 2 || rectangle.Height < 2) throw new ArgumentException("矩形至少需要 2 × 2 像素。");
                break;
            case ScreenshotArrowShape arrow:
                if (ArrowLength(arrow.StartX, arrow.StartY, arrow.EndX, arrow.EndY) < 8)
                    throw new ArgumentException("箭头太短，请拖出更长的线段。");
                break;
            case ScreenshotMosaicShape mosaic:
                if (mosaic.Width < 4 || mosaic.Height < 4) throw new ArgumentException("马赛克区域至少需要 4 × 4 像素。");
                if (mosaic.BlockSize is < 4 or > 200) throw new ArgumentOutOfRangeException(nameof(mosaic), "马赛克块大小应在 4–200 像素之间。");
                break;
            case ScreenshotSequenceShape sequence:
                if (sequence.Number is < 1 or > 999) throw new ArgumentOutOfRangeException(nameof(sequence), "序号范围是 1–999。");
                break;
            case ScreenshotTextShape text:
                if (string.IsNullOrWhiteSpace(text.Text)) throw new ArgumentException("请输入文字内容。");
                if (text.Text.Length > MaxTextLength) throw new ArgumentException($"文字最多 {MaxTextLength} 个字符。", nameof(text));
                break;
        }
    }

    /// <summary>Average-box pixelation. Writes into a writable RGBA (4 bytes/pixel) buffer in place.</summary>
    public static void ApplyMosaic(Span<byte> rgba, int imageWidth, int imageHeight, int x, int y, int width, int height, int blockSize)
    {
        if (blockSize < 2) throw new ArgumentOutOfRangeException(nameof(blockSize));
        x = Math.Clamp(x, 0, imageWidth); y = Math.Clamp(y, 0, imageHeight);
        width = Math.Clamp(width, 0, imageWidth - x); height = Math.Clamp(height, 0, imageHeight - y);
        if (width == 0 || height == 0) return;
        for (int by = y; by < y + height; by += blockSize)
        {
            for (int bx = x; bx < x + width; bx += blockSize)
            {
                int bw = Math.Min(blockSize, x + width - bx), bh = Math.Min(blockSize, y + height - by);
                long r = 0, g = 0, b = 0, a = 0;
                for (int row = by; row < by + bh; row++)
                {
                    int start = (row * imageWidth + bx) * 4;
                    for (int col = 0; col < bw; col++)
                    {
                        int i = start + col * 4;
                        r += rgba[i]; g += rgba[i + 1]; b += rgba[i + 2]; a += rgba[i + 3];
                    }
                }
                long count = (long)bw * bh;
                byte cr = (byte)(r / count), cg = (byte)(g / count), cb = (byte)(b / count), ca = (byte)(a / count);
                for (int row = by; row < by + bh; row++)
                {
                    int start = (row * imageWidth + bx) * 4;
                    for (int col = 0; col < bw; col++)
                    {
                        int i = start + col * 4;
                        rgba[i] = cr; rgba[i + 1] = cg; rgba[i + 2] = cb; rgba[i + 3] = ca;
                    }
                }
            }
        }
    }

    public static double ArrowLength(double x1, double y1, double x2, double y2) => Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));

    /// <summary>Two head segments meeting at the tip; the head length grows with the stroke, capped by the shaft length.</summary>
    public static ((double X, double Y) Left, (double X, double Y) Right) ArrowHead(double startX, double startY, double endX, double endY, double strokeWidth)
    {
        double dx = endX - startX, dy = endY - startY;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-6) return ((endX, endY), (endX, endY));
        double ux = dx / length, uy = dy / length;
        double headLength = Math.Clamp(strokeWidth * 4.5, 10, Math.Max(10, length * 0.45));
        const double cos30 = 0.86602540378, sin30 = 0.5;
        // Rotate the back-direction (from tip to tail) by ±30° to get the two barbs.
        double bx1 = -ux * cos30 - -uy * sin30, by1 = -ux * sin30 + -uy * cos30;
        double bx2 = -ux * cos30 + -uy * sin30, by2 = ux * sin30 + -uy * cos30;
        return ((endX + bx1 * headLength, endY + by1 * headLength), (endX + bx2 * headLength, endY + by2 * headLength));
    }

    /// <summary>Renders every shape onto the snapshot and saves a PNG. The source file is never modified.</summary>
    public static string RenderAndSave(string snapshotPath, IReadOnlyList<ScreenshotShape> shapes, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        string source = Path.GetFullPath(snapshotPath);
        if (!File.Exists(source)) throw new FileNotFoundException("截图不存在。", source);
        using var image = new MagickImage(source);
        image.AutoOrient();
        image.Alpha(AlphaOption.Set);
        byte[] rgba = image.GetPixels().ToByteArray(PixelMapping.RGBA) ?? throw new InvalidDataException("无法读取截图像素。");
        foreach (var mosaic in shapes.OfType<ScreenshotMosaicShape>())
            ApplyMosaic(rgba, (int)image.Width, (int)image.Height,
                (int)Math.Floor(mosaic.X), (int)Math.Floor(mosaic.Y),
                (int)Math.Ceiling(mosaic.Width), (int)Math.Ceiling(mosaic.Height), mosaic.BlockSize);
        using var mosaicked = new MagickImage(rgba, new PixelReadSettings(image.Width, image.Height, StorageType.Char, PixelMapping.RGBA));
        SkiaRender(mosaicked, shapes);
        string full = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string partial = full + ".partial";
        try
        {
            mosaicked.Write(partial, MagickFormat.Png);
            File.Move(partial, full, true);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
        return full;
    }

    private static void SkiaRender(MagickImage image, IReadOnlyList<ScreenshotShape> shapes)
    {
        if (!shapes.Any(shape => shape is not ScreenshotMosaicShape)) return;
        using var skia = SKBitmap.FromImage(SKImage.FromEncodedData(new ReadOnlySpan<byte>(image.ToByteArray(MagickFormat.Png))));
        if (skia == null) return;
        using var surface = SKSurface.Create(skia.Info);
        using var canvas = surface.Canvas;
        canvas.DrawBitmap(skia, 0, 0);
        foreach (var shape in shapes)
        {
            switch (shape)
            {
                case ScreenshotRectangleShape rectangle:
                {
                    using var paint = new SKPaint { Color = ParseSkColor(rectangle.Color), IsStroke = true, StrokeWidth = (float)Math.Max(1, rectangle.StrokeWidth), IsAntialias = true, Style = SKPaintStyle.Stroke };
                    canvas.DrawRect((float)rectangle.X, (float)rectangle.Y, (float)rectangle.Width, (float)rectangle.Height, paint);
                    break;
                }
                case ScreenshotArrowShape arrow:
                {
                    using var paint = new SKPaint { Color = ParseSkColor(arrow.Color), IsStroke = true, StrokeWidth = (float)Math.Max(1, arrow.StrokeWidth), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
                    canvas.DrawLine((float)arrow.StartX, (float)arrow.StartY, (float)arrow.EndX, (float)arrow.EndY, paint);
                    var (left, right) = ArrowHead(arrow.StartX, arrow.StartY, arrow.EndX, arrow.EndY, arrow.StrokeWidth);
                    canvas.DrawLine((float)arrow.EndX, (float)arrow.EndY, (float)left.X, (float)left.Y, paint);
                    canvas.DrawLine((float)arrow.EndX, (float)arrow.EndY, (float)right.X, (float)right.Y, paint);
                    break;
                }
                case ScreenshotSequenceShape sequence:
                {
                    using var fill = new SKPaint { Color = ParseSkColor(sequence.Color), IsAntialias = true, Style = SKPaintStyle.Fill };
                    canvas.DrawCircle((float)sequence.CenterX, (float)sequence.CenterY, (float)sequence.Radius, fill);
                    using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
                    // Use a system font; fall back to default on non-Windows.
                    float fontSize = (float)Math.Max(8, sequence.Radius * 1.05);
                    using var typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyle.Bold) ?? SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold) ?? SKTypeface.Default;
                    using var font = new SKFont(typeface, fontSize);
                    var text = sequence.Number.ToString();
                    font.MeasureText(text, out SKRect textBounds);
                    float tx = (float)sequence.CenterX - textBounds.MidX, ty = (float)sequence.CenterY - textBounds.MidY;
                    canvas.DrawText(text, tx, ty, SKTextAlign.Left, font, white);
                    break;
                }
                case ScreenshotTextShape text:
                {
                    using var fill = new SKPaint { Color = ParseSkColor(text.Color), IsAntialias = true };
                    float fontSize = (float)Math.Max(8, text.FontSize);
                    using var typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") ?? SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;
                    using var font = new SKFont(typeface, fontSize);
                    canvas.DrawText(text.Text, (float)text.X, (float)text.Y + fontSize, SKTextAlign.Left, font, fill);
                    break;
                }
            }
        }
        using var encoded = surface.Snapshot().Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(encoded.ToArray());
        image.Read(stream, MagickFormat.Png);
    }

    private static SKColor ParseSkColor(string color)
    {
        if (SKColor.TryParse(color, out var result)) return result;
        return SKColor.Parse("#E53935");
    }

    /// <summary>Captures a screen rectangle (physical pixels) through GDI and saves it as PNG.</summary>
    public static string CaptureScreenPng(int left, int top, int width, int height, string outputPath)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width), "截图区域不能为空。");
        nint screen = Native.CreateDC("DISPLAY", null, null, 0);
        if (screen == 0) throw new InvalidOperationException("无法读取屏幕内容。");
        nint dc = 0, bitmap = 0, old = 0;
        try
        {
            dc = Native.CreateCompatibleDC(screen);
            bitmap = Native.CreateCompatibleBitmap(screen, width, height);
            if (dc == 0 || bitmap == 0) throw new InvalidOperationException("无法创建截图缓冲区。");
            old = Native.SelectObject(dc, bitmap);
            if (old == 0 || old == -1) throw new InvalidOperationException("无法创建截图缓冲区。");
            const uint SourceCopyAndLayered = 0x00CC0020 | 0x40000000; // SRCCOPY | CAPTUREBLT
            if (!Native.BitBlt(dc, 0, 0, width, height, screen, left, top, SourceCopyAndLayered))
                throw new InvalidOperationException("无法复制屏幕画面。");
            var info = new Native.BitmapInfo
            {
                Size = 40, Width = width, Height = -height, // negative: top-down rows
                Planes = 1, BitCount = 32, Compression = 0
            };
            byte[] bgra = new byte[(long)width * height * 4];
            if (Native.GetDIBits(dc, bitmap, 0, (uint)height, bgra, ref info, 0) == 0)
                throw new InvalidOperationException("无法读取截图像素。");
            string full = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using var image = new MagickImage(bgra, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.BGRA));
            image.Alpha(AlphaOption.Set);
            image.Write(full, MagickFormat.Png);
            return full;
        }
        finally
        {
            if (old != 0 && old != -1 && dc != 0) Native.SelectObject(dc, old);
            if (bitmap != 0) Native.DeleteObject(bitmap);
            if (dc != 0) Native.DeleteDC(dc);
            Native.DeleteDC(screen);
        }
    }

    /// <summary>Crops a region out of a previously captured snapshot PNG.</summary>
    public static string CropPng(string snapshotPath, int x, int y, int width, int height, string outputPath)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width), "裁剪区域不能为空。");
        string source = Path.GetFullPath(snapshotPath);
        if (!File.Exists(source)) throw new FileNotFoundException("截图不存在。", source);
        using var image = new MagickImage(source);
        int cx = Math.Clamp(x, 0, (int)image.Width - 1), cy = Math.Clamp(y, 0, (int)image.Height - 1);
        uint cw = (uint)Math.Clamp(width, 1, (int)image.Width - cx), ch = (uint)Math.Clamp(height, 1, (int)image.Height - cy);
        image.Crop(new MagickGeometry(cx, cy, cw, ch));
        image.ResetPage();
        string full = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        image.Write(full, MagickFormat.Png);
        return full;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct BitmapInfo
        {
            internal int Size, Width, Height;
            internal short Planes, BitCount;
            internal int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter;
            internal int ColorUsed, ColorImportant;
        }

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] internal static extern nint CreateDC(string driver, string? device, string? port, nint data);
        [DllImport("gdi32.dll")] internal static extern nint CreateCompatibleDC(nint dc);
        [DllImport("gdi32.dll")] internal static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
        [DllImport("gdi32.dll")] internal static extern nint SelectObject(nint dc, nint value);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint value);
        [DllImport("gdi32.dll")] internal static extern bool DeleteDC(nint dc);
        [DllImport("gdi32.dll")] internal static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
        [DllImport("gdi32.dll")] internal static extern int GetDIBits(nint dc, nint bitmap, uint start, uint count, byte[] pixels, ref BitmapInfo info, uint usage);
    }
}
