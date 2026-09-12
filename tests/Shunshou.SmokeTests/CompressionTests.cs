using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ImageMagick;
using Shunshou.Core;

namespace Shunshou.SmokeTests;

public static class CompressionTests
{
    public static async Task RunBoundaryAsync(string root)
    {
        root = Path.Combine(Path.GetFullPath(root), "compression-boundary");
        var source = Path.Combine(root, "原始资料");
        Directory.CreateDirectory(source);
        var bytes = new byte[19_600];
        new Random(5678).NextBytes(bytes);
        File.WriteAllBytes(Path.Combine(source, "原始文件.bin"), bytes);
        Directory.CreateDirectory(Path.Combine(source, "内部空目录"));
        var service = new CompressionService();
        var result = await service.CompressAsync(source, Path.Combine(root, "output"), 20_000, false, false, null, default);
        Check(result.OutputBytes > 19_500 && result.OutputBytes < 20_000, "fixture lies between the optimization budget and upload limit");
        Check(result.ReachedTarget, "below upload limit counts as success even without the full margin");
        Check(Path.GetFileName(result.OutputPath).StartsWith("原始资料_upload_", StringComparison.Ordinal), "only generated upload suffix is translated");
        Check(result.Message.Contains("未留足"), "reduced margin is explicitly explained");
        var atLimit = await service.CompressAsync(source, Path.Combine(root, "exact-limit"), result.OutputBytes, false, false, null, default);
        Check(atLimit.OutputBytes == result.OutputBytes && !atLimit.ReachedTarget, "exactly equal to strict upload limit is not success");
        Check(Path.GetFileName(atLimit.OutputPath).StartsWith("原始资料_compressed_over_limit_", StringComparison.Ordinal), "unmet target gets English status suffix");
        var packed = await service.CreateZipAsync(source, Path.Combine(root, "packed"), null, default);
        Check(Path.GetFileName(packed).StartsWith("原始资料_packed_", StringComparison.Ordinal), "archive name retains original Chinese stem");
        using (var archive = ZipFile.OpenRead(packed))
        {
            Check(archive.GetEntry("原始文件.bin") is not null && archive.GetEntry("内部空目录/") is not null,
                "user filenames and directory names inside ZIP remain unchanged");
        }
        var extracted = await service.ExtractZipAsync(packed, Path.Combine(root, "extracted"), null, default);
        Check(Path.GetFileName(extracted).Contains("_extracted_", StringComparison.Ordinal), "generated extraction folder suffix is English");
        Check(File.ReadAllBytes(Path.Combine(extracted, "原始文件.bin")).SequenceEqual(bytes)
            && Directory.Exists(Path.Combine(extracted, "内部空目录")), "extraction preserves Chinese structure and original bytes");
        Console.WriteLine("PASS: below-limit output succeeds with a reduced-margin notice; exactly-at-limit output is rejected.");
    }

    public static async Task RunAsync(string root)
    {
        var testRoot = Path.Combine(Path.GetFullPath(root), "compression");
        Directory.CreateDirectory(testRoot);
        await RejectUnsafeArchives(testRoot);
        await UnreachableTargetAndCancellation(testRoot);
        await LosslessTransparentPngAndAnimation(testRoot);
        await RunAdaptiveAsync(testRoot);
        await LargeJpegSubmission(testRoot);
    }

    public static async Task RunAdaptiveAsync(string root)
    {
        root = Path.Combine(Path.GetFullPath(root), "compression-adaptive");
        Directory.CreateDirectory(root);
        await MixedImagesUseIndividualBudgets(root);
        await ResizeNeedsExplicitPermission(root);
        await PreserveUnsupportedImagesAndCancelSearch(root);
    }

