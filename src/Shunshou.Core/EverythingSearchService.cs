using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Shunshou.Core;

public enum EverythingIndexState { Missing, NeedsEnable, Loading, Ready, Unavailable }
public sealed record EverythingIndexStatus(EverythingIndexState State, string Message, bool UsesExistingIndex = false);
public sealed record EverythingCommandResult(int ExitCode, string Output, string Error = "");

/// <summary>The official ES client reads an Everything index over local IPC. It never scans directories.</summary>
public sealed class EverythingSearchService
{
    private readonly string _runtimeDirectory;
    private readonly string _dataDirectory;
    private readonly string _ownInstance;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<EverythingCommandResult>> _query;
    private string? _instance;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff", ".heic", ".avif", ".svg" };

    public EverythingSearchService(string? runtimeDirectory = null, string? dataDirectory = null,
        Func<IReadOnlyList<string>, CancellationToken, Task<EverythingCommandResult>>? query = null)
    {
        _runtimeDirectory = Path.GetFullPath(runtimeDirectory ?? Path.Combine(AppContext.BaseDirectory, "tools", "everything"));
        _dataDirectory = Path.GetFullPath(dataDirectory ?? Path.Combine(AppPaths.DataDirectory, "search-index"));
        _ownInstance = "ShunshouToolbox-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_dataDirectory.ToUpperInvariant())))[..12];
        _query = query ?? RunQueryAsync;
    }

    public async Task<EverythingIndexStatus> GetStatusAsync(CancellationToken ct = default)
    {
        if (!File.Exists(Path.Combine(_runtimeDirectory, "es.exe")))
            return new(EverythingIndexState.Missing, "搜索组件缺失，请重新解压完整软件包。");
        // Never change another application's settings or start an elevated process during a status check.
        foreach (var instance in new[] { _instance, "", "1.5a", "1.5", _ownInstance }.OfType<string>().Distinct())
        {
            var reply = await _query(["-instance", instance, "-get-everything-version"], ct).ConfigureAwait(false);
            if (reply.ExitCode != 0) continue;
            _instance = instance;
            var loaded = await _query(["-instance", instance, "-timeout", "100", "-get-result-count"], ct).ConfigureAwait(false);
            if (loaded.ExitCode != 0)
                return new(EverythingIndexState.Loading, "正在建立本机索引，首次需要一些时间…", instance != _ownInstance);
            return new(EverythingIndexState.Ready, instance == _ownInstance ? "本机索引已连接" : "已连接 Everything 索引", instance != _ownInstance);
        }
        _instance = null;
        return File.Exists(Path.Combine(_runtimeDirectory, "Everything.exe"))
            ? new(EverythingIndexState.NeedsEnable, "启用快速搜索后，按名称查询本机索引。")
            : new(EverythingIndexState.Missing, "搜索组件缺失，请重新解压完整软件包。");
    }

