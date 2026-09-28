using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Shunshou.Core;

public enum SubtitleOutputFormat { Srt, Vtt, Text }

public sealed record SubtitleTrack(string Key, string Language, string Name, bool IsAutomatic)
{
    public string Label => $"{Name} · {(IsAutomatic ? "自动字幕" : "网站字幕")}";
    public override string ToString() => Label;
}

public sealed class SubtitleDownloadInfo
{
    public string Url { get; }
    public string Title { get; }
    public IReadOnlyList<SubtitleTrack> Tracks { get; }
    internal IReadOnlyDictionary<string, JsonArray> Sources { get; }
    internal JsonObject Headers { get; }
    internal DateTimeOffset RetrievedAt { get; } = DateTimeOffset.UtcNow;
    internal SubtitleDownloadInfo(string url, string title, IReadOnlyList<SubtitleTrack> tracks,
        IReadOnlyDictionary<string, JsonArray> sources, JsonObject headers)
        => (Url, Title, Tracks, Sources, Headers) = (url, title, tracks, sources, headers);
}

/// <summary>Reads website-provided subtitle tracks without uploading files, downloading video, or using accounts.</summary>
public sealed partial class SubtitleDownloadService(string? engineDirectory = null, string? ffmpegDirectory = null)
{
    private readonly VideoDownloadService _runtime = new(engineDirectory, ffmpegDirectory);
    private static readonly string TemporaryRoot = Path.Combine(Path.GetTempPath(), "Shunshou", "subtitle-jobs");
    private const long MaximumSubtitleBytes = 32_000_000;

    public async Task<SubtitleDownloadInfo> AnalyzeAsync(string url, CancellationToken ct)
    {
        url = VideoDownloadService.ValidateUrl(url);
        var engines = _runtime.ResolveEngines();
        var arguments = VideoDownloadService.CommonArguments(engines);
        arguments.AddRange(["--dump-single-json", "--skip-download", "--playlist-items", "1", "--", url]);
        var metadata = await VideoDownloadService.RunAsync(engines.Python, arguments, engines.Directory, ct);
        return ParseMetadata(url, metadata);
    }

