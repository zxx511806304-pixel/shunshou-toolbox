using ImageMagick;

namespace Shunshou.Core;

public enum CropKind { Ratio, Pixels }

/// <summary>
/// Crop settings. Ratio mode keeps the largest rectangle of the requested shape that still fits the
/// source image; pixel mode uses an exact size and refuses anything larger than the source.
/// </summary>
public sealed record CropOptions(CropKind Kind, string Ratio, string Anchor, uint Width, uint Height)
{
    public static readonly string[] RatioNames = ["1:1 正方形", "4:3 横版", "3:4 竖版", "16:9 宽屏", "9:16 竖屏", "2:3 证件照", "3:2 照片"];
    public static readonly string[] AnchorNames = ["居中", "左上", "右上", "左下", "右下", "上中", "下中", "左中", "右中"];
}

/// <summary>Local batch crop. Outputs keep the source format, name and folder structure stays flat in one new folder.</summary>
public sealed class ImageCropService
{
    public Task<IReadOnlyList<string>> CropAsync(IEnumerable<string> paths, string outputDir, CropOptions options,
        uint quality, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var inputs = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (inputs.Length == 0) throw new ArgumentException("请先选择图片。");
        if (quality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(quality), "质量范围是 1–100。");
        if (options.Kind == CropKind.Pixels && (options.Width < 16 || options.Height < 16))
            throw new ArgumentOutOfRangeException(nameof(options), "裁剪宽高至少 16 像素。");
        (double ratioWidth, double ratioHeight) = ParseRatio(options.Ratio);
        return Task.Run<IReadOnlyList<string>>(() =>
        {
            outputDir = Path.GetFullPath(outputDir);
            Directory.CreateDirectory(outputDir);
            string operationDir = Path.Combine(outputDir, $"Images_Crop_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}");
            Directory.CreateDirectory(operationDir);
            var outputs = new List<string>();
            for (int index = 0; index < inputs.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                string input = inputs[index];
                if (!File.Exists(input)) throw new FileNotFoundException("图片不存在。", input);
                if (File.GetAttributes(input).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("请先将链接或在线占位图片保存为本地普通文件。");
                using var image = new MagickImage(input);
                image.AutoOrient();
                uint sourceWidth = image.Width, sourceHeight = image.Height;
                (uint x, uint y, uint width, uint height) = ResolveGeometry(options.Kind, ratioWidth, ratioHeight,
                    sourceWidth, sourceHeight, options.Width, options.Height, options.Anchor);
                image.Crop(new MagickGeometry((int)x, (int)y, width, height) { IgnoreAspectRatio = true });
                image.ResetPage();
                if (image.Width != width || image.Height != height)
                    throw new InvalidDataException($"裁剪尺寸校验失败：期望 {width}×{height}，实际 {image.Width}×{image.Height}。");
                string extension = Path.GetExtension(input).ToLowerInvariant() switch
                {
                    ".jpg" or ".jpeg" or ".jfif" => ".jpg",
                    ".png" => ".png",
                    ".webp" => ".webp",
                    ".bmp" => ".bmp",
                    ".tif" or ".tiff" => ".tif",
                    _ => ".png"
                };
                if (extension is ".jpg" or ".webp") image.Quality = quality;
                if (extension is ".jpg" && image.HasAlpha)
                {
                    image.BackgroundColor = MagickColors.White;
                    image.Alpha(AlphaOption.Remove);
                }
                string stem = Path.GetFileNameWithoutExtension(input);
                if (stem.Length > 80) stem = stem[..80];
                string output = Path.Combine(operationDir, $"{index + 1:D3}_{stem}_crop{extension}");
                using (var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
                    image.Write(destination);
                using var check = new MagickImage(output);
                if (check.Width != width || check.Height != height)
                    throw new InvalidDataException("裁剪后的图片尺寸校验失败。");
                outputs.Add(output);
                progress?.Report(new(100d * (index + 1) / inputs.Length, $"已裁剪 {index + 1}/{inputs.Length}：{Path.GetFileName(input)}"));
            }
            return outputs;
        }, ct);
    }

    internal static (uint X, uint Y, uint Width, uint Height) ResolveGeometry(CropKind kind, double ratioWidth, double ratioHeight,
        uint sourceWidth, uint sourceHeight, uint requestedWidth, uint requestedHeight, string anchor)
    {
        if (sourceWidth == 0 || sourceHeight == 0) throw new InvalidDataException("图片尺寸无效。");
        (double fractionX, double fractionY) = AnchorFractions(anchor);
        double width, height;
        if (kind == CropKind.Ratio)
        {
            double byWidth = Math.Min(sourceWidth, sourceHeight * ratioWidth / ratioHeight);
            width = Math.Floor(byWidth);
            height = Math.Floor(width * ratioHeight / ratioWidth);
            if (width < 16 || height < 16)
            {
                height = 16;
                width = Math.Max(16, Math.Floor(height * ratioWidth / ratioHeight));
            }
        }
        else
        {
            width = requestedWidth;
            height = requestedHeight;
            if (width > sourceWidth || height > sourceHeight)
                throw new InvalidDataException($"原图只有 {sourceWidth}×{sourceHeight}，无法裁剪出 {width}×{height}。");
        }
        width = Math.Min(width, sourceWidth);
        height = Math.Min(height, sourceHeight);
        uint maxX = (uint)(sourceWidth - width), maxY = (uint)(sourceHeight - height);
        return ((uint)Math.Round(maxX * fractionX), (uint)Math.Round(maxY * fractionY), (uint)width, (uint)height);
    }

    /// <summary>Maps the picker's 位置 names to 0 (start), 0.5 (middle) or 1 (end) on each axis.</summary>
    internal static (double X, double Y) AnchorFractions(string? anchor) => (anchor ?? "").Trim() switch
    {
        "左上" => (0, 0),
        "右上" => (1, 0),
        "左下" => (0, 1),
        "右下" => (1, 1),
        "上中" => (0.5, 0),
        "下中" => (0.5, 1),
        "左中" => (0, 0.5),
        "右中" => (1, 0.5),
        _ => (0.5, 0.5)
    };

    private static (double Width, double Height) ParseRatio(string value)
    {
        string text = (value ?? "").Trim();
        int colon = text.IndexOf(':');
        string first = colon > 0 ? text[..colon] : "";
        string second = colon > 0 ? text[(colon + 1)..].Split(' ')[0] : "";
        if (double.TryParse(first, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double width) &&
            double.TryParse(second, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double height) &&
            width > 0 && height > 0) return (width, height);
        throw new ArgumentException("请选择有效的裁剪比例。", nameof(value));
    }

}