    /// <summary>Call only after an explicit user action. Portable indexing requests UAC, installs no service/startup entry.</summary>
    public async Task<EverythingIndexStatus> EnableAsync(IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        var status = await GetStatusAsync(ct).ConfigureAwait(false);
        if (status.State is EverythingIndexState.Ready or EverythingIndexState.Missing) return status;
        if (status.State == EverythingIndexState.Loading)
        {
            progress?.Report(new(0, status.Message));
            var existing = await _query(["-instance", _instance!, "-timeout", "60000", "-get-result-count"], ct).ConfigureAwait(false);
            return existing.ExitCode == 0 ? new(EverythingIndexState.Ready, "本机索引已就绪", status.UsesExistingIndex) : status;
        }
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_dataDirectory);
        var config = Path.Combine(_dataDirectory, "Everything.ini");
        if (!File.Exists(config))
            await File.WriteAllTextAsync(config,
                "[Everything]\r\napp_data=0\r\nrun_as_admin=0\r\nrun_in_background=1\r\nshow_tray_icon=1\r\n" +
                "check_for_updates_on_startup=0\r\nhttp_server_enabled=0\r\netp_server_enabled=0\r\n" +
                "search_history_enabled=0\r\nrun_history_enabled=0\r\n", new UTF8Encoding(false), ct).ConfigureAwait(false);
        var start = new ProcessStartInfo(Path.Combine(_runtimeDirectory, "Everything.exe"))
        { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = _dataDirectory };
        foreach (var argument in new[] { "-instance", _ownInstance, "-config", config, "-db", Path.Combine(_dataDirectory, "Everything.db"), "-startup" })
            start.ArgumentList.Add(argument);
        try
        {
            // ShellExecute's Windows consent window is deliberately not bypassed or automated.
            using var process = Process.Start(start) ?? throw new IOException("无法启动本机索引。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { return new(EverythingIndexState.NeedsEnable, "未授权启用快速搜索，可继续使用普通搜索。"); }
        _instance = _ownInstance;
        progress?.Report(new(0, "正在建立本机索引，首次需要一些时间…"));
        // The query timeout waits for the database to load; an empty unloaded database is never reported as a completed search.
        var ready = await _query(["-instance", _ownInstance, "-timeout", "60000", "-get-result-count"], ct).ConfigureAwait(false);
        return ready.ExitCode == 0
            ? new(EverythingIndexState.Ready, "本机索引已就绪")
            : new(EverythingIndexState.Loading, "索引仍在建立，请稍后再次搜索。");
    }

    public async Task<FileSearchSummary> SearchAsync(IEnumerable<string> roots, string query, bool imagesOnly,
        IProgress<FileSearchUpdate>? progress, CancellationToken ct, int maxResults = 10000, int batchSize = 100)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("请输入文件名关键词。", nameof(query));
        if (maxResults is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(maxResults));
        if (batchSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(batchSize));
        var normalized = roots.Select(x => string.IsNullOrWhiteSpace(x) ? throw new ArgumentException("搜索位置不能为空。")
                : Path.TrimEndingDirectorySeparator(Path.GetFullPath(x))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (normalized.Length == 0) throw new ArgumentException("请选择搜索位置。", nameof(roots));
        query = query.Trim();
        var clock = Stopwatch.StartNew();
        var results = new List<FileSearchResult>();
        var batch = new List<FileSearchResult>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var truncated = false;
        var cancelled = false;
        void Publish(bool completed) { progress?.Report(new(batch.ToArray(), 0, 0, 0, results.Count, "", completed) { IsIndexed = true }); batch.Clear(); }
        try
        {
            ct.ThrowIfCancellationRequested();
            if (_instance is null)
            {
                var status = await GetStatusAsync(ct).ConfigureAwait(false);
                if (status.State is not (EverythingIndexState.Ready or EverythingIndexState.Loading)) throw new InvalidOperationException(status.Message);
            }
            Publish(false);
            // EFU asks only for metadata already indexed; explicitly requesting attributes would
            // make Everything read each result from disk when attribute indexing is disabled.
            var arguments = new List<string> { "-instance", _instance!, "-timeout", "30000", "-efu", "-full-path",
                "-header", "-no-footer", "-no-digit-grouping", "-size-format", "1", "-no-folder-append-path-separator",
                "-no-case", "-no-whole-words", "-no-match-path", "-offset", "0", "-n", (maxResults + 1).ToString(CultureInfo.InvariantCulture),
                "-search", BuildSearch(normalized, query, imagesOnly) };
            var response = await _query(arguments, ct).ConfigureAwait(false);
            if (response.ExitCode != 0)
            {
                _instance = null;
                throw new IOException(response.ExitCode == 8
                    ? "索引暂未就绪，请稍后重试或重新启用快速搜索。"
                    : $"索引查询失败（{response.ExitCode}），请重试。");
            }
            int nameColumn = -1, attributeColumn = -1, sizeColumn = -1, columnCount = 0;
            foreach (var cells in ReadCsv(response.Output))
            {
                ct.ThrowIfCancellationRequested();
                if (cells.Length == 1 && cells[0].Length == 0) continue;
                if (columnCount == 0)
                {
                    nameColumn = Array.FindIndex(cells, x => x.Equals("Filename", StringComparison.OrdinalIgnoreCase));
                    attributeColumn = Array.FindIndex(cells, x => x.Equals("Attributes", StringComparison.OrdinalIgnoreCase));
                    sizeColumn = Array.FindIndex(cells, x => x.Equals("Size", StringComparison.OrdinalIgnoreCase));
                    if (nameColumn < 0 || attributeColumn < 0) throw new InvalidDataException("搜索组件返回了无法识别的列。");
                    columnCount = cells.Length; continue;
                }
                if (cells.Length != columnCount || !uint.TryParse(cells[attributeColumn], NumberStyles.Integer, CultureInfo.InvariantCulture, out var attributes))
                    throw new InvalidDataException("搜索组件返回了无法识别的数据。");
                var path = Path.GetFullPath(cells[nameColumn]);
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                var folder = (attributes & (uint)FileAttributes.Directory) != 0;
                // Validate IPC output without touching the disk; do not let parser syntax widen the selected scope.
                if (!normalized.Any(root => path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) ||
                    !name.Contains(query, StringComparison.OrdinalIgnoreCase) || (attributes & (uint)FileAttributes.ReparsePoint) != 0 ||
                    imagesOnly && (folder || !ImageExtensions.Contains(Path.GetExtension(name))) || !paths.Add(path)) continue;
                if (results.Count == maxResults) { truncated = true; break; }
                var size = sizeColumn >= 0 && long.TryParse(cells[sizeColumn], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) ? bytes : -1;
                var item = new FileSearchResult(name, path, folder, folder ? 0 : size);
                results.Add(item); batch.Add(item);
                if (batch.Count >= batchSize) Publish(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { cancelled = true; }
        Publish(true);
        return new(results.ToArray(), 0, 0, 0, truncated, cancelled, clock.Elapsed, normalized.Length, cancelled && results.Count == 0 ? 0 : normalized.Length)
            { IsIndexed = true };
    }

    private static string BuildSearch(string[] roots, string query, bool imagesOnly)
    {
        // Hex-encode punctuation and whitespace so the ES command parser and Everything search parser
        // cannot reinterpret quotes, wildcard characters, operators or spaces in literal names.
        static string Literal(string value) => string.Concat(value.EnumerateRunes().Select(c => Rune.IsLetterOrDigit(c) ? c.ToString() : "\\x{" + c.Value.ToString("X", CultureInfo.InvariantCulture) + "}"));
        // Ordinary names use Everything's fast substring matcher; reserve regular expressions for
        // names containing syntax characters. Avoid a whole-index regex for each normal keystroke.
        var name = query.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-') ? "nopath:" + query : "nopath:regex:" + Literal(query);
        var scopes = string.Join(" | ", roots.Select(root =>
        {
            var path = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            return path.All(c => char.IsLetterOrDigit(c) || c is ':' or '\\' or '.' or '_' or '-') ? path : "path:regex:\\A" + Literal(path);
        }));
        return $"{name} <{scopes}>" +
            (imagesOnly ? " file: ext:jpg;jpeg;png;webp;bmp;gif;tif;tiff;heic;avif;svg" : "");
    }

    private async Task<EverythingCommandResult> RunQueryAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Path.Combine(_runtimeDirectory, "es.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        // Avoid the user's ES default filters, output columns or saved search altering toolbox queries.
        start.Environment["APPDATA"] = Path.Combine(_dataDirectory, "cli-profile");
        start.ArgumentList.Add("-argv");
        start.ArgumentList.Add("-cp");
        start.ArgumentList.Add("65001");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(arguments.Contains("-get-everything-version") ? 3 : arguments.Contains("60000") ? 65 : 35));
        using var process = Process.Start(start) ?? throw new IOException("无法启动搜索组件。");
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return new(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(output, error).ConfigureAwait(false); } catch (OperationCanceledException) { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException("索引响应超时，请稍后重试。");
        }
    }

    private static IEnumerable<string[]> ReadCsv(string text)
    {
        var fields = new List<string>(); var value = new StringBuilder(); var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') { value.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted) { fields.Add(value.ToString()); value.Clear(); }
            else if ((c == '\r' || c == '\n') && !quoted)
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                fields.Add(value.ToString()); value.Clear(); yield return fields.ToArray(); fields.Clear();
            }
            else value.Append(c);
        }
        if (quoted) throw new InvalidDataException("搜索组件返回了不完整的数据。");
        if (value.Length > 0 || fields.Count > 0) { fields.Add(value.ToString()); yield return fields.ToArray(); }
    }
}
