using System.Globalization;

namespace Shunshou.Core;

public sealed partial class MediaService
{
    /// <summary>
    /// Cuts one section out of a local audio or video file. Copy mode never re-encodes and therefore
    /// aligns to key frames; accurate mode re-encodes and matches the requested window exactly.
    /// </summary>
    public async Task<string> TrimAsync(string input, string outputDir, double startSeconds, double endSeconds,
        bool copyStreams, string format, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        input = Path.GetFullPath(input);
        if (!File.Exists(input)) throw new FileNotFoundException("请选择本地音频或视频文件。", input);
        if (File.GetAttributes(input).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("请先将链接或在线占位文件保存为本地普通文件。");
        format = format.Trim().TrimStart('.').ToLowerInvariant();
        if (format is not ("mp4" or "mp3" or "m4a" or "wav" or "flac"))
            throw new NotSupportedException("截取片段支持输出 MP4、MP3、M4A、WAV、FLAC。");
        if (copyStreams && format is "wav" or "flac")
            throw new ArgumentException("WAV / FLAC 容器不能直接复制原视频音轨，请改用精确模式或选择 MP3 / M4A。");
        if (!double.IsFinite(startSeconds) || !double.IsFinite(endSeconds)) throw new ArgumentException("请填写有效的起止时间。");
        if (startSeconds < 0) throw new ArgumentException("起点不能小于 0。");
        if (endSeconds <= startSeconds) throw new ArgumentException("终点必须晚于起点。");

        var engines = ResolveEngines();
        progress?.Report(new(2, "正在本地读取媒体信息"));
        var source = await ProbeAsync(engines.Probe, input, ct);
        if (!double.IsFinite(source.Duration) || source.Duration <= 0)
            throw new InvalidDataException("无法可靠读取完整时长，已停止截取。请检查文件是否完整。");
        bool video = format == "mp4";
        if (video && !source.HasVideo) throw new InvalidDataException("MP4 需要包含画面的输入；纯音频请选择音频格式。");
        if (!video && !source.HasAudio) throw new InvalidDataException("输入文件没有可截取的音频轨道。");
        if (startSeconds >= source.Duration) throw new ArgumentException($"起点 {Format(startSeconds)} 已超过文件时长 {Format(source.Duration)}。");
        endSeconds = Math.Min(endSeconds, source.Duration);
        double requested = endSeconds - startSeconds;

        string job = Path.Combine(TemporaryRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        string candidate = Path.Combine(job, "clip." + format);
        try
        {
            var arguments = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-protocol_whitelist", "file,pipe",
                "-ss", startSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", input,
                "-t", requested.ToString("0.###", CultureInfo.InvariantCulture),
                "-map_metadata", "0", "-sn", "-dn"
            };
            if (copyStreams) arguments.AddRange(["-c", "copy", "-avoid_negative_ts", "make_zero"]);
            else arguments.AddRange(BuildTrimCodecs(video, source));
            if (format is "mp4" or "m4a") arguments.AddRange(["-movflags", "+faststart"]);
            arguments.AddRange(["-progress", "pipe:1", "-nostats", candidate]);

            progress?.Report(new(8, copyStreams ? "正在无损截取（按关键帧对齐）" : "正在精确截取并重新编码"));
            await RunAsync(engines.Ffmpeg, arguments, ct, seconds =>
                progress?.Report(new(Math.Min(88, 8 + seconds / requested * 80),
                    $"正在截取 · {TimeSpan.FromSeconds(Math.Max(0, seconds)):hh\\:mm\\:ss} / {TimeSpan.FromSeconds(requested):hh\\:mm\\:ss}")));
            if (!File.Exists(candidate) || new FileInfo(candidate).Length == 0)
                throw new InvalidDataException("截取引擎没有生成有效文件。");

            progress?.Report(new(90, "正在检查输出可解码性"));
            var result = await ProbeAsync(engines.Probe, candidate, ct);
            if (result.Duration <= 0) throw new InvalidDataException("截取结果时长为空，未发布结果。");
            if ((video && !result.HasVideo) || (!video && !result.HasAudio)) throw new InvalidDataException("截取结果缺少必要的轨道，未发布结果。");
            if (result.Duration > source.Duration + 0.5) throw new InvalidDataException("截取结果时长异常，未发布结果。");
            double tolerance = copyStreams ? Math.Max(4, requested * 0.6) : Math.Max(0.5, requested * 0.03);
            if (Math.Abs(result.Duration - requested) > tolerance)
                throw new InvalidDataException($"截取结果的时长与所选区间相差过大（{Format(result.Duration)} / {Format(requested)}），未发布结果。");
            await RunAsync(engines.Ffmpeg,
                ["-hide_banner", "-loglevel", "error", "-nostdin", "-xerror", "-err_detect", "explode", "-protocol_whitelist", "file,pipe",
                 "-i", candidate, "-map", "0:v?", "-map", "0:a?", "-f", "null", "-"], ct);

            string output = await PublishAsync(candidate, outputDir, Path.GetFileNameWithoutExtension(input), format, ct);
            string note = copyStreams ? " · 无损复制，起止按关键帧对齐" : " · 精确重编码";
            progress?.Report(new(100, $"截取完成 · 实际时长 {Format(result.Duration)}{note}"));
            return output;
        }
        finally
        {
            var resolved = Path.GetFullPath(job);
            if (Path.GetDirectoryName(resolved) == Path.GetFullPath(TemporaryRoot) && Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
            {
                try { if (Directory.Exists(resolved)) Directory.Delete(resolved, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    internal static string Format(double seconds) => TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss\.f", CultureInfo.InvariantCulture);

    /// <summary>Exposes the shared time parser so a caller can validate input without starting a job.</summary>
    public static bool TryReadClipTimeProbe(string text) => Shunshou.Core.ClipTime.TryParse(text, out double seconds) && seconds > 0;

    /// <summary>Re-encode settings for the accurate mode; video keeps the project's H.264 baseline.</summary>
    private static List<string> BuildTrimCodecs(bool video, MediaInfo source)
    {
        if (video)
        {
            int videoRate = (int)Math.Clamp((long)source.Width * source.Height * 3, 600_000, 12_000_000);
            return ["-map", "0:v:0", "-map", "0:a:0?", "-c:v", "libopenh264", "-rc_mode", "bitrate", "-allow_skip_frames", "0",
                "-b:v", videoRate.ToString(CultureInfo.InvariantCulture), "-maxrate", videoRate.ToString(CultureInfo.InvariantCulture),
                "-bufsize", ((long)videoRate * 2).ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p",
                "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-fps_mode", "passthrough", "-c:a", "aac", "-b:a", "128000"];
        }
        return ["-map", "0:a:0", "-vn", "-c:a", "aac", "-b:a", "192000"];
    }
}