    internal static SubtitleDownloadInfo ParseMetadata(string url, string metadata)
    {
        url = VideoDownloadService.ValidateUrl(url);
        using var document = JsonDocument.Parse(metadata);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("网页未返回有效的视频信息。");
        if (Text(root, "_type") is "playlist" or "multi_video" || root.TryGetProperty("entries", out _))
            throw new NotSupportedException("请使用单个视频链接，暂不处理播放列表。");
        if (Text(root, "live_status") is "is_live" or "is_upcoming" ||
            root.TryGetProperty("is_live", out var live) && live.ValueKind == JsonValueKind.True)
            throw new NotSupportedException("请使用已经发布的完整视频，暂不处理直播字幕。");

        var tracks = new List<SubtitleTrack>();
        var sources = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        foreach (var (property, automatic) in new[] { ("subtitles", false), ("automatic_captions", true) })
        {
            if (!root.TryGetProperty(property, out var languages) || languages.ValueKind != JsonValueKind.Object) continue;
            foreach (var language in languages.EnumerateObject())
            {
                // Language identifiers never become command syntax or paths. Exclude live chat and malformed IDs.
                if (language.Name.Length is 0 or > 100 || language.Name.Any(char.IsControl) ||
                    language.Name.Equals("live_chat", StringComparison.OrdinalIgnoreCase) ||
                    language.Value.ValueKind != JsonValueKind.Array) continue;
                var formats = new JsonArray();
                string? name = null;
                foreach (var format in language.Value.EnumerateArray())
                {
                    if (format.ValueKind != JsonValueKind.Object) continue;
                    var extension = Text(format, "ext")?.ToLowerInvariant();
                    if (extension is not ("srt" or "vtt" or "ass" or "ssa" or "ttml" or "dfxp" or "srv1" or "srv2" or "srv3" or "json3")) continue;
                    var source = new JsonObject { ["ext"] = extension };
                    var inline = Text(format, "data");
                    if (inline is { Length: > 0 and <= 8_000_000 }) source["data"] = inline;
                    else
                    {
                        var address = Text(format, "url");
                        try { source["url"] = VideoDownloadService.ValidateUrl(address ?? ""); }
                        catch (ArgumentException) { continue; }
                    }
                    if (format.TryGetProperty("http_headers", out var formatHeaders)) source["http_headers"] = SafeHeaders(formatHeaders);
                    formats.Add(source);
                    name ??= Text(format, "name");
                }
                if (formats.Count == 0) continue;
                var key = (automatic ? "auto:" : "manual:") + language.Name;
                var display = string.IsNullOrWhiteSpace(name) ? LanguageName(language.Name) : CleanDisplay(name);
                tracks.Add(new(key, language.Name, display, automatic));
                sources.Add(key, formats);
            }
        }
        tracks = tracks.OrderBy(track => track.IsAutomatic)
            .ThenBy(track => track.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(track => track.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new(url, CleanDisplay(Text(root, "title") ?? "视频字幕"), tracks.AsReadOnly(), sources,
            root.TryGetProperty("http_headers", out var headers) ? SafeHeaders(headers) : new());
    }

    public async Task<string> DownloadAsync(SubtitleDownloadInfo info, string trackKey, SubtitleOutputFormat format,
        string outputDirectory, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        var track = info.Tracks.SingleOrDefault(item => item.Key == trackKey);
        if (track is null || !info.Sources.TryGetValue(trackKey, out var sources))
            throw new ArgumentException("请选择当前视频提供的字幕。");
        if (DateTimeOffset.UtcNow - info.RetrievedAt > TimeSpan.FromMinutes(30))
            throw new InvalidOperationException("字幕链接可能已过期，请重新解析视频链接。");
        ct.ThrowIfCancellationRequested();
        var engines = _runtime.ResolveEngines();
        var job = Path.Combine(TemporaryRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        try
        {
            // Rebuild a minimal info JSON, never pass untrusted metadata filenames, command options, or all tracks through.
            var metadata = new JsonObject
            {
                ["id"] = "subtitle", ["title"] = "subtitle", ["extractor"] = "generic", ["extractor_key"] = "Generic",
                ["webpage_url"] = info.Url, ["url"] = info.Url, ["ext"] = "mp4", ["http_headers"] = info.Headers.DeepClone(),
                [track.IsAutomatic ? "automatic_captions" : "subtitles"] = new JsonObject { ["selected"] = sources.DeepClone() }
            };
            var metadataPath = Path.Combine(job, "selected.info.json");
            await File.WriteAllTextAsync(metadataPath, metadata.ToJsonString(), new UTF8Encoding(false), ct);
            var arguments = VideoDownloadService.CommonArguments(engines);
            arguments.AddRange(["--load-info-json", metadataPath, "--skip-download", "--no-simulate", "--no-overwrites", "--no-continue", "--no-part",
                track.IsAutomatic ? "--no-write-subs" : "--write-subs", track.IsAutomatic ? "--write-auto-subs" : "--no-write-auto-subs",
                "--sub-langs", "selected", "--sub-format", "srt/vtt/ass/ssa/ttml/dfxp/srv3/srv2/srv1/json3",
                "--convert-subs", "srt", "--max-filesize", MaximumSubtitleBytes.ToString(CultureInfo.InvariantCulture),
                "--newline", "--progress", "--progress-delta", "0.1", "--progress-template", "download:SS_SUBTITLE:%(progress.downloaded_bytes)s",
                "--paths", job, "--output", "caption.%(ext)s"]);
            progress?.Report(new(5, "正在下载字幕 · " + track.Label));
            using var boundedJob = CancellationTokenSource.CreateLinkedTokenSource(ct);
            boundedJob.CancelAfter(TimeSpan.FromMinutes(3));
            int sizeExceeded = 0;
            try
            {
                await VideoDownloadService.RunAsync(engines.Python, arguments, job, boundedJob.Token, line =>
                {
                    if (!line.StartsWith("SS_SUBTITLE:", StringComparison.Ordinal) ||
                        !long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) || bytes <= MaximumSubtitleBytes) return;
                    Interlocked.Exchange(ref sizeExceeded, 1);
                    boundedJob.Cancel();
                });
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InvalidDataException(sizeExceeded != 0 ? "字幕文件超过 32 MB，已停止下载。请选择其他字幕。" : "字幕处理超时，已停止。请稍后重试。");
            }
            var candidate = Path.Combine(job, "caption.selected.srt");
            if (!File.Exists(candidate) || File.GetAttributes(candidate).HasFlag(FileAttributes.ReparsePoint) || new FileInfo(candidate).Length is 0 or > MaximumSubtitleBytes)
                throw new InvalidDataException("没有获得可用字幕。链接可能已过期或字幕超过 32 MB，请重新解析或选择其他字幕。");
            var cues = ParseSrt(await File.ReadAllTextAsync(candidate, ct));
            if (cues.Count == 0) throw new InvalidDataException("这条字幕没有可导出的文字，请选择其他字幕。");
            progress?.Report(new(90, "正在保存字幕"));
            var content = Render(cues, format);
            var output = await PublishAsync(content, info.Title, track.Language, format, outputDirectory, ct);
            progress?.Report(new(100, "已保存 · " + Path.GetFileName(output)));
            return output;
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(job)) == Path.GetFullPath(TemporaryRoot) && Guid.TryParseExact(Path.GetFileName(job), "N", out _))
                try { Directory.Delete(job, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    internal sealed record Cue(string Start, string End, string Text);
    internal static IReadOnlyList<Cue> ParseSrt(string text)
    {
        var cues = new List<Cue>();
        foreach (var block in Regex.Split(text.Replace("\r", "").Trim('\uFEFF', '\n', ' '), @"\n[\t ]*\n", RegexOptions.None, TimeSpan.FromSeconds(2)))
        {
            var lines = block.Split('\n');
            var index = Array.FindIndex(lines, line => line.Contains("-->", StringComparison.Ordinal));
            if (index < 0 || index + 1 >= lines.Length) continue;
            var match = Timing().Match(lines[index]);
            if (!match.Success) continue;
            var start = match.Groups[1].Value.Replace('.', ','); var end = match.Groups[2].Value.Replace('.', ',');
            var startTime = ParseTime(start); var endTime = ParseTime(end);
            if (startTime is null || endTime is null || endTime <= startTime) continue;
            var content = string.Join('\n', lines.Skip(index + 1)).Trim();
            if (content.Length > 0) cues.Add(new(start, end, content));
        }
        return cues;
    }

    internal static string Render(IReadOnlyList<Cue> cues, SubtitleOutputFormat format)
    {
        // Strip pseudo-protocol content (javascript:/data:/vbscript:) and control characters before writing any output.
        var clean = cues.Select(cue => cue with { Text = Sanitize(cue.Text) }).Where(cue => cue.Text.Length > 0).ToList();
        if (format == SubtitleOutputFormat.Text)
        {
            var lines = new List<string>();
            foreach (var cue in clean)
            {
                var plain = WebUtility.HtmlDecode(Tags().Replace(cue.Text, "")).Trim();
                if (plain.Length > 0 && (lines.Count == 0 || lines[^1] != plain)) lines.Add(plain);
            }
            return string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }
        var output = new StringBuilder(format == SubtitleOutputFormat.Vtt ? "WEBVTT\n\n" : "");
        for (var index = 0; index < clean.Count; index++)
        {
            var cue = clean[index];
            output.Append(index + 1).Append('\n');
            output.Append(format == SubtitleOutputFormat.Vtt ? cue.Start.Replace(',', '.') : cue.Start).Append(" --> ")
                .Append(format == SubtitleOutputFormat.Vtt ? cue.End.Replace(',', '.') : cue.End).Append('\n').Append(cue.Text).Append("\n\n");
        }
        return output.ToString();
    }

    private static TimeSpan? ParseTime(string value)
    {
        var parts = value.Split(':', ',');
        if (parts.Length != 4 || !parts.All(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))) return null;
        var values = parts.Select(int.Parse).ToArray();
        return values[1] < 60 && values[2] < 60 && values[3] < 1000 ? TimeSpan.FromHours(values[0]) + TimeSpan.FromMinutes(values[1]) + TimeSpan.FromSeconds(values[2]) + TimeSpan.FromMilliseconds(values[3]) : null;
    }

    private static async Task<string> PublishAsync(string content, string title, string language, SubtitleOutputFormat format, string directory, CancellationToken ct)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        string Safe(string value) => new(value.Where(character => !Path.GetInvalidFileNameChars().Contains(character) && !char.IsControl(character)).Take(70).ToArray());
        var extension = format switch { SubtitleOutputFormat.Srt => ".srt", SubtitleOutputFormat.Vtt => ".vtt", _ => ".txt" };
        var output = Path.Combine(directory, $"subtitle_{Safe(title).Trim().TrimEnd('.')}_{Safe(language)}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}{extension}");
        var partial = output + ".partial";
        try
        {
            await using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                await writer.WriteAsync(content.AsMemory(), ct);
            ct.ThrowIfCancellationRequested(); File.Move(partial, output, false); return output;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    private static JsonObject SafeHeaders(JsonElement headers)
    {
        var result = new JsonObject();
        if (headers.ValueKind != JsonValueKind.Object) return result;
        foreach (var header in headers.EnumerateObject())
            if (header.Name.ToLowerInvariant() is "user-agent" or "referer" or "origin" or "accept" or "accept-language" &&
                header.Value.ValueKind == JsonValueKind.String && header.Value.GetString() is { Length: <= 8192 } value && !value.Any(char.IsControl))
                result[header.Name] = value;
        return result;
    }
    private static string Sanitize(string text)
    {
        var withoutControls = new string(text.Where(character => !char.IsControl(character) || character == '\n').ToArray());
        var withoutBrackets = PseudoProtocolBracket().Replace(withoutControls, "");
        var lines = withoutBrackets.Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => !PseudoProtocolLine().IsMatch(line));
        return string.Join('\n', lines).Trim();
    }
    private static string? Text(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string CleanDisplay(string value) => new(value.Where(character => !char.IsControl(character)).Take(250).ToArray());
    private static string LanguageName(string language)
    {
        try { return CultureInfo.GetCultureInfo(language).NativeName + " (" + language + ")"; }
        catch (CultureNotFoundException) { return CleanDisplay(language); }
    }
    [GeneratedRegex(@"^(\d{2,4}:\d{2}:\d{2}[,.]\d{3})\s+-->\s+(\d{2,4}:\d{2}:\d{2}[,.]\d{3})(?:\s.*)?$", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex Timing();
    [GeneratedRegex(@"<[^>]*>|\{\\[^}]*\}", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex Tags();
    [GeneratedRegex(@"^\s*(?:javascript|data|vbscript)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex PseudoProtocolLine();
    [GeneratedRegex(@"\((?:javascript|data|vbscript)\s*:[^)]*\)|\[(?:javascript|data|vbscript)\s*:[^\]]*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex PseudoProtocolBracket();
}
