using System.Text.RegularExpressions;
using ImageMagick;
using SkiaSharp;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace Shunshou.Core;

/// <summary>Prefers bundled multilingual PP-OCR models; Windows OCR is the offline fallback.</summary>
public sealed class OcrService
{
    private readonly string modelDirectory;
    internal bool HasBundledModels => File.Exists(Path.Combine(modelDirectory, "v6", "PP-OCRv6_rec_small.onnx"));
    public string EngineDescription { get; private set; } = "内置中英文 OCR";

    public OcrService(string? modelDirectory = null)
    {
        this.modelDirectory = modelDirectory ?? Path.Combine(AppContext.BaseDirectory, "tools", "ocr");
    }

    public async Task<string> RecognizeAsync(string imagePath, CancellationToken ct = default)
    {
        var result = await RecognizeLayoutAsync(imagePath, ct).ConfigureAwait(false);
        if (result.Blocks.Count == 0) throw new InvalidOperationException("没有识别到文字，请检查图片中是否包含清晰文字。");
        return result.Text;
    }

    /// <summary>Returns text, source coordinates and confidence. A blank image returns no blocks.</summary>
    public async Task<OcrImageResult> RecognizeLayoutAsync(string imagePath, CancellationToken ct = default)
    {
        using var session = CreateSession();
        return await RecognizeLayoutAsync(imagePath, session, ct).ConfigureAwait(false);
    }

    internal OcrSession CreateSession() => new(modelDirectory);

