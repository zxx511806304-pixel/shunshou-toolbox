using System.Collections.Concurrent;
using System.Globalization;
using System.Security;
using System.Security.Cryptography;

namespace Shunshou.Core;

public sealed record DuplicateGroup(long Size, string Hash, IReadOnlyList<string> Paths);

public sealed record DuplicateScanResult(IReadOnlyList<DuplicateGroup> Groups, long ReclaimableBytes, int FilesScanned);

/// <summary>
/// Finds duplicate files locally by grouping by size and comparing SHA-256. Reparse points, recycle bins
/// and inaccessible entries are skipped. Cancellation returns normally with what was scanned so far.
/// </summary>
public static class DuplicateFileService
{
    private static readonly string[] ExcludedDirectoryNames = ["$Recycle.Bin", "System Volume Information"];

    public static Task<DuplicateScanResult> ScanAsync(IReadOnlyList<string> roots, long minBytes,
        IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (roots.Count == 0) throw new ArgumentException("请选择要检查的文件夹。", nameof(roots));
        if (minBytes < 0) throw new ArgumentOutOfRangeException(nameof(minBytes));
        return Task.Run(async () => await ScanCoreAsync(roots, minBytes, progress, ct).ConfigureAwait(false), CancellationToken.None);
    }

    private static async Task<DuplicateScanResult> ScanCoreAsync(IReadOnlyList<string> roots, long minBytes,
        IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("正在枚举文件…");
        var bySize = new Dictionary<long, List<string>>();
        var scanned = 0;
        foreach (var root in roots)
        {
            if (ct.IsCancellationRequested) break;
            EnumerateFiles(root, minBytes, bySize, ref scanned, progress, ct);
        }

        var candidates = bySize.Where(pair => pair.Value.Count >= 2)
            .OrderByDescending(pair => pair.Key * pair.Value.Count).ToArray();
        if (ct.IsCancellationRequested || candidates.Length == 0)
            return BuildResult(null, scanned);

        progress?.Report($"正在比对 {candidates.Sum(group => group.Value.Count)} 个可能重复的文件…");
        var groups = new ConcurrentBag<DuplicateGroup>();
        var hashed = 0;
        var gate = new SemaphoreSlim(4);
        var tasks = candidates.Select(async candidate =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ct.IsCancellationRequested) return;
                var byHash = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var path in candidate.Value)
                {
                    if (ct.IsCancellationRequested) break;
                    var hash = await TryHashAsync(path, ct).ConfigureAwait(false);
                    if (hash is null) continue;
                    if (!byHash.TryGetValue(hash, out var list)) byHash[hash] = list = [];
                    list.Add(path);
                }
                foreach (var pair in byHash)
                {
                    if (pair.Value.Count >= 2)
                        groups.Add(new(candidate.Key, pair.Key, pair.Value.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()));
                }
            }
            finally
            {
                var done = Interlocked.Increment(ref hashed);
                if (done % 8 == 0 || done == candidates.Length)
                    progress?.Report($"正在比对文件内容 {done}/{candidates.Length}…");
                gate.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return BuildResult(groups, scanned);
    }

    private static DuplicateScanResult BuildResult(ConcurrentBag<DuplicateGroup>? groups, int scanned)
    {
        IReadOnlyList<DuplicateGroup> list = groups is null
            ? []
            : groups.OrderByDescending(group => group.Size * (group.Paths.Count - 1)).ToArray();
        long reclaimable = list.Sum(group => group.Size * (group.Paths.Count - 1));
        return new(list, reclaimable, scanned);
    }

    private static void EnumerateFiles(string root, long minBytes, Dictionary<long, List<string>> bySize,
        ref int scanned, IProgress<string>? progress, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var lastReport = 0;
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = false, RecurseSubdirectories = false,
            AttributesToSkip = 0, ReturnSpecialDirectories = false, BufferSize = 16384
        };
        while (pending.TryPop(out var folder) && !ct.IsCancellationRequested)
        {
            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(folder).GetFileSystemInfos("*", options); }
            catch (Exception ex) when (IsFileSystemReadFailure(ex)) { continue; }
            foreach (var entry in entries)
            {
                if (ct.IsCancellationRequested) return;
                FileAttributes attributes;
                try { attributes = entry.Attributes; }
                catch (Exception ex) when (IsFileSystemReadFailure(ex)) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!ExcludedDirectoryNames.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
                        pending.Push(entry.FullName);
                    continue;
                }
                long size;
                try { size = ((FileInfo)entry).Length; }
                catch (Exception ex) when (IsFileSystemReadFailure(ex)) { continue; }
                if (size < minBytes) continue;
                if (!bySize.TryGetValue(size, out var list)) bySize[size] = list = [];
                list.Add(entry.FullName);
                scanned++;
                if (scanned - lastReport >= 500)
                {
                    lastReport = scanned;
                    progress?.Report($"已枚举 {scanned} 个文件…");
                }
            }
        }
    }

    private static async Task<string?> TryHashAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 65536, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            var bytes = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            return Convert.ToHexString(bytes);
        }
        catch (Exception ex) when (IsFileSystemReadFailure(ex) || ex is NotSupportedException) { return null; }
        catch (OperationCanceledException) { return null; }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return string.Create(CultureInfo.InvariantCulture, $"{value:0.##} {units[unit]}");
    }

    public static void SendToRecycleBin(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    }

    private static bool IsFileSystemReadFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or SecurityException;
}
