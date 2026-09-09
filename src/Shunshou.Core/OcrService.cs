using System.Text.RegularExpressions;
using SkiaSharp;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace Shunshou.Core;

/// <summary>Prefers bundled multilingual PP-OCR models; Windows OCR is the offline fallback.</summary>
public sealed class OcrService
{
    private readonly string modelDirectory;
    public string EngineDescription { get; private set; } = "内置中英文 OCR";

    public OcrService(string? modelDirectory = null)
    {
        this.modelDirectory = modelDirectory ?? Path.Combine(AppContext.BaseDirectory, "tools", "ocr");
    }

    public async Task<string> RecognizeAsync(string imagePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(imagePath)) throw new FileNotFoundException("找不到需要识别的图片。", imagePath);
        if (File.Exists(Path.Combine(modelDirectory, "v6", "PP-OCRv6_rec_small.onnx")))
        {
            EngineDescription = "内置 PP-OCRv6 中英文模型（离线）";
            return await RecognizeWithRapidAsync(imagePath, ct).ConfigureAwait(false);
        }
        EngineDescription = "Windows 本机 OCR 语言组件（离线后备）";
        return await RecognizeWithWindowsAsync(imagePath, ct).ConfigureAwait(false);
    }

    private async Task<string> RecognizeWithRapidAsync(string imagePath, CancellationToken ct)
    {
        var models = RapidOcrNet.RapidOcrModelSet.PPOCRv6Small with
        {
            DetModelPath = Path.Combine(modelDirectory, "v6", "PP-OCRv6_det_small.onnx"),
            RecModelPath = Path.Combine(modelDirectory, "v6", "PP-OCRv6_rec_small.onnx"),
            ClsModelPath = Path.Combine(modelDirectory, "v5", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
            KeysPath = Path.Combine(modelDirectory, "v6", "ppocrv6_dict.txt")
        };
        foreach (string model in new[] { models.DetModelPath, models.RecModelPath, models.ClsModelPath, models.KeysPath })
            if (!File.Exists(model)) throw new InvalidOperationException("内置 OCR 文件不完整，请重新解压完整软件包。缺少：" + Path.GetFileName(model));

        using var codec = SKCodec.Create(imagePath) ?? throw new InvalidDataException("无法读取需要识别的图片。");
        if (codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > 100_000_000)
            throw new InvalidOperationException("图片超过一亿像素，请裁剪需要识别的区域或分批识别。");
        ct.ThrowIfCancellationRequested();
        using var bitmap = SKBitmap.Decode(codec) ?? throw new InvalidDataException("无法解码需要识别的图片。");
        using var engine = new RapidOcrNet.RapidOcr();
        await Task.Run(() => engine.InitModels(models, numThread: Math.Clamp(Environment.ProcessorCount / 2, 1, 4)), ct).ConfigureAwait(false);
        const int limit = 2600, overlap = 200, step = limit - overlap;
        var lines = new List<(double Y, double X, string Text)>();
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
                lines.Add((top + center, block.BoxPoints.Min(p => p.X), block.Text.Trim()));
            }
            if (top + height == bitmap.Height) break;
        }
        ct.ThrowIfCancellationRequested();
        string text = string.Join(Environment.NewLine, lines.OrderBy(l => Math.Round(l.Y / 12)).ThenBy(l => l.X).Select(l => l.Text));
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("没有识别到文字，请检查图片中是否包含清晰文字。");
        return text;
    }

    private static async Task<string> RecognizeWithWindowsAsync(string imagePath, CancellationToken ct)
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
        var lines = new List<(double Y, string Text)>();
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
                lines.Add((top + center, recognized.Trim()));
            }
            if (top + height == decoder.PixelHeight) break;
        }

        var text = string.Join(Environment.NewLine, lines.OrderBy(x => x.Y).Select(x => x.Text));
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException($"没有识别到文字。当前使用 {engine.RecognizerLanguage.DisplayName} 识别组件；请检查文字清晰度及系统中安装的 OCR 语言。");
        return text;
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
