using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Shunshou.Core;

/// <summary>Offline conversion using a private FFmpeg process. Target-size output always keeps the full duration.</summary>
public sealed partial class MediaService(string? engineDirectory = null)
{
    private static readonly string TemporaryRoot = Path.Combine(Path.GetTempPath(), "Shunshou", "media-jobs");
    private static readonly int[] Mp3Rates = [32_000, 40_000, 48_000, 56_000, 64_000, 80_000, 96_000, 112_000, 128_000, 160_000, 192_000, 224_000, 256_000, 320_000];

    public async Task<string> ConvertAsync(string input, string outputDir, string format, long? maxBytes,
        IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        input = Path.GetFullPath(input);
        if (!File.Exists(input)) throw new FileNotFoundException("请选择本地音频或视频文件。", input);
        if (File.GetAttributes(input).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("请先将链接或在线占位文件保存为本地普通文件。");
        format = format.Trim().TrimStart('.').ToLowerInvariant();
        if (format is not ("mp3" or "mp4" or "wav" or "flac" or "m4a"))
            throw new NotSupportedException("支持输出 MP3、MP4、WAV、FLAC、M4A。");
        if (maxBytes.HasValue && format is "wav" or "flac")
            throw new ArgumentException("WAV / FLAC 不支持承诺固定目标大小。请选择普通无损编码，或使用 MP3 / M4A 并允许有损处理。");
        if (maxBytes is <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes), "上传上限必须大于零。");

        var engines = ResolveEngines();
        progress?.Report(new(2, "正在本地读取媒体信息"));
        var original = await ProbeAsync(engines.Probe, input, ct);
        var isVideo = format == "mp4";
        if (!isVideo && original.AudioDuration > 0) original = original with { Duration = original.AudioDuration };
        if (isVideo && !original.HasVideo) throw new InvalidDataException("MP4 视频转换需要包含视频画面的输入。纯音频请选择 MP3、M4A、WAV 或 FLAC。");
        if (!isVideo && !original.HasAudio) throw new InvalidDataException("输入文件没有可提取的音频轨道。");
        if (format == "flac" && (original.SampleFormat.StartsWith("flt", StringComparison.Ordinal) || original.SampleFormat.StartsWith("dbl", StringComparison.Ordinal)))
            throw new NotSupportedException("FLAC 不能无损保存浮点音频。为避免量化损失，请选择 WAV 保存原始浮点精度。");
        if (!double.IsFinite(original.Duration) || original.Duration <= 0)
            throw new InvalidDataException("无法可靠读取完整时长，已停止转换。请检查文件是否完整。");

        var budget = maxBytes.HasValue ? (long)Math.Floor(maxBytes.Value * 0.975) : (long?)null;
        var audioRate = isVideo ? (original.HasAudio ? 128_000 : 0) : 192_000;
        var videoRate = isVideo ? (int)Math.Clamp((long)original.Width * original.Height * 3, 600_000, 12_000_000) : 0;
        if (budget.HasValue)
        {
            // Reserve container/header space in addition to the user-visible 2.5% upload margin.
            var availableRate = (budget.Value - 16_384) * 8d / original.Duration * 0.96;
            if (isVideo)
            {
                audioRate = original.HasAudio ? (int)Math.Clamp(availableRate * 0.18, 32_000, 128_000) : 0;
                if (availableRate - audioRate < 64_000)
                    throw TooSmall(maxBytes!.Value, original.Duration, "该目标不足以保留完整时长的视频与音频");
                videoRate = (int)Math.Min(availableRate - audioRate, 50_000_000);
            }
            else
            {
                if (availableRate < 32_000) throw TooSmall(maxBytes!.Value, original.Duration, "该目标低于本版本可接受的最低音频码率");
                audioRate = (int)Math.Clamp(availableRate, 32_000, 320_000);
                if (format == "mp3") audioRate = Mp3Rates.Last(rate => rate <= audioRate);
            }
        }

        var job = Path.Combine(TemporaryRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        var candidate = Path.Combine(job, "converted." + format);
        try
        {
            var attempts = budget.HasValue ? 5 : 1;
            long lastLength = 0;
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new(5 + attempt * 14, $"本地转换 {attempt + 1}/{attempts} · " +
                    (format is "mp3" or "m4a" or "mp4" ? "有损编码，完整保留时长" : "WAV / FLAC 编码，完整保留时长")));
                var arguments = BuildArguments(input, candidate, format, original, audioRate, videoRate);
                await RunAsync(engines.Ffmpeg, arguments, ct, seconds =>
                    progress?.Report(new(Math.Min(88, 5 + attempt * 14 + Math.Min(1, seconds / original.Duration) * 13),
                        $"正在转换 · {TimeSpan.FromSeconds(Math.Max(0, seconds)):hh\\:mm\\:ss} / {TimeSpan.FromSeconds(original.Duration):hh\\:mm\\:ss}")));
                if (!File.Exists(candidate) || (lastLength = new FileInfo(candidate).Length) == 0)
                    throw new InvalidDataException("转换引擎没有生成有效文件。");
                if (!budget.HasValue || lastLength <= budget.Value)
                {
                    progress?.Report(new(90, "正在检查完整时长和输出可解码性"));
                    var result = await ProbeAsync(engines.Probe, candidate, ct);
                    ValidateMedia(original, result, isVideo);
                    if (format == "flac" && original.AudioBits > 0 && result.AudioBits != original.AudioBits)
                        throw new InvalidDataException($"FLAC 编码器无法保持原音频 {original.AudioBits} 位精度（输出为 {result.AudioBits} 位）。结果未发布，请选择 WAV。");
                    await RunAsync(engines.Ffmpeg,
                        ["-hide_banner", "-loglevel", "error", "-nostdin", "-xerror", "-err_detect", "explode", "-protocol_whitelist", "file,pipe",
                         "-i", candidate, "-map", "0:v?", "-map", "0:a?", "-f", "null", "-"], ct);
                    var output = await PublishAsync(candidate, outputDir, Path.GetFileNameWithoutExtension(input), format, ct);
                    var actualBytes = new FileInfo(output).Length;
                    if (budget.HasValue && actualBytes > budget.Value) throw new IOException("发布后的文件大小校验失败。");
                    progress?.Report(new(100, $"转换完成 · {actualBytes / 1_000_000d:F2} MB · 完整时长已验证" +
                        (format is "mp3" or "m4a" or "mp4" ? " · 有损编码" : "")));
                    return output;
                }

                // Retry from the original file, never re-encode an already degraded intermediate.
                var factor = Math.Min(0.85, budget.Value / (double)lastLength * 0.90);
                if (isVideo)
                {
                    var next = (int)(videoRate * factor);
                    if (next < 64_000) break;
                    videoRate = next;
                }
                else
                {
                    var next = (int)(audioRate * factor);
                    if (format == "mp3") next = Mp3Rates.Where(rate => rate <= next).DefaultIfEmpty(0).Last();
                    if (next < 32_000 || next >= audioRate) break;
                    audioRate = next;
                }
            }
            throw TooSmall(maxBytes!.Value, original.Duration,
                $"尝试保留完整时长后仍为 {lastLength / 1_000_000d:F2} MB；没有截断时长、丢帧或缩小画面");
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

    private static List<string> BuildArguments(string input, string output, string format, MediaInfo source, int audioRate, int videoRate)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-protocol_whitelist", "file,pipe", "-i", input,
            "-map_metadata", "0", "-sn", "-dn"
        };
        if (format == "mp4")
        {
            arguments.AddRange(["-map", "0:v:0", "-map", "0:a:0?", "-c:v", "libopenh264", "-rc_mode", "bitrate", "-allow_skip_frames", "0",
                "-b:v", videoRate.ToString(CultureInfo.InvariantCulture), "-maxrate", videoRate.ToString(CultureInfo.InvariantCulture),
                "-bufsize", ((long)videoRate * 2).ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p",
                "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-fps_mode", "passthrough", "-c:a", "aac", "-b:a", audioRate.ToString(CultureInfo.InvariantCulture),
                "-movflags", "+faststart"]);
        }
        else
        {
            arguments.AddRange(["-map", "0:a:0", "-vn"]);
            switch (format)
            {
                case "mp3": arguments.AddRange(["-c:a", "libmp3lame", "-b:a", audioRate.ToString(CultureInfo.InvariantCulture)]); break;
                case "m4a": arguments.AddRange(["-c:a", "aac", "-b:a", audioRate.ToString(CultureInfo.InvariantCulture), "-movflags", "+faststart"]); break;
                case "wav":
                    var codec = source.SampleFormat.StartsWith("dbl", StringComparison.Ordinal) ? "pcm_f64le" :
                        source.SampleFormat.StartsWith("flt", StringComparison.Ordinal) ? "pcm_f32le" :
                        source.AudioBits > 16 ? (source.AudioBits > 24 ? "pcm_s32le" : "pcm_s24le") : "pcm_s16le";
                    arguments.AddRange(["-c:a", codec]);
                    break;
                case "flac":
                    arguments.AddRange(["-c:a", "flac", "-compression_level", "8", "-sample_fmt", source.AudioBits > 16 ? "s32" : "s16"]);
                    if (source.AudioBits > 0) arguments.AddRange(["-bits_per_raw_sample", source.AudioBits.ToString(CultureInfo.InvariantCulture)]);
                    break;
            }
            if (format == "mp3" && source.Channels > 2)
                throw new NotSupportedException("该音频包含两个以上声道。MP3 仅支持单声道或立体声，请先选择支持多声道的 M4A、WAV 或 FLAC，避免静默丢失声道。");
        }
        arguments.AddRange(["-progress", "pipe:1", "-nostats", output]);
        return arguments;
    }

    private (string Ffmpeg, string Probe) ResolveEngines()
    {
        var directory = engineDirectory ?? Environment.GetEnvironmentVariable("SHUNSHOU_FFMPEG_DIR") ??
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "bin");
        if (!File.Exists(Path.Combine(directory, "ffmpeg.exe")) && engineDirectory is null && Environment.GetEnvironmentVariable("SHUNSHOU_FFMPEG_DIR") is null)
        {
            for (var parent = new DirectoryInfo(AppContext.BaseDirectory); parent is not null; parent = parent.Parent)
            {
                var developerDirectory = Path.Combine(parent.FullName, "runtime", "ffmpeg", "bin");
                if (!File.Exists(Path.Combine(developerDirectory, "ffmpeg.exe"))) continue;
                directory = developerDirectory;
                break;
            }
        }
        directory = Path.GetFullPath(directory);
        var ffmpeg = Path.Combine(directory, "ffmpeg.exe");
        var probe = Path.Combine(directory, "ffprobe.exe");
        if (!File.Exists(ffmpeg) || !File.Exists(probe))
            throw new FileNotFoundException("缺少离线音视频引擎。请完整解压软件，确保 tools/ffmpeg/bin 内有 ffmpeg.exe、ffprobe.exe 及配套 DLL。");
        return (ffmpeg, probe);
    }

    private sealed record MediaInfo(double Duration, bool HasVideo, bool HasAudio, int Width, int Height, int Channels, int AudioBits, string SampleFormat, double AudioDuration);

    private static async Task<MediaInfo> ProbeAsync(string executable, string input, CancellationToken ct)
    {
        var result = await RunAsync(executable,
            ["-hide_banner", "-loglevel", "error", "-protocol_whitelist", "file,pipe", "-show_entries",
             "format=duration:stream=codec_type,duration,width,height,channels,bits_per_sample,bits_per_raw_sample,sample_fmt", "-of", "json", input], ct);
        using var json = JsonDocument.Parse(result);
        double duration = 0;
        if (json.RootElement.TryGetProperty("format", out var container)) duration = Number(container, "duration");
        bool audio = false, video = false;
        int width = 0, height = 0, channels = 0, bits = 16;
        var sampleFormat = "";
        double audioDuration = 0;
        if (json.RootElement.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                duration = Math.Max(duration, Number(stream, "duration"));
                var type = stream.TryGetProperty("codec_type", out var kind) ? kind.GetString() : null;
                if (type == "audio" && !audio)
                {
                    audio = true;
                    audioDuration = Number(stream, "duration");
                    channels = (int)Number(stream, "channels");
                    bits = (int)Math.Max(Number(stream, "bits_per_sample"), Number(stream, "bits_per_raw_sample"));
                    if (stream.TryGetProperty("sample_fmt", out var sample)) sampleFormat = sample.GetString() ?? "";
                }
                if (type == "video" && !video)
                {
                    video = true;
                    width = (int)Number(stream, "width");
                    height = (int)Number(stream, "height");
                }
            }
        }
        return new(duration, video, audio, width, height, channels, bits, sampleFormat, audioDuration);
    }

    private static double Number(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)) return 0;
        return double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0;
    }

    private static void ValidateMedia(MediaInfo original, MediaInfo result, bool video)
    {
        var tolerance = Math.Max(0.5, original.Duration * 0.01);
        if (Math.Abs(original.Duration - result.Duration) > tolerance)
            throw new InvalidDataException($"转换前后时长不一致（{original.Duration:F2} 秒 / {result.Duration:F2} 秒），未发布结果。");
        if ((video && !result.HasVideo) || (original.HasAudio && !result.HasAudio)) throw new InvalidDataException("转换结果缺少必要的音频或视频轨道。");
        if (video)
        {
            var sameOrientation = result.Width >= original.Width && result.Height >= original.Height;
            var rotated = result.Width >= original.Height && result.Height >= original.Width;
            if (!sameOrientation && !rotated) throw new InvalidDataException("转换结果意外降低了画面尺寸，未发布结果。");
        }
        if (original.HasAudio && original.Channels > 0 && result.Channels != original.Channels)
            throw new InvalidDataException("转换结果声道数改变，未发布结果。");
    }

    private static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken ct, Action<double>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException("无法启动本地音视频引擎。");
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outputTask = DrainAsync(process.StandardOutput, stdout, progress);
        var errorTask = DrainAsync(process.StandardError, stderr, null);
        try
        {
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(outputTask, errorTask);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(outputTask, errorTask);
            throw;
        }
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            var detail = stderr.ToString().Trim();
            if (detail.Length > 1600) detail = detail[..1600];
            throw new InvalidDataException("本地音视频处理失败，请检查文件是否完整或格式是否受支持。\n" + detail);
        }
        return stdout.ToString();
    }

    private static async Task DrainAsync(StreamReader reader, StringBuilder destination, Action<double>? progress)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (destination.Length < 1_000_000) destination.AppendLine(line);
            if (progress is not null && line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros))
                progress(micros / 1_000_000d);
        }
    }

    private static async Task<string> PublishAsync(string candidate, string directory, string stem, string extension, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        if (stem.Length > 70) stem = stem[..70];
        var output = Path.Combine(directory, $"{stem}_converted_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}.{extension}");
        var partial = output + ".partial";
        try
        {
            await using (var source = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
            await using (var destination = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
                await source.CopyToAsync(destination, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(partial, output, false);
            return output;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    private static InvalidOperationException TooSmall(long maxBytes, double seconds, string detail) =>
        new($"无法在保留完整 {seconds:F1} 秒内容的条件下达到 {maxBytes / 1_000_000d:F2} MB 上限。{detail}。请提高目标大小；原文件未改变。");
}