    internal async Task<OcrImageResult> RecognizeLayoutAsync(string imagePath, OcrSession session, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(imagePath)) throw new FileNotFoundException("找不到需要识别的图片。", imagePath);
        // Skia decodes common screenshot formats. A TIFF's first page is normalized
        // locally with the already-bundled Magick decoder, at its original dimensions.
        using var tiff = IsTiff(imagePath) ? await PrepareTiffAsync(imagePath, ct).ConfigureAwait(false) : null;
        imagePath = tiff?.Path ?? imagePath;
        if (File.Exists(Path.Combine(modelDirectory, "v6", "PP-OCRv6_rec_small.onnx")))
        {
            EngineDescription = "内置 PP-OCRv6 中英文模型（离线）";
            return await RecognizeWithRapidAsync(imagePath, session, ct).ConfigureAwait(false);
        }
        EngineDescription = "Windows 本机 OCR 语言组件（离线后备）";
        return await RecognizeWithWindowsAsync(imagePath, ct).ConfigureAwait(false);
    }

    /// <summary>Reads only image metadata. GIF and TIFF recognition uses the first frame/page.</summary>
    public static (int Width, int Height) ReadImageDimensions(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到这张图片，请重新添加。", path);
        if (IsTiff(path))
        {
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var probe = new MagickImage();
            probe.Ping(source, TiffFirstPageSettings());
            ValidateImageDimensions(probe.Width, probe.Height);
            return ((int)probe.Width, (int)probe.Height);
        }
        using var codec = SKCodec.Create(path) ?? throw new InvalidDataException("无法读取这张图片，请选择完整的 PNG、JPG、WebP、BMP、TIFF 或 GIF 图片。");
        ValidateImageDimensions((uint)codec.Info.Width, (uint)codec.Info.Height);
        return (codec.Info.Width, codec.Info.Height);
    }

    private static bool IsTiff(string path) => Path.GetExtension(path).ToLowerInvariant() is ".tif" or ".tiff";
    private static MagickReadSettings TiffFirstPageSettings() => new() { Format = MagickFormat.Tiff, FrameIndex = 0, FrameCount = 1 };
    private static void ValidateImageDimensions(uint width, uint height)
    {
        if (width == 0 || height == 0 || (ulong)width * height > 100_000_000)
            throw new InvalidOperationException("图片超过一亿像素或尺寸无效，请裁剪需要识别的区域。");
    }

    private static Task<TemporaryTiffInput> PrepareTiffAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        TemporaryTiffInput? temporary = null;
        try
        {
            // Keep the same read-only handle across ping and decode, preventing a
            // writer from replacing dimensions between the bound check and allocation.
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var image = new MagickImage();
            var settings = TiffFirstPageSettings();
            image.Ping(source, settings);
            ValidateImageDimensions(image.Width, image.Height);
            ct.ThrowIfCancellationRequested();
            source.Position = 0;
            image.Read(source, settings);
            image.AutoOrient();
            ct.ThrowIfCancellationRequested();
            string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ShunshouToolbox", "ocr-tiff");
            Directory.CreateDirectory(directory);
            temporary = new TemporaryTiffInput(System.IO.Path.Combine(directory, Guid.NewGuid().ToString("N") + ".png"));
            using (var output = new FileStream(temporary.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                image.Write(output, MagickFormat.Png);
            ct.ThrowIfCancellationRequested();
            return temporary;
        }
        catch { temporary?.Dispose(); throw; }
    }, ct);

    private sealed class TemporaryTiffInput(string path) : IDisposable
    {
        public string Path { get; } = path;
        public void Dispose()
        {
            try { File.Delete(Path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<OcrImageResult> RecognizeWithRapidAsync(string imagePath, OcrSession session, CancellationToken ct)
    {
        using var codec = SKCodec.Create(imagePath) ?? throw new InvalidDataException("无法读取需要识别的图片。");
        if (codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > 100_000_000)
            throw new InvalidOperationException("图片超过一亿像素，请裁剪需要识别的区域或分批识别。");
        ct.ThrowIfCancellationRequested();
        using var bitmap = SKBitmap.Decode(codec) ?? throw new InvalidDataException("无法解码需要识别的图片。");
        var engine = await session.GetEngineAsync(ct).ConfigureAwait(false);
        const int limit = 2600, overlap = 200, step = limit - overlap;
        var lines = new List<TextLayoutBlock>();
        for (int top = 0; top < bitmap.Height; top += step)
        {
            ct.ThrowIfCancellationRequested();
            int height = Math.Min(limit, bitmap.Height - top);
            using var strip = new SKBitmap();
            if (!bitmap.ExtractSubset(strip, new SKRectI(0, top, bitmap.Width, top + height)))
                throw new InvalidDataException("无法读取图片像素区域。");
            var result = await engine.DetectAsync(strip, RapidOcrNet.RapidOcrOptions.PPOCRv6, null, ct).ConfigureAwait(false);
            foreach (var block in result.TextBlocks)
            {
                if (string.IsNullOrWhiteSpace(block.Text)) continue;
                double center = block.BoxPoints.Average(p => p.Y);
                if (top > 0 && center < overlap / 2.0) continue;
                if (top + height < bitmap.Height && center >= step + overlap / 2.0) continue;
                double x = Math.Max(0, block.BoxPoints.Min(p => p.X));
                double y = Math.Max(0, block.BoxPoints.Min(p => p.Y));
                double right = Math.Min(bitmap.Width, block.BoxPoints.Max(p => p.X));
                double bottom = Math.Min(height, block.BoxPoints.Max(p => p.Y));
                double? confidence = block.CharScores is { Length: > 0 } scores ? scores.Average(v => (double)v) : null;
                lines.Add(new TextLayoutBlock(block.Text.Trim(), x, top + y, right - x, bottom - y, Confidence: confidence));
            }
            if (top + height == bitmap.Height) break;
        }
        ct.ThrowIfCancellationRequested();
        // Retry only a bounded number of uncertain, small text crops. The v6
        // preset already resizes small inputs; cropping avoids whole-image caps
        // discarding small letters and keeps normal screenshots fast.
        foreach (int index in Enumerable.Range(0, lines.Count).Where(i => lines[i].Confidence is < 0.8 && lines[i].Height < 48).Take(6))
        {
            ct.ThrowIfCancellationRequested();
            var original = lines[index];
            var bounds = new SKRectI(Math.Max(0, (int)original.X - 8), Math.Max(0, (int)original.Y - 8),
                Math.Min(bitmap.Width, (int)Math.Ceiling(original.Right) + 8), Math.Min(bitmap.Height, (int)Math.Ceiling(original.Bottom) + 8));
            if ((long)bounds.Width * bounds.Height > 1_000_000) continue;
            using var crop = new SKBitmap();
            if (!bitmap.ExtractSubset(crop, bounds)) continue;
            var retry = await engine.DetectAsync(crop, RapidOcrNet.RapidOcrOptions.PPOCRv6, null, ct).ConfigureAwait(false);
            // A single matching line is a conservative replacement; never join
            // unrelated nearby lines or treat another engine's score as comparable.
            if (retry.TextBlocks.Length != 1) continue;
            var candidate = retry.TextBlocks[0];
            double score = candidate.CharScores is { Length: > 0 } values ? values.Average(v => (double)v) : 0;
            if (score >= (original.Confidence ?? 0) + 0.07 && candidate.Text.Length >= original.Text.Length * 0.7
                && candidate.Text.Length <= original.Text.Length * 1.3)
                lines[index] = original with { Text = candidate.Text.Trim(), Confidence = score };
        }
        return new OcrImageResult(bitmap.Width, bitmap.Height, TextLayout.Merge(lines));
    }

    private static async Task<OcrImageResult> RecognizeWithWindowsAsync(string imagePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(imagePath)) throw new FileNotFoundException("找不到需要识别的图片。", imagePath);
        var languages = OcrEngine.AvailableRecognizerLanguages;
        var preferred = languages.FirstOrDefault(x => x.LanguageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
        var engine = preferred is null ? OcrEngine.TryCreateFromUserProfileLanguages() : OcrEngine.TryCreateFromLanguage(preferred);
        engine ??= languages.Count > 0 ? OcrEngine.TryCreateFromLanguage(languages[0]) : null;
        if (engine is null)
            throw new InvalidOperationException("此电脑尚未安装 Windows OCR 语言组件。请在 Windows 设置的语言选项中安装中文或英文的“光学字符识别”组件后重试；工具不会上传图片。安装组件后即可离线识别。");
        var englishLanguage = languages.FirstOrDefault(x => x.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        var englishEngine = preferred is not null && englishLanguage is not null
            ? OcrEngine.TryCreateFromLanguage(englishLanguage) : null;

        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(imagePath));
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        uint limit = OcrEngine.MaxImageDimension;
        if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0) throw new InvalidDataException("图片尺寸无效。");
        if (decoder.PixelWidth > limit)
            throw new InvalidOperationException($"图片宽度为 {decoder.PixelWidth} 像素，超过此电脑 OCR 的 {limit} 像素限制。请先裁剪需要识别的区域；软件不会自动缩小图片损失文字细节。");

        // A long screenshot is recognized in overlapping vertical strips at its original resolution.
        // Boundary ownership by each line's midpoint prevents duplicate lines from the overlap.
        uint overlap = Math.Min(160U, limit / 8);
        uint step = limit - overlap;
        var lines = new List<TextLayoutBlock>();
        for (uint top = 0; top < decoder.PixelHeight; top += step)
        {
            ct.ThrowIfCancellationRequested();
            uint height = Math.Min(limit, decoder.PixelHeight - top);
            var transform = new BitmapTransform { Bounds = new BitmapBounds { X = 0, Y = top, Width = decoder.PixelWidth, Height = height } };
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb);
            var result = await engine.RecognizeAsync(bitmap);
            var englishResult = englishEngine is null ? null : await englishEngine.RecognizeAsync(bitmap);
            ct.ThrowIfCancellationRequested();
            foreach (var line in result.Lines)
            {
                if (line.Words.Count == 0 || string.IsNullOrWhiteSpace(line.Text)) continue;
                double center = line.Words.Average(w => w.BoundingRect.Y + w.BoundingRect.Height / 2);
                if (top > 0 && center < overlap / 2.0) continue;
                if (top + height < decoder.PixelHeight && center >= step + overlap / 2.0) continue;
                string recognized = SelectLineText(line, englishResult);
                // Windows Chinese OCR inserts word-boundary spaces between individual Han characters.
                recognized = Regex.Replace(recognized, @"(?<=[\u3400-\u9fff])[ \t]+(?=[\u3400-\u9fff])", "");
                double x = line.Words.Min(w => w.BoundingRect.X), y = line.Words.Min(w => w.BoundingRect.Y);
                double right = line.Words.Max(w => w.BoundingRect.Right), bottom = line.Words.Max(w => w.BoundingRect.Bottom);
                lines.Add(new TextLayoutBlock(recognized.Trim(), x, top + y, right - x, bottom - y));
            }
            if (top + height == decoder.PixelHeight) break;
        }

        return new OcrImageResult((int)decoder.PixelWidth, (int)decoder.PixelHeight, TextLayout.Merge(lines));
    }

    private static string SelectLineText(OcrLine primary, OcrResult? english)
    {
        if (english is null) return primary.Text;
        string compact = new(primary.Text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        int han = compact.Count(c => c is >= '\u3400' and <= '\u9fff');
        int latin = compact.Count(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9');
        // Keep Chinese and mixed Chinese prose. The English pass repairs Latin-dominated lines
        // such as file names where the Chinese recognizer mistakes narrow Latin glyphs for Han.
        if (latin < 3 || han * 3 >= compact.Length) return primary.Text;
        if (han > 0 && !compact.Any(c => c is '丨' or '丿' or '丶' or '乚')) return primary.Text;
        double center = primary.Words.Average(w => w.BoundingRect.Y + w.BoundingRect.Height / 2);
        double height = primary.Words.Max(w => w.BoundingRect.Height);
        var candidate = english.Lines.Where(l => l.Words.Count > 0)
            .OrderBy(l => Math.Abs(l.Words.Average(w => w.BoundingRect.Y + w.BoundingRect.Height / 2) - center))
            .FirstOrDefault();
        if (candidate is null) return primary.Text;
        double distance = Math.Abs(candidate.Words.Average(w => w.BoundingRect.Y + w.BoundingRect.Height / 2) - center);
        int candidateLatin = candidate.Text.Count(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9');
        return distance <= height * 0.65 && candidateLatin >= latin ? candidate.Text : primary.Text;
    }
}

/// <summary>One batch owns one lazy model session. No process-wide retained model.</summary>
internal sealed class OcrSession(string directory) : IDisposable
{
    private RapidOcrNet.RapidOcr? engine;
    public async Task<RapidOcrNet.RapidOcr> GetEngineAsync(CancellationToken ct)
    {
        if (engine is not null) return engine;
        var models = RapidOcrNet.RapidOcrModelSet.PPOCRv6Small with
        {
            DetModelPath = Path.Combine(directory, "v6", "PP-OCRv6_det_small.onnx"),
            RecModelPath = Path.Combine(directory, "v6", "PP-OCRv6_rec_small.onnx"),
            ClsModelPath = Path.Combine(directory, "v5", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
            KeysPath = Path.Combine(directory, "v6", "ppocrv6_dict.txt")
        };
        foreach (string model in new[] { models.DetModelPath, models.RecModelPath, models.ClsModelPath, models.KeysPath })
            if (!File.Exists(model)) throw new InvalidOperationException("内置 OCR 文件不完整，请重新解压完整软件包。缺少：" + Path.GetFileName(model));
        var created = new RapidOcrNet.RapidOcr();
        try
        {
            await Task.Run(() => created.InitModels(models, numThread: Math.Clamp(Environment.ProcessorCount / 2, 1, 4)), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return engine = created;
        }
        catch { created.Dispose(); throw; }
    }
    public void Dispose() { engine?.Dispose(); engine = null; }
}
