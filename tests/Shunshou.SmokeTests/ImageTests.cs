using ImageMagick;
using Shunshou.Core;

namespace Shunshou.SmokeTests;

public static class ImageTests
{
    public static async Task RunAsync(string root)
    {
        root = Path.Combine(Path.GetFullPath(root), "images");
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "透明原图.png");
        using (var image = new MagickImage(new MagickColor("#23A5AB80"), 320, 200)) image.Write(source, MagickFormat.Png);
        var before = CompressionTests.HashFile(source);
        var service = new ImageService();
        var outputs = await service.ConvertAsync([source], root, "webp", 160, 90, null, default);
        CompressionTests.Check(outputs.Count == 1 && File.Exists(outputs[0]), "image conversion returns a real output");
        using (var output = new MagickImage(outputs[0]))
        {
            CompressionTests.Check(output.Width == 160 && output.Height == 100, "resize retains aspect ratio");
            CompressionTests.Check(output.Format == MagickFormat.WebP && output.HasAlpha, "WebP conversion retains alpha");
        }
        var pngOutputs = await service.ConvertAsync([source], root, "png", null, 90, null, default);
        using (var output = new MagickImage(pngOutputs[0]))
        using (var original = new MagickImage(source))
            CompressionTests.Check(output.ToByteArray(MagickFormat.Rgba).SequenceEqual(original.ToByteArray(MagickFormat.Rgba)), "PNG conversion keeps RGBA pixels");
        var jpgOutputs = await service.ConvertAsync([source], root, "jpg", null, 95, null, default);
        using (var output = new MagickImage(jpgOutputs[0]))
            CompressionTests.Check(output.Format == MagickFormat.Jpeg && !output.HasAlpha, "JPEG creates a valid non-alpha result");
        CompressionTests.Check(before == CompressionTests.HashFile(source), "image source is never overwritten");
        CompressionTests.Check(pngOutputs[0] != outputs[0], "operations publish independent output folders");

        var gif = Path.Combine(root, "two-frames.gif");
        using (var frames = new MagickImageCollection())
        {
            frames.Add(new MagickImage(MagickColors.Red, 40, 30));
            frames.Add(new MagickImage(MagickColors.Blue, 40, 30));
            frames.Write(gif, MagickFormat.Gif);
        }
        var gifHash = CompressionTests.HashFile(gif);
        var frameOutputs = await service.ConvertAsync([gif], root, "png", null, 90, null, default);
        CompressionTests.Check(frameOutputs.Count == 2, "multi-frame input produces every frame instead of dropping frames");
        using (var first = new MagickImage(frameOutputs[0]))
        using (var second = new MagickImage(frameOutputs[1]))
            CompressionTests.Check(!first.ToByteArray(MagickFormat.Rgb).SequenceEqual(second.ToByteArray(MagickFormat.Rgb)), "frames retain distinct content");
        CompressionTests.Check(CompressionTests.HashFile(gif) == gifHash, "animated original unchanged");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await CompressionTests.Throws<OperationCanceledException>(() => service.ConvertAsync([source], root, "png", null, 90, null, cancellation.Token), "pre-cancelled image conversion");
        await CompressionTests.Throws<NotSupportedException>(() => service.ConvertAsync([source], root, "pdf", null, 90, null, default), "PDF is not routed through ImageMagick delegates");
        Console.WriteLine("PASS: JPG/PNG/WebP images, aspect ratio, transparency, all GIF frames, cancellation and unchanged originals.");
    }
}
