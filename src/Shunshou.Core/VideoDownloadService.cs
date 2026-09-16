using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Shunshou.Core;

public sealed record VideoDownloadInfo(string Url, string Title, double? Duration,
    IReadOnlyList<int> AvailableHeights, string? ThumbnailUrl);

/// <summary>Downloads a single public HTTP(S) media URL through the bundled, isolated yt-dlp runtime.</summary>
public sealed class VideoDownloadService(string? engineDirectory = null, string? ffmpegDirectory = null)
{
    private static readonly string TemporaryRoot = Path.Combine(Path.GetTempPath(), "Shunshou", "download-jobs");

    public async Task<VideoDownloadInfo> AnalyzeAsync(string url, CancellationToken ct)
    {
        url = ValidateUrl(url);
        ct.ThrowIfCancellationRequested();
        var engines = ResolveEngines();
        var arguments = CommonArguments(engines);
        arguments.AddRange(["--dump-single-json", "--skip-download", "--playlist-items", "1", "--", url]);
        var output = await RunAsync(engines.Python, arguments, engines.Directory, ct);
        using var json = JsonDocument.Parse(output);
        var item = json.RootElement;
        var type = Text(item, "_type");
        if (type is "playlist" or "multi_video" || item.TryGetProperty("entries", out _))
            throw new NotSupportedException("请粘贴一个视频的链接，暂不下载整个账号或播放列表。");
        if (Text(item, "live_status") is "is_live" or "is_upcoming" ||
            (item.TryGetProperty("is_live", out var live) && live.ValueKind == JsonValueKind.True))
            throw new NotSupportedException("暂不录制直播，请使用已经发布的完整视频链接。");
        var heights = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
        if (item.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
            foreach (var format in formats.EnumerateArray())
                if (Text(format, "vcodec") != "none" && Number(format, "height") is > 0 and <= 16384)
                    heights.Add((int)Number(format, "height"));
        if (Number(item, "height") is > 0 and <= 16384) heights.Add((int)Number(item, "height"));
        var duration = Number(item, "duration");
        var thumbnail = Text(item, "thumbnail");
        if (!Uri.TryCreate(thumbnail, UriKind.Absolute, out var thumbnailUri) || thumbnailUri.Scheme is not ("http" or "https")) thumbnail = null;
        return new(url, Text(item, "title") ?? "未命名视频", duration > 0 ? duration : null, heights.ToArray(), thumbnail);
    }

    public async Task<string> DownloadAsync(string url, string outputDirectory, int? maxHeight,
        IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        url = ValidateUrl(url);
        if (maxHeight is < 1 or > 16384) throw new ArgumentOutOfRangeException(nameof(maxHeight));
        ct.ThrowIfCancellationRequested();
        progress?.Report(new(1, "正在读取视频信息"));
        // Recheck the actual URL immediately before downloading: a playlist/live URL must never run indefinitely.
        var metadata = await AnalyzeAsync(url, ct);
        var engines = ResolveEngines();
        var job = Path.Combine(TemporaryRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        try
        {
            var receipt = Path.Combine(job, "completed.jsonl");
            var arguments = CommonArguments(engines);
            var cap = maxHeight.HasValue ? "[height<=" + maxHeight.Value.ToString(CultureInfo.InvariantCulture) + "]" : "";
            arguments.AddRange(["--no-simulate", "--no-overwrites", "--no-continue", "--no-part", "--playlist-items", "1",
                "--format", "bv*" + cap + "+ba/b" + cap,
                "--merge-output-format", "mp4/mkv", "--windows-filenames", "--paths", job,
                "--output", "download.%(ext)s", "--newline", "--progress", "--progress-delta", "0.3",
                "--progress-template", "download:SS_PROGRESS:%(progress.downloaded_bytes)s:%(progress.total_bytes,progress.total_bytes_estimate)s",
                "--print-to-file", "after_move:%(filepath)j", receipt, "--", url]);
            progress?.Report(new(4, "正在下载 · " + metadata.Title));
            await RunAsync(engines.Python, arguments, job, ct, line =>
            {
                if (!line.StartsWith("SS_PROGRESS:", StringComparison.Ordinal)) return;
                var fields = line.Split(':');
                if (fields.Length != 3 || !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var done)) return;
                var known = double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var total) && total > 0;
                progress?.Report(new(known ? Math.Clamp(4 + done / total * 88, 4, 92) : 5,
                    known ? $"正在下载 · {done / 1_000_000:F1} / {total / 1_000_000:F1} MB" : $"正在下载 · {done / 1_000_000:F1} MB"));
            });
            if (!File.Exists(receipt)) throw new InvalidDataException("下载没有完整结束，未发布文件。请稍后重试。");
            var lines = await File.ReadAllLinesAsync(receipt, ct);
            if (lines.Length != 1) throw new InvalidDataException("下载结果不是单个视频，未发布文件。");
            var candidate = Path.GetFullPath(JsonSerializer.Deserialize<string>(lines[0]) ?? "");
            if (!candidate.StartsWith(Path.GetFullPath(job) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(candidate) || File.GetAttributes(candidate).HasFlag(FileAttributes.ReparsePoint) || new FileInfo(candidate).Length == 0)
                throw new InvalidDataException("下载结果路径或文件无效。");
            var extension = Path.GetExtension(candidate).ToLowerInvariant();
            if (extension is not (".mp4" or ".mkv" or ".webm" or ".mov" or ".m4v" or ".avi" or ".flv" or ".ts" or ".mp3" or ".m4a" or ".wav" or ".flac" or ".ogg" or ".opus" or ".aac"))
                throw new InvalidDataException("链接没有生成受支持的音视频文件。");
            progress?.Report(new(94, "正在检查下载结果"));
            var probe = await RunAsync(engines.Probe, ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_entries", "stream=codec_type", "-of", "json", candidate], job, ct);
            using (var document = JsonDocument.Parse(probe))
                if (!document.RootElement.TryGetProperty("streams", out var streams) ||
                    !streams.EnumerateArray().Any(stream => Text(stream, "codec_type") is "audio" or "video"))
                    throw new InvalidDataException("下载结果无法识别为音视频文件，未发布文件。");
            var output = await PublishAsync(candidate, outputDirectory, metadata.Title, extension, ct);
            progress?.Report(new(100, "下载完成 · " + Path.GetFileName(output)));
            return output;
        }
        finally
        {
            var resolved = Path.GetFullPath(job);
            if (Path.GetDirectoryName(resolved) == Path.GetFullPath(TemporaryRoot) && Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
                try { Directory.Delete(resolved, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public static string ValidateUrl(string url)
    {
        url = (url ?? "").Trim();
        if (url.Length > 8192 || url.Any(char.IsControl) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("请粘贴完整的 http 或 https 视频链接。");
        return uri.AbsoluteUri;
    }

    internal sealed record Engines(string Directory, string Python, string JavaScript, string Ffmpeg, string Probe);
    internal Engines ResolveEngines()
    {
        var directory = ResolveDirectory(engineDirectory, "SHUNSHOU_VIDEO_DOWNLOAD_DIR", "video-download", "python.exe");
        var ffmpeg = ResolveDirectory(ffmpegDirectory, "SHUNSHOU_FFMPEG_DIR", Path.Combine("ffmpeg", "bin"), "ffmpeg.exe");
        foreach (var relative in new[] { "python.exe", "yt-dlp", "node.exe" })
            if (!File.Exists(Path.Combine(directory, relative))) throw new FileNotFoundException("下载组件不完整，请重新完整解压软件。", relative);
        if (!File.Exists(Path.Combine(ffmpeg, "ffprobe.exe"))) throw new FileNotFoundException("音视频组件不完整，请重新完整解压软件。");
        return new(directory, Path.Combine(directory, "python.exe"), Path.Combine(directory, "node.exe"), ffmpeg, Path.Combine(ffmpeg, "ffprobe.exe"));
    }

    private static string ResolveDirectory(string? explicitDirectory, string environmentVariable, string relative, string marker)
    {
        var directory = explicitDirectory ?? Environment.GetEnvironmentVariable(environmentVariable) ?? Path.Combine(AppContext.BaseDirectory, "tools", relative);
        if (!File.Exists(Path.Combine(directory, marker)) && explicitDirectory is null && Environment.GetEnvironmentVariable(environmentVariable) is null)
            for (var parent = new DirectoryInfo(AppContext.BaseDirectory); parent is not null; parent = parent.Parent)
            {
                var candidate = Path.Combine(parent.FullName, "runtime", relative);
                if (!File.Exists(Path.Combine(candidate, marker))) continue;
                directory = candidate;
                break;
            }
        return Path.GetFullPath(directory);
    }

    internal static List<string> CommonArguments(Engines engines) =>
        ["-I", Path.Combine(engines.Directory, "yt-dlp"), "--ignore-config", "--no-plugin-dirs", "--no-cache-dir", "--no-remote-components",
         "--no-js-runtimes", "--js-runtimes", "node:" + engines.JavaScript, "--ffmpeg-location", engines.Ffmpeg,
         "--no-playlist", "--no-colors", "--encoding", "utf-8", "--socket-timeout", "20", "--retries", "2", "--fragment-retries", "2", "--abort-on-unavailable-fragments"];

    internal static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory, CancellationToken ct, Action<string>? onLine = null)
    {
        ct.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment.Remove("NODE_OPTIONS");
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException("无法启动下载组件。");
        using var cancellationRegistration = ct.Register(static state =>
        {
            var active = (Process)state!;
            try { if (!active.HasExited) active.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }, process, useSynchronizationContext: false);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outputTask = DrainAsync(process.StandardOutput, stdout, onLine, 32_000_000);
        var errorTask = DrainAsync(process.StandardError, stderr, onLine, 12_000);
        try { await process.WaitForExitAsync(ct); await Task.WhenAll(outputTask, errorTask); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(outputTask, errorTask);
            throw;
        }
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            var error = stderr.ToString().Trim();
            if (error.Length > 1800) error = error[^1800..];
            throw new InvalidDataException("无法处理这个链接，请确认视频公开可访问，或稍后重试。\n" + error);
        }
        return stdout.ToString();
    }

    private static async Task DrainAsync(StreamReader reader, StringBuilder buffer, Action<string>? callback, int maximum)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (buffer.Length < maximum) buffer.AppendLine(line[..Math.Min(line.Length, maximum - buffer.Length)]);
            callback?.Invoke(line);
        }
    }

    private static string? Text(JsonElement parent, string property) => parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double Number(JsonElement parent, string property) => parent.TryGetProperty(property, out var value) && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : 0;

    private static async Task<string> PublishAsync(string candidate, string directory, string title, string extension, CancellationToken ct)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var invalid = Path.GetInvalidFileNameChars();
        var stem = new string(title.Where(character => !invalid.Contains(character) && !char.IsControl(character)).Take(80).ToArray()).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(stem)) stem = "video";
        var destination = Path.Combine(directory, $"{stem}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}{extension}");
        var partial = destination + ".partial";
        try
        {
            await using (var source = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
                await source.CopyToAsync(output, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(partial, destination, false);
            return destination;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
}
