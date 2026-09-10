using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;

namespace Shunshou.App;

/// <summary>Runs inside a real WinUI process, so thumbnail and dispatcher behaviour are exercised.</summary>
internal static class SearchPreviewVerification
{
    public static async Task<IReadOnlyList<string>> RunAsync(DispatcherQueue dispatcher, string fixtureDirectory)
    {
        fixtureDirectory = Path.GetFullPath(fixtureDirectory);
        Directory.CreateDirectory(fixtureDirectory);
        var fixtures = new List<string>();
        foreach (var (extension, format) in new[]
                 { ("png", SKEncodedImageFormat.Png), ("jpg", SKEncodedImageFormat.Jpeg), ("webp", SKEncodedImageFormat.Webp) })
        {
            var path = Path.Combine(fixtureDirectory, "preview-landscape." + extension);
            WriteImage(path, format, 1600, 900);
            fixtures.Add(path);
        }
        var portrait = Path.Combine(fixtureDirectory, "preview-portrait.png");
        WriteImage(portrait, SKEncodedImageFormat.Png, 600, 1400);
        fixtures.Add(portrait);
        var hashes = fixtures.ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
        var checks = new List<string>();
        using var service = new SearchPreviewService(dispatcher);
        foreach (var fixture in fixtures)
        {
            foreach (int size in new[] { 96, 480 })
            {
                var image = await service.LoadAsync(fixture, size);
                RequireThumbnail(image, size, Path.GetFileName(fixture));
                Require(fixture == portrait ? image!.PixelHeight > image.PixelWidth : image!.PixelWidth > image.PixelHeight,
                    "Thumbnail aspect ratio changed: " + fixture);
            }
        }
        checks.Add("JPG, PNG and WebP plus portrait images: real 96/480-pixel thumbnails, bounded dimensions and aspect ratios");

        var concurrent = await Task.WhenAll(Enumerable.Range(0, 18).Select(i => service.LoadAsync(fixtures[i % fixtures.Count], 128)));
        foreach (var result in concurrent) RequireThumbnail(result, 128, "concurrent request");
        checks.Add("Concurrent thumbnail requests complete and remain within requested dimensions");

        var corrupt = Path.Combine(fixtureDirectory, "corrupt.png");
        await File.WriteAllTextAsync(corrupt, "not an image");
        Require(await service.LoadAsync(corrupt, 96) is null, "Corrupt image did not fall back");
        Require(await service.LoadAsync(Path.Combine(fixtureDirectory, "missing.png"), 96) is null, "Missing image did not fall back");
        var remoteTimer = Stopwatch.StartNew();
        Require(await service.LoadAsync(@"\\thumbnail-test.invalid\share\image.png", 96) is null, "UNC path was accepted");
        Require(await service.LoadAsync("https://thumbnail-test.invalid/image.png", 96) is null, "Remote URL was accepted");
        Require(remoteTimer.Elapsed < TimeSpan.FromSeconds(2), "Remote rejection attempted network work");
        checks.Add("Corrupt/missing files and remote paths safely return icon fallback");

        var offline = Path.Combine(fixtureDirectory, "offline.png");
        File.Copy(fixtures[0], offline, overwrite: true);
        var originalAttributes = File.GetAttributes(offline);
        try
        {
            File.SetAttributes(offline, originalAttributes | FileAttributes.Offline);
            Require(await service.LoadAsync(offline, 96) is null, "Offline file was opened for preview");
        }
        finally { File.SetAttributes(offline, originalAttributes); }
        checks.Add("Offline file attributes prevent thumbnail/content reads");

        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            bool observed = false;
            try { await service.LoadAsync(fixtures[0], 96, canceled.Token); }
            catch (OperationCanceledException) { observed = true; }
            Require(observed, "Caller cancellation was swallowed");
        }
        checks.Add("Caller cancellation propagates for recycled rows and changed selection");

        var mutable = Path.Combine(fixtureDirectory, "cache-refresh.png");
        WriteImage(mutable, SKEncodedImageFormat.Png, 1200, 500);
        var before = await service.LoadAsync(mutable, 96);
        RequireThumbnail(before, 96, "initial cache entry");
        Require(before!.PixelWidth > before.PixelHeight, "Initial cache fixture orientation incorrect");
        WriteImage(mutable, SKEncodedImageFormat.Png, 400, 1000);
        File.SetLastWriteTimeUtc(mutable, DateTime.UtcNow.AddSeconds(2));
        var after = await service.LoadAsync(mutable, 96);
        RequireThumbnail(after, 96, "changed cache entry");
        Require(after!.PixelHeight > after.PixelWidth, "Changed file reused stale thumbnail");
        checks.Add("Changed file metadata invalidates cached thumbnail");

        foreach (var fixture in fixtures)
            Require(hashes[fixture].SequenceEqual(SHA256.HashData(File.ReadAllBytes(fixture))), "Preview modified source: " + fixture);
        service.Dispose();
        Require(await service.LoadAsync(fixtures[0], 96) is null, "Disposed service retained active previews");
        checks.Add("Source hashes remain unchanged and disposal cancels further preview work");
        return checks;
    }

    private static void WriteImage(string path, SKEncodedImageFormat format, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(34, 135, 126));
            using var paint = new SKPaint { Color = new SKColor(241, 181, 65), IsAntialias = true };
            canvas.DrawCircle(width * 0.3f, height * 0.5f, Math.Min(width, height) * 0.25f, paint);
        }
        using var encoded = bitmap.Encode(format, 90);
        using var output = File.Create(path);
        encoded.SaveTo(output);
    }

    private static void RequireThumbnail(BitmapImage? image, int maximumSize, string description)
    {
        Require(image is not null, "Thumbnail was null: " + description);
        Require(image!.PixelWidth > 0 && image.PixelHeight > 0 && image.PixelWidth <= maximumSize && image.PixelHeight <= maximumSize,
            $"Thumbnail dimensions exceeded {maximumSize}: {description} ({image.PixelWidth} x {image.PixelHeight})");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
