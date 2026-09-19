using System.Diagnostics;
using System.Security;

namespace Shunshou.Core;

public sealed record FileSearchDrive(string RootPath, string DisplayName, bool IsRemovable);

/// <summary>Results is a detached batch, not the complete accumulated result list.</summary>
public sealed record FileSearchUpdate(IReadOnlyList<FileSearchResult> Results,
    long ScannedEntries, long ScannedDirectories, long SkippedEntries, int MatchedCount,
    string CurrentDirectory, bool IsCompleted)
{
    public bool IsIndexed { get; init; }
}

/// <summary>Cancellation and the result limit retain results. SearchedRoots == 0 means no root was readable.</summary>
public sealed record FileSearchSummary(IReadOnlyList<FileSearchResult> Results,
    long ScannedEntries, long ScannedDirectories, long SkippedEntries,
    bool IsTruncated, bool IsCancelled, TimeSpan Elapsed, int RequestedRoots, int SearchedRoots)
{
    public bool IsIndexed { get; init; }
}

public sealed partial class FileService
{
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff", ".heic", ".avif", ".svg" };

    public IReadOnlyList<FileSearchDrive> GetLocalDrives()
    {
        var result = new List<FileSearchDrive>();
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (Exception ex) when (IsFileSystemReadFailure(ex)) { return result; }
        // Check the drive kind before IsReady so mapped network drives are never contacted.
        foreach (var drive in drives)
        {
            try
            {
                var type = drive.DriveType;
                if (type is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady) continue;
                var label = "";
                try { label = drive.VolumeLabel; }
                catch (Exception ex) when (IsFileSystemReadFailure(ex)) { }
                var name = string.IsNullOrWhiteSpace(label) ? (type == DriveType.Removable ? "可移动磁盘" : "本地磁盘") : label;
                result.Add(new(drive.RootDirectory.FullName, $"{name} ({drive.Name.TrimEnd('\\')})", type == DriveType.Removable));
            }
            catch (Exception ex) when (IsFileSystemReadFailure(ex)) { }
        }
        return result.DistinctBy(x => x.RootPath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.RootPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Searches local directory roots on a worker thread. Overlapping roots are collapsed; reparse points
    /// are skipped. Ordinary read failures affect only their entry or subtree. File size -1 means unknown.
    /// Callbacks receive bounded batches as the scan runs. Cancellation returns retained results normally.
    /// </summary>
    public Task<FileSearchSummary> SearchRootsAsync(IEnumerable<string> roots, string query, bool imagesOnly,
        IProgress<FileSearchUpdate>? progress, CancellationToken ct, int maxResults = 10000, int batchSize = 100)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("请输入文件名关键词。", nameof(query));
        if (maxResults is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(maxResults));
        if (batchSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(batchSize));
        var normalized = NormalizeSearchRoots(roots);
        if (normalized.Length == 0) throw new ArgumentException("请选择搜索位置。", nameof(roots));
        query = query.Trim();
        // Do not pass ct to Task.Run: even an already-cancelled call must provide a terminal summary.
        return Task.Run(() => SearchRoots(normalized, query, imagesOnly, progress, ct, maxResults, batchSize));
    }

    private static FileSearchSummary SearchRoots(string[] roots, string query, bool imagesOnly,
        IProgress<FileSearchUpdate>? progress, CancellationToken ct, int maxResults, int batchSize)
    {
        var clock = Stopwatch.StartNew();
        var result = new List<FileSearchResult>(Math.Min(maxResults, 1000));
        var batch = new List<FileSearchResult>(batchSize);
        long scannedEntries = 0, scannedDirectories = 0, skippedEntries = 0;
        var searchedRoots = 0;
        var currentDirectory = "";
        var lastReport = TimeSpan.Zero;
        var truncated = false;
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = false, RecurseSubdirectories = false,
            AttributesToSkip = 0, ReturnSpecialDirectories = false, BufferSize = 16384
        };

        void Publish(bool completed = false)
        {
            // Copy the batch before clearing it: Progress<T> may dispatch it after this worker continues.
            progress?.Report(new(batch.ToArray(), scannedEntries, scannedDirectories, skippedEntries,
                result.Count, currentDirectory, completed));
            batch.Clear();
            lastReport = clock.Elapsed;
        }

        Publish();
        foreach (var root in roots)
        {
            if (ct.IsCancellationRequested || truncated) break;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out var folder) && !ct.IsCancellationRequested && !truncated)
            {
                currentDirectory = folder;
                if (!TryOpenSearchDirectory(folder, options, out var entries, out var hasNext))
                {
                    skippedEntries++;
                    if (clock.Elapsed - lastReport >= TimeSpan.FromMilliseconds(200)) Publish();
                    continue;
                }
                using (entries)
                {
                    scannedDirectories++;
                    if (StringComparer.OrdinalIgnoreCase.Equals(folder, root)) searchedRoots++;
                    while (hasNext && !ct.IsCancellationRequested && !truncated)
                    {
                        var entry = entries!.Current;
                        scannedEntries++;
                        if (!TryReadSearchAttributes(entry, out var attributes)) skippedEntries++;
                        else if ((attributes & FileAttributes.ReparsePoint) != 0) skippedEntries++;
                        else
                        {
                            var isDirectory = (attributes & FileAttributes.Directory) != 0;
                            if (isDirectory) pending.Push(entry.FullName);
                            if (entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                                (!imagesOnly || !isDirectory && Images.Contains(entry.Extension)))
                            {
                                // Probe the next match before declaring truncation; exactly N can be complete.
                                if (result.Count == maxResults) truncated = true;
                                else
                                {
                                    long size = 0;
                                    if (!isDirectory && !TryReadSearchSize((FileInfo)entry, out size)) skippedEntries++;
                                    var found = new FileSearchResult(entry.Name, entry.FullName, isDirectory, size);
                                    result.Add(found);
                                    batch.Add(found);
                                }
                            }
                        }
                        if (batch.Count >= batchSize || clock.Elapsed - lastReport >= TimeSpan.FromMilliseconds(200)) Publish();
                        if (!ct.IsCancellationRequested && !truncated)
                            hasNext = TryMoveNextSearchEntry(entries, ref skippedEntries);
                    }
                }
                if (clock.Elapsed - lastReport >= TimeSpan.FromMilliseconds(200)) Publish();
            }
        }
        clock.Stop();
        Publish(completed: true);
        return new(result.ToArray(), scannedEntries, scannedDirectories, skippedEntries,
            truncated, ct.IsCancellationRequested, clock.Elapsed, roots.Length, searchedRoots);
    }

    private static string[] NormalizeSearchRoots(IEnumerable<string> roots)
    {
        var normalized = roots.Select(path =>
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("搜索位置不能为空。", nameof(roots));
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Length).ThenBy(x => x, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var path in normalized)
        {
            if (!result.Any(parent => path.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))) result.Add(path);
        }
        return result.ToArray();
    }

    private static bool TryOpenSearchDirectory(string folder, EnumerationOptions options,
        out IEnumerator<FileSystemInfo>? entries, out bool hasNext)
    {
        entries = null;
        hasNext = false;
        try
        {
            var directory = new DirectoryInfo(folder);
            var attributes = directory.Attributes;
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0) return false;
            entries = directory.EnumerateFileSystemInfos("*", options).GetEnumerator();
            hasNext = entries.MoveNext();
            return true;
        }
        catch (Exception ex) when (IsFileSystemReadFailure(ex))
        {
            entries?.Dispose();
            entries = null;
            return false;
        }
    }

    private static bool TryReadSearchAttributes(FileSystemInfo entry, out FileAttributes attributes)
    {
        try { attributes = entry.Attributes; return true; }
        catch (Exception ex) when (IsFileSystemReadFailure(ex)) { attributes = 0; return false; }
    }

    private static bool TryReadSearchSize(FileInfo entry, out long size)
    {
        try { size = entry.Length; return true; }
        catch (Exception ex) when (IsFileSystemReadFailure(ex)) { size = -1; return false; }
    }

    private static bool TryMoveNextSearchEntry(IEnumerator<FileSystemInfo> entries, ref long skipped)
    {
        try { return entries.MoveNext(); }
        catch (Exception ex) when (IsFileSystemReadFailure(ex)) { skipped++; return false; }
    }

    private static bool IsFileSystemReadFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or SecurityException;
}