    private static async Task MixedImagesUseIndividualBudgets(string root)
    {
        var source = Path.Combine(root, "mixed", "source");
        Directory.CreateDirectory(Path.Combine(source, "学习资料"));
        Directory.CreateDirectory(Path.Combine(source, "空目录"));
        var large = Path.Combine(source, "照片.jpg");
        var small = Path.Combine(source, "已压缩.jpg");
        WriteNoise(large, 1024, 768, 100, 701);
        WriteNoise(small, 320, 240, 35, 702);
        var screenshot = Path.Combine(source, "学习资料", "截图.png");
        var documentJpeg = Path.Combine(source, "学习资料", "文字截图.jpg");
        var pixels = Enumerable.Repeat((byte)255, 1280 * 720 * 3).ToArray();
        // Deterministic fine strokes on a light background; no installed fonts or private documents.
        for (var y = 48; y < 650; y += 24)
            for (var x = 50; x < 1150; x += 11)
                for (var dy = 0; dy < 8; dy++)
                    for (var dx = 0; dx < 6; dx++)
                        if (dx == 0 || dy is 0 or 7 || (x / 11 + y / 24 + dx + dy) % 5 == 0)
                            Array.Fill(pixels, (byte)25, ((y + dy) * 1280 + x + dx) * 3, 3);
        using (var image = new MagickImage(pixels, new MagickReadSettings { Width = 1280, Height = 720, Depth = 8, Format = MagickFormat.Rgb }))
        {
            image.Write(screenshot, MagickFormat.Png);
            image.Quality = 96;
            image.Write(documentJpeg, MagickFormat.Jpeg);
        }
        var transparentPixels = new byte[128 * 64 * 4];
        for (var index = 0; index < transparentPixels.Length; index += 4)
        {
            transparentPixels[index] = 210;
            transparentPixels[index + 1] = 80;
            transparentPixels[index + 2] = 140;
            transparentPixels[index + 3] = (byte)(index / 4 % 256);
        }
        using (var image = new MagickImage(transparentPixels,
            new MagickReadSettings { Width = 128, Height = 64, Depth = 8, Format = MagickFormat.Rgba }))
            image.Write(Path.Combine(source, "学习资料", "透明图.png"), MagickFormat.Png);
        var note = new byte[17_000];
        new Random(703).NextBytes(note);
        File.WriteAllBytes(Path.Combine(source, "学习资料", "附件.bin"), note);
        var originalHashes = Directory.GetFiles(source, "*", SearchOption.AllDirectories).ToDictionary(p => p, HashFile);

        // Define a real archive budget using a fine (non-ten-step) JPEG quality and otherwise
        // unchanged inputs. The service must discover its own candidates through its public API.
        var reference = Path.Combine(root, "mixed", "quality43.jpg");
        using (var image = new MagickImage(large))
        {
            image.Quality = 43;
            image.Write(reference, MagickFormat.Jpeg);
        }
        var budgetZip = Path.Combine(root, "mixed", "reference.zip");
        using (var archive = ZipFile.Open(budgetZip, ZipArchiveMode.Create))
        {
            archive.CreateEntry("空目录/");
            archive.CreateEntry("学习资料/");
            foreach (var path in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                archive.CreateEntryFromFile(path == large ? reference : path,
                    Path.GetRelativePath(source, path).Replace('\\', '/'), CompressionLevel.SmallestSize);
        }
        var maxBytes = (long)Math.Ceiling(new FileInfo(budgetZip).Length / 0.975);
        var result = await new CompressionService().CompressAsync(source, Path.Combine(root, "mixed", "output"),
            maxBytes, true, false, null, default);
        Check(result.ReachedTarget && result.OutputBytes <= Math.Floor(maxBytes * 0.975), "individual image budgets meet real ZIP limit and margin");
        Check(result.FileCount == originalHashes.Count, "mixed image budget retains every file");
        using var zip = ZipFile.OpenRead(result.OutputPath);
        var expectedNames = originalHashes.Keys.Select(p => Path.GetRelativePath(source, p).Replace('\\', '/'))
            .Concat(["空目录/", "学习资料/"]).Order().ToArray();
        Check(zip.Entries.Select(e => e.FullName).Order().SequenceEqual(expectedNames), "mixed image budget preserves all relative paths and empty directories");
        foreach (var name in new[] { "学习资料/截图.png", "学习资料/文字截图.jpg", "学习资料/透明图.png", "已压缩.jpg" })
        {
            using var original = new MagickImage(Path.Combine(source, name.Replace('/', Path.DirectorySeparatorChar)));
            using var stream = zip.GetEntry(name)!.Open();
            using var output = new MagickImage(stream);
            Check(output.Width == original.Width && output.Height == original.Height, "protected or already-small image retains dimensions: " + name);
            Check(output.ToByteArray(MagickFormat.Rgba).SequenceEqual(original.ToByteArray(MagickFormat.Rgba)),
                "protected or already-small image retains exact pixels: " + name);
        }
        using (var stream = zip.GetEntry("照片.jpg")!.Open())
        using (var output = new MagickImage(stream))
        using (var directOriginal = new MagickImage(large))
        {
            Check(output.Width == 1024 && output.Height == 768, "adaptive photo keeps dimensions without resize permission");
            Check(output.Quality > 40 && output.Quality < 50, "fine quality budget avoids dropping the whole archive to quality 40");
            directOriginal.Quality = output.Quality;
            using var direct = new MagickImage(directOriginal.ToByteArray(MagickFormat.Jpeg));
            Check(output.ToByteArray(MagickFormat.Rgb).SequenceEqual(direct.ToByteArray(MagickFormat.Rgb)),
                "selected JPEG is a single encoding from the original, not repeated lossy generations");
            Console.WriteLine($"  Adaptive photo selected quality {output.Quality}; screenshot pixels and already-small JPEG unchanged.");
        }
        using (var stream = zip.GetEntry("学习资料/附件.bin")!.Open())
        using (var memory = new MemoryStream())
        {
            stream.CopyTo(memory);
            Check(memory.ToArray().SequenceEqual(note), "budget allocator reserves and preserves non-image bytes");
        }
        Check(originalHashes.All(pair => HashFile(pair.Key) == pair.Value), "all mixed input originals are byte-identical");
        Console.WriteLine($"PASS: mixed image budget {result.OriginalBytes:N0} -> {result.OutputBytes:N0} bytes, target {maxBytes:N0}; fine quality, protected text and exact originals.");
    }

    private static async Task ResizeNeedsExplicitPermission(string root)
    {
        var source = Path.Combine(root, "resize", "source");
        Directory.CreateDirectory(source);
        var png = Path.Combine(source, "照片仍是PNG.png");
        WriteNoise(png, 640, 480, 100, 710);
        var originalHash = HashFile(png);
        var reference = Path.Combine(root, "resize", "reference.png");
        using (var image = new MagickImage(png))
        {
            image.Resize(352, 264);
            image.Write(reference, MagickFormat.Png);
        }
        var budgetZip = Path.Combine(root, "resize", "reference.zip");
        using (var zip = ZipFile.Open(budgetZip, ZipArchiveMode.Create))
            zip.CreateEntryFromFile(reference, Path.GetFileName(png), CompressionLevel.SmallestSize);
        var target = (long)Math.Ceiling((new FileInfo(budgetZip).Length + 512) / 0.975);
        var service = new CompressionService();
        var lossless = await service.CompressAsync(source, Path.Combine(root, "resize", "lossless"), target, false, false, null, default);
        var noResize = await service.CompressAsync(source, Path.Combine(root, "resize", "no-resize"), target, true, false, null, default);
        foreach (var result in new[] { lossless, noResize })
        {
            Check(!result.ReachedTarget && result.OutputBytes > target, "PNG target is honestly unreachable without resize permission");
            using var zip = ZipFile.OpenRead(result.OutputPath);
            using var stream = zip.GetEntry(Path.GetFileName(png))!.Open();
            using var output = new MagickImage(stream);
            using var original = new MagickImage(png);
            Check(output.Width == 640 && output.Height == 480 &&
                output.ToByteArray(MagickFormat.Rgba).SequenceEqual(original.ToByteArray(MagickFormat.Rgba)),
                "allowing lossy JPEG encoding does not quantize, resize or convert PNG without resize permission");
        }
        var resized = await service.CompressAsync(source, Path.Combine(root, "resize", "authorized"), target, true, true, null, default);
        Check(resized.ReachedTarget && resized.OutputBytes < target, "explicit resize permission enables photo PNG target");
        using (var zip = ZipFile.OpenRead(resized.OutputPath))
        using (var stream = zip.GetEntry(Path.GetFileName(png))!.Open())
        using (var output = new MagickImage(stream))
        {
            Check(output.Format == MagickFormat.Png, "resizing never silently changes PNG format or extension");
            Check(output.Width < 640 && output.Width >= 256 && output.Height < 480, "PNG dimensions shrink only inside explicit conservative resize range");
        }
        Check(HashFile(png) == originalHash, "resizing leaves original PNG byte-identical");
        Console.WriteLine("PASS: lossless/lossy/resize permissions remain separate; impossible PNG target is explicit; authorized resizing preserves PNG format and original.");
    }

    private static async Task PreserveUnsupportedImagesAndCancelSearch(string root)
    {
        var source = Path.Combine(root, "preserve", "source");
        Directory.CreateDirectory(source);
        var highDepth = Path.Combine(source, "16-bit.png");
        using (var image = new MagickImage(MagickColors.CornflowerBlue, 64, 64))
        {
            image.Depth = 16;
            image.Settings.SetDefine(MagickFormat.Png, "bit-depth", "16");
            image.Write(highDepth, MagickFormat.Png);
        }
        Check(File.ReadAllBytes(highDepth)[24] == 16, "high-depth fixture has a real 16-bit PNG IHDR");
        var animation = Path.Combine(source, "animated.webp");
        using (var frames = new MagickImageCollection())
        {
            frames.Add(new MagickImage(MagickColors.Red, 64, 64) { AnimationDelay = 10 });
            frames.Add(new MagickImage(MagickColors.Blue, 64, 64) { AnimationDelay = 20 });
            frames.Write(animation, MagickFormat.WebP);
        }
        var photo = Path.Combine(source, "photo.jpg");
        WriteNoise(photo, 768, 512, 100, 720);
        var originalHashes = Directory.GetFiles(source).ToDictionary(p => Path.GetFileName(p), HashFile);
        var service = new CompressionService();
        var result = await service.CompressAsync(source, Path.Combine(root, "preserve", "output"), 1024, true, true, null, default);
        Check(!result.ReachedTarget && result.OutputBytes > 1024, "unreachable budget never removes unsupported images");
        using (var zip = ZipFile.OpenRead(result.OutputPath))
            foreach (var name in new[] { "16-bit.png", "animated.webp" })
            {
                using var stream = zip.GetEntry(name)!.Open();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                Check(Convert.ToHexString(SHA256.HashData(memory.ToArray())) == originalHashes[name], "unsupported depth or animation is byte-preserved: " + name);
                if (name.EndsWith(".webp"))
                {
                    using var frames = new MagickImageCollection(memory.ToArray());
                    Check(frames.Count == 2, "all animated WebP frames remain available");
                }
            }
        using var cancellation = new CancellationTokenSource();
        var cancelOutput = Path.Combine(root, "preserve", "cancelled");
        var progress = new ImmediateProgress<ToolProgress>(value =>
        {
            if (value.Message.StartsWith("逐图细化编码质量", StringComparison.Ordinal)) cancellation.Cancel();
        });
        await Throws<OperationCanceledException>(() => service.CompressAsync(source, cancelOutput, 1024, true, false,
            progress, cancellation.Token), "cancellation while generating individual image candidates");
        Check(cancellation.IsCancellationRequested && !Directory.Exists(cancelOutput), "mid-search cancellation publishes no partial result");
        Check(originalHashes.All(pair => HashFile(Path.Combine(source, pair.Key)) == pair.Value), "cancelled and impossible adaptive jobs leave originals intact");
        Console.WriteLine("PASS: 16-bit PNG and animated WebP byte-preserved; impossible target honest; mid-search cancellation publishes no result.");
    }

    private static void WriteNoise(string path, uint width, uint height, uint quality, int seed)
    {
        var pixels = new byte[checked((int)(width * height * 3))];
        new Random(seed).NextBytes(pixels);
        using var image = new MagickImage(pixels, new MagickReadSettings { Width = width, Height = height, Depth = 8, Format = MagickFormat.Rgb });
        image.Quality = quality;
        image.Write(path, Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? MagickFormat.Png : MagickFormat.Jpeg);
    }

    private static async Task RejectUnsafeArchives(string root)
    {
        var service = new CompressionService();
        var output = Path.Combine(root, "unsafe-output");
        var cases = new[] { "../outside.txt", "/absolute.txt", "C:/escape.txt", "okay/../../escape.txt", "normal.txt:hidden", "CON.txt" };
        for (var index = 0; index < cases.Length; index++)
        {
            var zip = Path.Combine(root, "unsafe-" + index + ".zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry(cases[index]).Open())) writer.Write("unsafe");
            await Throws<InvalidDataException>(() => service.ExtractZipAsync(zip, output, null, default), "unsafe ZIP path " + cases[index]);
        }
        var duplicate = Path.Combine(root, "duplicate.zip");
        using (var archive = ZipFile.Open(duplicate, ZipArchiveMode.Create))
        {
            archive.CreateEntry("same.txt");
            archive.CreateEntry("SAME.txt");
        }
        await Throws<InvalidDataException>(() => service.ExtractZipAsync(duplicate, output, null, default), "case-colliding ZIP names");
        var link = Path.Combine(root, "link.zip");
        using (var archive = ZipFile.Open(link, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("symlink");
            entry.ExternalAttributes = unchecked((0xA000 | 0x1FF) << 16);
            using var writer = new StreamWriter(entry.Open());
            writer.Write("../outside");
        }
        await Throws<InvalidDataException>(() => service.ExtractZipAsync(link, output, null, default), "symbolic link ZIP entry");

        var oversized = Path.Combine(root, "oversized.zip");
        using (var archive = ZipFile.Open(oversized, ZipArchiveMode.Create))
        {
            archive.CreateEntry("large-one.bin");
            archive.CreateEntry("large-two.bin");
        }
        // Advertise a 6 GB expansion in two central-directory entries, without allocating those bytes.
        var oversizedBytes = File.ReadAllBytes(oversized);
        var patched = 0;
        for (var offset = 0; offset <= oversizedBytes.Length - 46; offset++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(oversizedBytes.AsSpan(offset, 4)) != 0x02014B50) continue;
            BinaryPrimitives.WriteUInt32LittleEndian(oversizedBytes.AsSpan(offset + 24, 4), 3_000_000_000);
            patched++;
        }
        Check(patched == 2, "oversized fixture has exactly two patched entries");
        File.WriteAllBytes(oversized, oversizedBytes);
        await Throws<InvalidDataException>(() => service.ExtractZipAsync(oversized, output, null, default), "over-5-GB ZIP expansion");

        var excessiveCount = Path.Combine(root, "excessive-count.zip");
        using (var archive = ZipFile.Open(excessiveCount, ZipArchiveMode.Create))
            for (var index = 0; index < 20_001; index++) archive.CreateEntry($"{index:D5}.txt");
        await Throws<InvalidDataException>(() => service.ExtractZipAsync(excessiveCount, output, null, default), "over-20,000-entry ZIP");
        Check(!File.Exists(Path.Combine(root, "outside.txt")), "no traversal output exists");
        Check(!Directory.Exists(output), "rejected archives never publish an output directory");
        Console.WriteLine("PASS: ZIP traversal, ADS/device paths, symlink, duplicate names, expansion and item limits rejected.");
    }

    private static async Task UnreachableTargetAndCancellation(string root)
    {
        var source = Path.Combine(root, "non-image-source");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        var random = new byte[128 * 1024];
        new Random(1009).NextBytes(random);
        File.WriteAllBytes(Path.Combine(source, "payload.bin"), random);
        var service = new CompressionService();
        var packed = await service.CreateZipAsync(source, Path.Combine(root, "pack-output"), null, default);
        var hash = HashFile(packed);
        var result = await service.CompressAsync(packed, Path.Combine(root, "unreachable-output"), 1024, true, false, null, default);
        Check(!result.ReachedTarget, "incompressible data is explicitly not at target");
        Check(result.OutputBytes > 1024, "impossible result is not mislabeled small");
        Check(result.FileCount == 1, "incompressible payload is preserved");
        Check(HashFile(packed) == hash, "original archive remains byte-identical");
        using (var archive = ZipFile.OpenRead(result.OutputPath))
        {
            Check(archive.GetEntry("empty/") is not null, "empty directory is preserved");
            var entry = archive.GetEntry("payload.bin") ?? throw new Exception("Payload disappeared.");
            using var content = entry.Open();
            using var memory = new MemoryStream();
            content.CopyTo(memory);
            Check(memory.ToArray().SequenceEqual(random), "non-image bytes are unchanged");
        }
        var extracted = await service.ExtractZipAsync(result.OutputPath, Path.Combine(root, "extract-output"), null, default);
        Check(File.ReadAllBytes(Path.Combine(extracted, "payload.bin")).SequenceEqual(random), "normal extraction preserves payload");
        Check(Directory.Exists(Path.Combine(extracted, "empty")), "normal extraction preserves empty directory");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelOutput = Path.Combine(root, "cancelled-output");
        await Throws<OperationCanceledException>(() => service.CompressAsync(packed, cancelOutput, 100_000, true, false, null, cancelled.Token), "pre-cancelled compression");
        Check(!Directory.Exists(cancelOutput), "pre-cancelled operation creates no output");
        using var during = new CancellationTokenSource();
        var immediateProgress = new ImmediateProgress<ToolProgress>(_ => during.Cancel());
        await Throws<OperationCanceledException>(() => service.CompressAsync(packed, cancelOutput, 100_000, true, false, immediateProgress, during.Token), "cancellation during archive read");
        Check(HashFile(packed) == hash, "cancelled jobs preserve original archive");
        await Throws<ArgumentException>(() => service.CompressAsync(packed, cancelOutput, 100_000, false, true, null, default), "resize cannot claim lossless");
        Console.WriteLine("PASS: impossible target, empty directories, exact non-image bytes, cancellation and originals preserved.");
    }

    private static async Task LosslessTransparentPngAndAnimation(string root)
    {
        var source = Path.Combine(root, "lossless-source");
        Directory.CreateDirectory(source);
        var png = Path.Combine(source, "透明截图.png");
        var pixels = new byte[256 * 128 * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = (byte)((index / 4) % 256);
            pixels[index + 1] = 90;
            pixels[index + 2] = 170;
            pixels[index + 3] = (byte)((index / 4) % 128 * 2);
        }
        using (var image = new MagickImage(pixels, new MagickReadSettings { Width = 256, Height = 128, Depth = 8, Format = MagickFormat.Rgba }))
        {
            image.Settings.SetDefine(MagickFormat.Png, "compression-level", "0");
            image.Write(png, MagickFormat.Png);
        }
        var animation = Path.Combine(source, "animation.gif");
        using (var frames = new MagickImageCollection())
        {
            frames.Add(new MagickImage(MagickColors.Red, 64, 64) { AnimationDelay = 10 });
            frames.Add(new MagickImage(MagickColors.Blue, 64, 64) { AnimationDelay = 20 });
            frames.Write(animation, MagickFormat.Gif);
        }
        var beforePngHash = HashFile(png);
        var beforeGifHash = HashFile(animation);
        var service = new CompressionService();
        var result = await service.CompressAsync(source, Path.Combine(root, "lossless-output"), 1024, false, false, null, default);
        using var archive = ZipFile.OpenRead(result.OutputPath);
        using var pngStream = archive.GetEntry("透明截图.png")!.Open();
        using var original = new MagickImage(png);
        using var output = new MagickImage(pngStream);
        Check(original.Width == output.Width && original.Height == output.Height, "lossless PNG dimensions are unchanged");
        Check(original.ToByteArray(MagickFormat.Rgba).SequenceEqual(output.ToByteArray(MagickFormat.Rgba)), "lossless PNG RGBA pixels are exact");
        Check(output.HasAlpha, "transparent PNG stays transparent");
        using var gifStream = archive.GetEntry("animation.gif")!.Open();
        using var gifMemory = new MemoryStream();
        gifStream.CopyTo(gifMemory);
        Check(Convert.ToHexString(SHA256.HashData(gifMemory.ToArray())) == beforeGifHash, "animation file preserved byte-for-byte");
        using var framesCheck = new MagickImageCollection(gifMemory.ToArray());
        Check(framesCheck.Count == 2, "all GIF frames remain present");
        Check(HashFile(png) == beforePngHash && HashFile(animation) == beforeGifHash, "lossless originals unchanged");
        Console.WriteLine("PASS: lossless PNG exact RGBA pixels and transparency; animated GIF frames and bytes preserved.");
    }

    private static async Task LargeJpegSubmission(string root)
    {
        var source = Path.Combine(root, "large-source");
        var photos = Path.Combine(source, "照片", "原图");
        Directory.CreateDirectory(photos);
        Directory.CreateDirectory(Path.Combine(source, "空文件夹"));
        var note = Encoding.UTF8.GetBytes("这些图片由确定性伪随机像素生成，用于验证完整图片包按上传限制压缩。\n保留此文件内容。\n");
        File.WriteAllBytes(Path.Combine(source, "说明.txt"), note);
        var random = new Random(20260909);
        long imageBytes = 0;
        var count = 0;
        Console.WriteLine("Preparing >100 MB synthetic high-detail JPEG archive (no personal files). ");
        while (imageBytes < 106_000_000)
        {
            var data = new byte[2048 * 1536 * 3];
            random.NextBytes(data);
            using var image = new MagickImage(data, new MagickReadSettings { Width = 2048, Height = 1536, Depth = 8, Format = MagickFormat.Rgb });
            image.Quality = 100;
            var path = Path.Combine(photos, $"图片-{count + 1:D3}.jpg");
            image.Write(path, MagickFormat.Jpeg);
            imageBytes += new FileInfo(path).Length;
            if (++count > 80) throw new Exception("JPEG fixture unexpectedly small.");
        }
        var input = Path.Combine(root, "100MB图片提交.zip");
        ZipFile.CreateFromDirectory(source, input, CompressionLevel.Fastest, false, Encoding.UTF8);
        var originalBytes = new FileInfo(input).Length;
        Check(originalBytes > 100_000_000, "input ZIP itself exceeds 100 decimal MB");
        var beforeHash = HashFile(input);
        string[] names;
        using (var archive = ZipFile.OpenRead(input)) names = archive.Entries.Select(e => e.FullName).Order().ToArray();
        var service = new CompressionService();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var lastReported = -1;
        var progress = new ImmediateProgress<ToolProgress>(value =>
        {
            var step = (int)value.Percent / 10;
            if (step == lastReported) return;
            lastReported = step;
            Console.WriteLine($"  JPEG compression {value.Percent:F0}%: {value.Message}");
        });
        var result = await service.CompressAsync(input, Path.Combine(root, "large-output"), 20_000_000, true, false, progress, default);
        stopwatch.Stop();
        Check(result.ReachedTarget, "100+ MB synthetic JPEG ZIP reaches the 20 MB target");
        Check(result.OutputBytes == new FileInfo(result.OutputPath).Length, "reported ZIP bytes are real file bytes");
        Check(result.OutputBytes <= 19_500_000, "20 MB output includes the declared 2.5% margin");
        Check(result.FileCount == count + 1, "every image and the non-image note remain present");
        Check(HashFile(input) == beforeHash, "100 MB original archive is unchanged");
        using (var archive = ZipFile.OpenRead(result.OutputPath))
        {
            Check(archive.Entries.Select(e => e.FullName).Order().SequenceEqual(names), "every ZIP path and empty directory is preserved");
            using var noteStream = archive.GetEntry("说明.txt")!.Open();
            using var memory = new MemoryStream();
            noteStream.CopyTo(memory);
            Check(memory.ToArray().SequenceEqual(note), "non-image note remains byte-identical");
            foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".jpg")))
            {
                using var content = entry.Open();
                using var image = new MagickImage(content);
                Check(image.Width == 2048 && image.Height == 1536, "all JPEG dimensions retained when resizing is disabled");
            }
        }
        var summary = $"PASS: synthetic JPEG ZIP {originalBytes / 1_000_000d:F2} MB -> {result.OutputBytes / 1_000_000d:F2} MB; {count} images + note, all paths kept, no resize, {stopwatch.Elapsed.TotalSeconds:F1}s.\n{result.Message}";
        File.WriteAllText(Path.Combine(root, "compression-result.txt"), summary, Encoding.UTF8);
        Console.WriteLine(summary);
    }

    internal static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + description);
    }

    internal static async Task Throws<TException>(Func<Task> action, string description) where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        throw new InvalidOperationException("FAILED: expected " + typeof(TException).Name + " for " + description);
    }

    internal static string HashFile(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private sealed class ImmediateProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
