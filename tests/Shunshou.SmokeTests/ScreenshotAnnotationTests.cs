using ImageMagick;
using Shunshou.Core;

namespace Shunshou.SmokeTests;

public static class ScreenshotAnnotationTests
{
    public static async Task RunAsync(string root)
    {
        string folder = Path.Combine(root, "screenshot-annotation");
        Directory.CreateDirectory(folder);
        string source = Path.Combine(folder, "source.png");
        CreateSamplePng(source, 400, 300, new MagickColor("#E8F5E9"));

        var service = new ScreenshotAnnotationService();

        // 1) Undo / redo state
        CompressionTests.Check(!service.CanUndo && !service.CanRedo, "初始状态不可撤销/重做");
        service.Add(new ScreenshotRectangleShape(10, 10, 50, 50, "#E53935", 3));
        CompressionTests.Check(service.CanUndo && !service.CanRedo, "添加矩形后可撤销");
        service.Undo();
        CompressionTests.Check(!service.CanUndo && service.CanRedo, "撤销后可重做");
        service.Redo();
        CompressionTests.Check(service.CanUndo && !service.CanRedo, "重做后回到有形状状态");
        Console.WriteLine("PASS ScreenshotAnnotation: undo/redo state");

        // 2) Shape list ordering
        service.Add(new ScreenshotArrowShape(100, 100, 150, 150, "#1E88E5", 3));
        CompressionTests.Check(service.Shapes.Count == 2, "应有两条形状");
        Console.WriteLine("PASS ScreenshotAnnotation: shape list");

        // 3) Arrow geometry
        var (left, right) = ScreenshotAnnotationService.ArrowHead(100, 100, 150, 150, 3);
        CompressionTests.Check(ScreenshotAnnotationService.ArrowLength(left.X, left.Y, 150, 150) > 5, "箭头左头应非退化");
        CompressionTests.Check(ScreenshotAnnotationService.ArrowLength(right.X, right.Y, 150, 150) > 5, "箭头右头应非退化");
        Console.WriteLine("PASS ScreenshotAnnotation: arrow geometry");

        // 4) Mosaic pixelation — per-pixel checkerboard so every 10×10 block averages to a new value
        var mosaic = new ScreenshotMosaicShape(50, 50, 80, 60, 8);
        var original = CreateRgbaBytes(100, 100,
            (x, y) => (byte)((x + y) % 2 * 255),
            (x, y) => (byte)((x + y) % 2 * 255),
            (x, y) => (byte)0x40,
            (x, y) => (byte)0xFF);
        var copy = original.ToArray();
        ScreenshotAnnotationService.ApplyMosaic(copy.AsSpan(), 100, 100, 0, 0, 100, 100, 10);
        bool mosaicChanged = false;
        for (int i = 0; i < original.Length; i++) if (original[i] != copy[i]) { mosaicChanged = true; break; }
        CompressionTests.Check(mosaicChanged, "马赛克应改变像素值");
        Console.WriteLine("PASS ScreenshotAnnotation: mosaic pixelation");

        // 5) Render and save
        service.Add(mosaic);
        service.Add(new ScreenshotSequenceShape(200, 200, 1, 14, "#43A047"));
        service.Add(new ScreenshotTextShape(20, 250, "标注测试", 18, "#000000"));
        string output = await Task.Run(() => ScreenshotAnnotationService.RenderAndSave(source, service.Shapes, Path.Combine(folder, "annotated.png")));
        CompressionTests.Check(File.Exists(output) && new FileInfo(output).Length > 0, "渲染应生成非空 PNG");
        using (var check = new MagickImage(output))
        {
            CompressionTests.Check(check.Width == 400 && check.Height == 300, "输出尺寸应与源图一致");
        }
        Console.WriteLine("PASS ScreenshotAnnotation: render and save");

        // 6) Clear
        service.Clear();
        CompressionTests.Check(service.Shapes.Count == 0 && !service.CanUndo && !service.CanRedo, "清空后应无形状且不可撤销/重做");
        Console.WriteLine("PASS ScreenshotAnnotation: clear");
    }

    private static void CreateSamplePng(string path, int width, int height, MagickColor fill)
    {
        using var image = new MagickImage(fill, (uint)width, (uint)height);
        image.Write(path, MagickFormat.Png);
    }

    private static byte[] CreateRgbaBytes(int w, int h, Func<int, int, byte> r, Func<int, int, byte> g, Func<int, int, byte> b, Func<int, int, byte> a)
    {
        byte[] buf = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                buf[i] = r(x, y); buf[i + 1] = g(x, y); buf[i + 2] = b(x, y); buf[i + 3] = a(x, y);
            }
        return buf;
    }
}
