using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Shunshou.Core;

public enum VideoWatermarkMode { Blur, Cover, Crop, Repair }
public sealed record VideoRegion(int X, int Y, int Width, int Height);
public sealed record VideoInspection(int Width, int Height, double Duration, bool HasAudio, string PreviewPath);

/// <summary>Local rectangular video editing. Coordinates refer to the upright, square-pixel preview.
/// Crop retains the selected rectangle. Repair interpolates neighbouring pixels; it cannot reconstruct hidden detail.</summary>
public sealed class VideoWatermarkService(string? engineDirectory = null)
{
    private const string Normalize = "scale=ceil(iw*sar):ih,setsar=1";
    private static readonly string TemporaryRoot = Path.Combine(Path.GetTempPath(), "Shunshou", "video-edits");

    public async Task<VideoInspection> InspectAsync(string input, string previewPngPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        input = ValidateInput(input);
        previewPngPath = Path.GetFullPath(previewPngPath);
        if (!previewPngPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("视频预览路径必须是 PNG 文件。", nameof(previewPngPath));
        if (File.Exists(previewPngPath)) throw new IOException("预览文件已存在，请使用新的预览路径。");
        var engines = ResolveEngines();
        var source = await ProbeAsync(engines.Probe, input, ct);
        Directory.CreateDirectory(Path.GetDirectoryName(previewPngPath)!);
        var job = CreateJob();
        try
        {
            var preview = Path.Combine(job, "frame.png");
            var dimensions = await ExtractFrameAsync(engines.Ffmpeg, input, source.VideoIndex, preview, ct);
            ct.ThrowIfCancellationRequested();
            File.Copy(preview, previewPngPath, overwrite: false);
            return new(dimensions.Width, dimensions.Height, source.Duration, source.AudioChannels > 0, previewPngPath);
        }
        finally { DeleteJob(job); }
    }

    public Task<string> PreviewAsync(string input, string outputDir, VideoWatermarkMode mode,
        VideoRegion region, IProgress<ToolProgress>? progress, CancellationToken ct) =>
        ProcessAsync(input, outputDir, mode, region, true, progress, ct);

    public Task<string> ExportAsync(string input, string outputDir, VideoWatermarkMode mode,
        VideoRegion region, IProgress<ToolProgress>? progress, CancellationToken ct) =>
        ProcessAsync(input, outputDir, mode, region, false, progress, ct);

    private async Task<string> ProcessAsync(string input, string outputDir, VideoWatermarkMode mode,
        VideoRegion region, bool previewOnly, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        input = ValidateInput(input);
        ArgumentNullException.ThrowIfNull(region);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var engines = ResolveEngines();
        progress?.Report(new(2, "正在读取视频画面"));
        var source = await ProbeAsync(engines.Probe, input, ct);
        var job = CreateJob();
        try
        {
            // FFmpeg applies display-matrix rotation before filters. Read the real resulting image dimensions,
            // rather than assuming every input stores a landscape, square-pixel video stream.
            var dimensions = await ExtractFrameAsync(engines.Ffmpeg, input, source.VideoIndex, Path.Combine(job, "frame.png"), ct);
            ValidateRegion(region, dimensions.Width, dimensions.Height, mode);
            if (mode == VideoWatermarkMode.Repair)
                await WriteMaskAsync(Path.Combine(job, "mask.pgm"), dimensions.Width, dimensions.Height, region, ct);

            var duration = previewOnly ? Math.Min(3, source.Duration) : source.Duration;
            var candidate = Path.Combine(job, "edited.mp4");
            var graph = BuildFilter(source.VideoIndex, region, mode);
            var outputWidth = mode == VideoWatermarkMode.Crop ? region.Width : dimensions.Width;
            var outputHeight = mode == VideoWatermarkMode.Crop ? region.Height : dimensions.Height;
            var bitrate = Math.Clamp((long)outputWidth * outputHeight * 6, 1_000_000, 40_000_000);
            var arguments = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-protocol_whitelist", "file,pipe", "-i", input,
                "-filter_complex", graph, "-map", "[out]", "-map", "0:a?", "-map_metadata", "-1", "-map_chapters", "-1",
                "-sn", "-dn", "-c:v", "libopenh264", "-rc_mode", "bitrate", "-allow_skip_frames", "0",
                "-b:v", bitrate.ToString(CultureInfo.InvariantCulture), "-maxrate", bitrate.ToString(CultureInfo.InvariantCulture),
                "-bufsize", (bitrate * 2).ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p", "-fps_mode", "passthrough",
                "-c:a", "aac", "-b:a", "192k", "-metadata:s:v:0", "rotate=0", "-movflags", "+faststart"
            };
            if (previewOnly) arguments.AddRange(["-t", duration.ToString(CultureInfo.InvariantCulture)]);
            arguments.AddRange(["-progress", "pipe:1", "-nostats", candidate]);
            progress?.Report(new(5, previewOnly ? "正在生成 3 秒以内的效果预览" : "正在处理完整视频"));
            await RunAsync(engines.Ffmpeg, arguments, ct, job, seconds =>
                progress?.Report(new(Math.Clamp(5 + 80 * seconds / duration, 5, 85),
                    $"正在处理 · {TimeSpan.FromSeconds(Math.Max(0, seconds)):hh\\:mm\\:ss} / {TimeSpan.FromSeconds(duration):hh\\:mm\\:ss}")));

            progress?.Report(new(88, "正在检查输出画面与音频"));
            var result = await ProbeAsync(engines.Probe, candidate, ct);
            var expectedWidth = outputWidth + outputWidth % 2;
            var expectedHeight = outputHeight + outputHeight % 2;
            if (result.Width != expectedWidth || result.Height != expectedHeight)
                throw new InvalidDataException("输出画面尺寸校验失败，结果未保存。");
            if (Math.Abs(result.Duration - duration) > Math.Max(0.5, duration * 0.01))
                throw new InvalidDataException("输出视频时长不完整，结果未保存。");
            if (result.AudioChannels != source.AudioChannels || result.AudioTracks != source.AudioTracks)
                throw new InvalidDataException("输出音轨或声道数改变，结果未保存。");
            await RunAsync(engines.Ffmpeg,
                ["-hide_banner", "-loglevel", "error", "-nostdin", "-xerror", "-err_detect", "explode", "-protocol_whitelist", "file,pipe",
                 "-i", candidate, "-map", "0:v:0", "-map", "0:a?", "-f", "null", "-"], ct);
            var resultPath = await PublishAsync(candidate, outputDir, input, previewOnly, ct);
            progress?.Report(new(100, previewOnly ? "预览已生成" : "处理完成，已另存为新视频"));
            return resultPath;
        }
        finally { DeleteJob(job); }
    }

