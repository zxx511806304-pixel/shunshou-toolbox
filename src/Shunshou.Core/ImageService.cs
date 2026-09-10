using ImageMagick;

namespace Shunshou.Core;

/// <summary>Raster image conversion without Ghostscript, external services, or original-file replacement.</summary>
public sealed class ImageService
{
    public Task<IReadOnlyList<string>> ConvertAsync(IEnumerable<string> paths, string outputDir, string format,
        uint? maxWidth, uint quality, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var inputPaths = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (inputPaths.Length == 0) throw new ArgumentException("请先选择图片。");
        if (quality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(quality), "质量范围是 1–100。");
        if (maxWidth is 0) throw new ArgumentOutOfRangeException(nameof(maxWidth), "最大宽度必须大于 0。");
        var outputFormat = ParseOutput(format);
        return Task.Run<IReadOnlyList<string>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            outputDir = Path.GetFullPath(outputDir);
            Directory.CreateDirectory(outputDir);
            var operationDir = Path.Combine(outputDir, $"Images_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}");
            Directory.CreateDirectory(operationDir);
            var outputs = new List<string>();
            for (var index = 0; index < inputPaths.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var input = inputPaths[index];
                if (!File.Exists(input)) throw new FileNotFoundException("图片不存在。", input);
                if (File.GetAttributes(input).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("请先将链接或在线占位图片保存为本地普通文件。");
                var inputFormat = ParseInput(input);
                var settings = new MagickReadSettings { Format = inputFormat };
                using var probe = new MagickImageCollection();
                probe.Ping(input, settings);
                if (probe.Count == 0 || probe.Count > 1000) throw new InvalidDataException("图片为空或包含超过 1,000 帧。");
                ulong pixels = 0;
                foreach (var frame in probe) pixels = checked(pixels + (ulong)frame.Width * frame.Height);
                if (pixels > 100_000_000) throw new InvalidDataException("单个图片文件展开后超过一亿像素，请先拆分或缩小。");
                ct.ThrowIfCancellationRequested();
                using var images = new MagickImageCollection();
                images.Read(input, settings);
                var stem = Path.GetFileNameWithoutExtension(input);
                if (stem.Length > 80) stem = stem[..80];
                if (images.Count > 1 && inputFormat is MagickFormat.Gif or MagickFormat.WebP) images.Coalesce();
                for (var frameIndex = 0; frameIndex < images.Count; frameIndex++)
                {
                    ct.ThrowIfCancellationRequested();
                    var image = images[frameIndex];
                    image.AutoOrient();
                    if (maxWidth.HasValue && image.Width > maxWidth.Value) image.Resize(maxWidth.Value, 0);
                    // JPEG has no transparency: composite against white rather than silently turning it black.
                    if (outputFormat.Format == MagickFormat.Jpeg && image.HasAlpha)
                    {
                        image.BackgroundColor = MagickColors.White;
                        image.Alpha(AlphaOption.Remove);
                    }
                    if (outputFormat.Format is MagickFormat.Jpeg or MagickFormat.WebP) image.Quality = quality;
                    var suffix = images.Count > 1 ? $"_frame{frameIndex + 1:D3}" : "";
                    var output = Path.Combine(operationDir, $"{index + 1:D3}_{stem}{suffix}.{outputFormat.Extension}");
                    using (var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
                        image.Write(destination, outputFormat.Format);
                    ct.ThrowIfCancellationRequested();
                    // Decode the written file to verify that a real image was created.
                    using var check = new MagickImage(output, new MagickReadSettings { Format = outputFormat.Format });
                    if (check.Width != image.Width || check.Height != image.Height)
                        throw new InvalidDataException("转换后图片尺寸校验失败。");
                    outputs.Add(output);
                }
                progress?.Report(new(100d * (index + 1) / inputPaths.Length, $"已转换 {index + 1}/{inputPaths.Length}：{Path.GetFileName(input)}"));
            }
            return outputs;
        }, ct);
    }

    private static (MagickFormat Format, string Extension) ParseOutput(string value) => value.Trim().TrimStart('.').ToLowerInvariant() switch
    {
        "jpg" or "jpeg" => (MagickFormat.Jpeg, "jpg"),
        "png" => (MagickFormat.Png, "png"),
        "webp" => (MagickFormat.WebP, "webp"),
        "bmp" => (MagickFormat.Bmp, "bmp"),
        "tif" or "tiff" => (MagickFormat.Tiff, "tif"),
        _ => throw new NotSupportedException("支持输出 JPG、PNG、WebP、BMP 和 TIFF。")
    };

    private static MagickFormat ParseInput(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".jfif" => MagickFormat.Jpeg,
        ".png" => MagickFormat.Png,
        ".webp" => MagickFormat.WebP,
        ".bmp" => MagickFormat.Bmp,
        ".gif" => MagickFormat.Gif,
        ".tif" or ".tiff" => MagickFormat.Tiff,
        ".ico" => MagickFormat.Ico,
        ".heic" or ".heif" => MagickFormat.Heic,
        ".avif" => MagickFormat.Avif,
        _ => throw new NotSupportedException("请选择 JPG、PNG、WebP、BMP、GIF、TIFF、ICO、HEIC 或 AVIF 图片。PDF 请使用 PDF 工具。")
    };
}