    private static string BuildFilter(int videoIndex, VideoRegion r, VideoWatermarkMode mode)
    {
        // Every dynamic filter value is a validated integer. The mask path is a fixed relative name in a private job directory.
        var start = $"[0:{videoIndex}]{Normalize}";
        var end = "pad=ceil(iw/2)*2:ceil(ih/2)*2[out]";
        return mode switch
        {
            VideoWatermarkMode.Crop => $"{start},crop=w={r.Width}:h={r.Height}:x={r.X}:y={r.Y}:exact=1,{end}",
            VideoWatermarkMode.Cover => $"{start},drawbox=x={r.X}:y={r.Y}:w={r.Width}:h={r.Height}:color=black:t=fill,{end}",
            VideoWatermarkMode.Repair => $"{start},format=yuv420p,removelogo=filename=mask.pgm,{end}",
            VideoWatermarkMode.Blur => $"{start},split[base][piece];[piece]crop=w={r.Width}:h={r.Height}:x={r.X}:y={r.Y}:exact=1,gblur=sigma=20:steps=2[blur];[base][blur]overlay=x={r.X}:y={r.Y}:format=yuv444,{end}",
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    private static void ValidateRegion(VideoRegion r, int width, int height, VideoWatermarkMode mode)
    {
        if (r.X < 0 || r.Y < 0 || r.Width < 2 || r.Height < 2 || (long)r.X + r.Width > width || (long)r.Y + r.Height > height)
            throw new ArgumentOutOfRangeException(nameof(r), "请选择画面以内、至少 2 × 2 像素的区域。");
        if (mode != VideoWatermarkMode.Repair) return;
        if (r.X < 2 || r.Y < 2 || (long)r.X + r.Width > width - 2 || (long)r.Y + r.Height > height - 2)
            throw new ArgumentException("邻域修补需要周围的画面像素。贴边水印请使用裁剪、模糊或遮盖。");
        if (Math.Min(r.Width, r.Height) > 128 || (long)r.Width * r.Height > 250_000)
            throw new ArgumentException("邻域修补适合小块固定水印，请缩小选区（较短边不超过 128 像素）。大范围请选择模糊、遮盖或裁剪。");
    }

    private static async Task WriteMaskAsync(string path, int width, int height, VideoRegion region, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"P5\n{width} {height}\n255\n"), ct);
        var blank = new byte[width];
        var marked = new byte[width];
        marked.AsSpan(region.X, region.Width).Fill(255);
        for (var y = 0; y < height; y++)
            await stream.WriteAsync(y >= region.Y && y < region.Y + region.Height ? marked : blank, ct);
    }

    private static string ValidateInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || (Uri.TryCreate(input, UriKind.Absolute, out var uri) && !uri.IsFile))
            throw new ArgumentException("请选择本地视频文件。", nameof(input));
        input = Path.GetFullPath(input);
        if (!File.Exists(input)) throw new FileNotFoundException("找不到视频文件。", input);
        if (File.GetAttributes(input).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("请先将链接或在线占位文件保存为本地普通文件。");
        return input;
    }

    private static async Task<(int Width, int Height)> ExtractFrameAsync(string executable, string input, int videoIndex, string output, CancellationToken ct)
    {
        await RunAsync(executable,
            ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-protocol_whitelist", "file,pipe", "-i", input,
             "-map", $"0:{videoIndex}", "-vf", Normalize, "-frames:v", "1", "-an", "-sn", "-dn", "-update", "1", output], ct);
        var header = new byte[24];
        await using var stream = File.OpenRead(output);
        await stream.ReadExactlyAsync(header, ct);
        if (!header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("无法生成视频画面预览。");
        var width = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4));
        if (width < 2 || height < 2 || (long)width * height > 100_000_000)
            throw new NotSupportedException("视频画面尺寸超出处理范围。");
        return (width, height);
    }

    private sealed record ProbeResult(int VideoIndex, int Width, int Height, double Duration, int AudioChannels, int AudioTracks);

    private static async Task<ProbeResult> ProbeAsync(string executable, string input, CancellationToken ct)
    {
        var text = await RunAsync(executable,
            ["-hide_banner", "-loglevel", "error", "-protocol_whitelist", "file,pipe", "-show_entries",
             "format=duration:stream=index,codec_type,width,height,duration,channels:stream_disposition=attached_pic", "-of", "json", input], ct);
        using var json = JsonDocument.Parse(text);
        JsonElement? video = null;
        var channels = 0;
        var tracks = 0;
        foreach (var stream in json.RootElement.GetProperty("streams").EnumerateArray())
        {
            var type = stream.GetProperty("codec_type").GetString();
            if (type == "audio") { channels += (int)Number(stream, "channels"); tracks++; }
            if (type == "video" && video is null &&
                (!stream.TryGetProperty("disposition", out var disposition) || Number(disposition, "attached_pic") == 0)) video = stream;
        }
        if (video is null) throw new InvalidDataException("该文件没有可处理的视频画面。");
        var duration = Number(video.Value, "duration");
        if (duration <= 0 && json.RootElement.TryGetProperty("format", out var format)) duration = Number(format, "duration");
        if (!double.IsFinite(duration) || duration <= 0) throw new InvalidDataException("无法读取视频完整时长。");
        return new((int)Number(video.Value, "index"), (int)Number(video.Value, "width"), (int)Number(video.Value, "height"), duration, channels, tracks);
    }

    private static double Number(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private (string Ffmpeg, string Probe) ResolveEngines()
    {
        var configured = engineDirectory ?? Environment.GetEnvironmentVariable("SHUNSHOU_FFMPEG_DIR");
        var directory = configured ?? Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "bin");
        if (configured is null && !File.Exists(Path.Combine(directory, "ffmpeg.exe")))
            for (var parent = new DirectoryInfo(AppContext.BaseDirectory); parent is not null; parent = parent.Parent)
            {
                var development = Path.Combine(parent.FullName, "runtime", "ffmpeg", "bin");
                if (!File.Exists(Path.Combine(development, "ffmpeg.exe"))) continue;
                directory = development;
                break;
            }
        var ffmpeg = Path.GetFullPath(Path.Combine(directory, "ffmpeg.exe"));
        var probe = Path.GetFullPath(Path.Combine(directory, "ffprobe.exe"));
        if (!File.Exists(ffmpeg) || !File.Exists(probe)) throw new FileNotFoundException("缺少本地音视频引擎，请完整解压软件。");
        return (ffmpeg, probe);
    }

    private static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken ct,
        string? workingDirectory = null, Action<double>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        if (workingDirectory is not null) info.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException("无法启动本地视频引擎。");
        using var cancellationRegistration = ct.Register(static state =>
        {
            var active = (Process)state!;
            try { if (!active.HasExited) active.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }, process, useSynchronizationContext: false);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var readers = Task.WhenAll(DrainAsync(process.StandardOutput, stdout, progress), DrainAsync(process.StandardError, stderr, null));
        try { await process.WaitForExitAsync(ct); await readers; }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await readers;
            throw;
        }
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            var error = stderr.ToString().Trim();
            if (error.Length > 1400) error = error[..1400];
            throw new InvalidDataException("视频处理失败，请检查文件和选区。\n" + error);
        }
        return stdout.ToString();
    }

    private static async Task DrainAsync(StreamReader reader, StringBuilder result, Action<double>? progress)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (result.Length < 1_000_000) result.AppendLine(line);
            if (progress is not null && line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros)) progress(micros / 1_000_000d);
        }
    }

    private static string CreateJob()
    {
        var directory = Path.Combine(TemporaryRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteJob(string job)
    {
        var resolved = Path.GetFullPath(job);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(TemporaryRoot) || !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _)) return;
        try { if (Directory.Exists(resolved)) Directory.Delete(resolved, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task<string> PublishAsync(string candidate, string directory, string input, bool previewOnly, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var stem = Path.GetFileNameWithoutExtension(input);
        if (stem.Length > 70) stem = stem[..70];
        var output = Path.Combine(directory, $"{stem}_{(previewOnly ? "preview" : "edited")}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}.mp4");
        var partial = output + ".partial";
        try
        {
            await using (var source = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
            await using (var target = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
                await source.CopyToAsync(target, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(partial, output, false);
            return output;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
}
