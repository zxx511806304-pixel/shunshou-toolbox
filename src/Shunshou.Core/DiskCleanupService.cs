using System.Security;
using System.Security.Principal;

namespace Shunshou.Core;

public sealed record CleanupItem(string Path, long Bytes, int FileCount, bool RequiresAdmin);

public sealed record CleanupCategory(string Name, string Description, IReadOnlyList<CleanupItem> Items)
{
    public long TotalBytes => Items.Sum(x => x.Bytes);
}

public sealed record CleanResult(int DeletedFiles, long FreedBytes, int FailedCount);

public sealed record LargeFileEntry(string Path, long Bytes, DateTime Modified);

/// <summary>Scans well-known local cache locations and large files. Fully offline; read failures skip only their entry.</summary>
public static class DiskCleanupService
{
    public static Task<IReadOnlyList<CleanupCategory>> ScanAsync(IProgress<string>? progress, CancellationToken ct)
        => Task.Run(() => Scan(progress, ct), CancellationToken.None);

    private static IReadOnlyList<CleanupCategory> Scan(IProgress<string>? progress, CancellationToken ct)
    {
        var categories = new List<CleanupCategory>();

        progress?.Report("正在统计用户临时文件…");
        categories.Add(new("用户临时文件", "当前账户的临时目录 (%TEMP%)", ScanTargets([Path.GetTempPath()], false, ct)));

        progress?.Report("正在统计系统临时文件…");
        categories.Add(new("系统临时文件", @"C:\Windows\Temp，清理通常需要管理员权限", ScanTargets([@"C:\Windows\Temp"], true, ct)));

        progress?.Report("正在统计 Windows 更新缓存…");
        categories.Add(new("Windows 更新下载缓存", @"C:\Windows\SoftwareDistribution\Download，清理通常需要管理员权限",
            ScanTargets([Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "Download")], true, ct)));

        progress?.Report("正在统计回收站…");
        categories.Add(new("回收站", "各磁盘回收站中属于当前账户的内容", ScanRecycleBin(ct)));

        progress?.Report("正在统计微信缓存…");
        categories.Add(new("微信缓存", "微信 FileStorage 下的 Cache 与 Temp 目录，不涉及聊天文件", ScanWeChat(ct)));

        progress?.Report("正在统计浏览器缓存…");
        categories.Add(new("浏览器缓存", "Edge 与 Chrome 配置目录下的 Cache", ScanBrowserCaches(ct)));

        // Hide empty categories entirely so the list only shows actionable items.
        return categories.Where(c => c.Items.Count > 0).ToArray();
    }

    private static IReadOnlyList<CleanupItem> ScanTargets(IEnumerable<string> roots, bool requiresAdmin, CancellationToken ct)
    {
        var items = new List<CleanupItem>();
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            var (bytes, files) = MeasureDirectory(root, ct);
            if (files > 0 || bytes > 0) items.Add(new(root, bytes, files, requiresAdmin));
        }
        return items;
    }

    private static IReadOnlyList<CleanupItem> ScanRecycleBin(CancellationToken ct)
    {
        var items = new List<CleanupItem>();
        string? sid = null;
        try { sid = WindowsIdentity.GetCurrent().User?.Value; } catch (Exception ex) when (IsFileSystemReadFailure(ex)) { }
        if (string.IsNullOrEmpty(sid)) return items;
        foreach (var drive in DriveInfo.GetDrives())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                var bin = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", sid);
                if (!Directory.Exists(bin)) continue;
                var (bytes, files) = MeasureDirectory(bin, ct);
                if (files > 0 || bytes > 0) items.Add(new(bin, bytes, files, false));
            }
            catch (Exception ex) when (IsFileSystemReadFailure(ex)) { }
        }
        return items;
    }

    private static IReadOnlyList<CleanupItem> ScanWeChat(CancellationToken ct)
    {
        var items = new List<CleanupItem>();
        var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WeChat Files");
        if (!Directory.Exists(baseDir)) return items;
        IEnumerable<string> accounts;
        try { accounts = Directory.EnumerateDirectories(baseDir); }
        catch (Exception ex) when (IsFileSystemReadFailure(ex)) { return items; }
        foreach (var account in accounts)
        {
            ct.ThrowIfCancellationRequested();
            var storage = Path.Combine(account, "FileStorage");
            if (!Directory.Exists(storage)) continue;
            IEnumerable<string> subdirs;
            try { subdirs = Directory.EnumerateDirectories(storage); }
            catch (Exception ex) when (IsFileSystemReadFailure(ex)) { continue; }
            foreach (var sub in subdirs)
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(sub);
                if (!string.Equals(name, "Cache", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(name, "Temp", StringComparison.OrdinalIgnoreCase)) continue;
                var (bytes, files) = MeasureDirectory(sub, ct);
                if (files > 0 || bytes > 0) items.Add(new(sub, bytes, files, false));
            }
        }
        return items;
    }

    private static IReadOnlyList<CleanupItem> ScanBrowserCaches(CancellationToken ct)
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "User Data"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data")
        };
        var items = new List<CleanupItem>();
        foreach (var userData in roots)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(userData)) continue;
            IEnumerable<string> profiles;
            try { profiles = Directory.EnumerateDirectories(userData); }
            catch (Exception ex) when (IsFileSystemReadFailure(ex)) { continue; }
            foreach (var profile in profiles)
            {
                ct.ThrowIfCancellationRequested();
                var cache = Path.Combine(profile, "Cache");
                if (!Directory.Exists(cache)) continue;
                var (bytes, files) = MeasureDirectory(cache, ct);
                if (files > 0 || bytes > 0) items.Add(new(cache, bytes, files, false));
            }
        }
        return items;
    }

    private static (long Bytes, int FileCount) MeasureDirectory(string root, CancellationToken ct)
    {
        long bytes = 0;
        var files = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint, BufferSize = 16384 };
        while (pending.TryPop(out var folder))
        {
            ct.ThrowIfCancellationRequested();
            IEnumerable<string> children;
            try { children = Directory.EnumerateFileSystemEntries(ToLongPath(folder), "*", options); }
            catch (Exception ex) when (IsFileSystemReadFailure(ex)) { continue; }
            foreach (var child in children)
            {
                ct.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try { attributes = File.GetAttributes(child); }
                catch (Exception ex) when (IsFileSystemReadFailure(ex)) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(child);
                else
                {
                    try { bytes += new FileInfo(child).Length; files++; }
                    catch (Exception ex) when (IsFileSystemReadFailure(ex)) { }
                }
            }
        }
        return (bytes, files);
    }

    public static Task<CleanResult> CleanAsync(IEnumerable<CleanupItem> items, IProgress<string>? progress, CancellationToken ct)
        => Task.Run(() => Clean(items.ToArray(), progress, ct), CancellationToken.None);

    private static CleanResult Clean(IReadOnlyList<CleanupItem> items, IProgress<string>? progress, CancellationToken ct)
    {
        var deleted = 0;
        long freed = 0;
        var failed = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report("正在清理 " + item.Path + " …");
            // Delete the directory contents only; the directory itself stays in place.
            IEnumerable<string> children;
            try { children = Directory.EnumerateFileSystemEntries(ToLongPath(item.Path)); }
            catch (Exception ex) when (IsFileSystemReadFailure(ex)) { failed++; continue; }
            foreach (var child in children)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    long size = 0;
                    var isDirectory = (File.GetAttributes(child) & FileAttributes.Directory) != 0;
                    if (isDirectory) Directory.Delete(child, recursive: true);
                    else
                    {
                        size = new FileInfo(child).Length;
                        File.Delete(child);
                    }
                    deleted++;
                    freed += size;
                }
                catch (Exception ex) when (IsFileSystemReadFailure(ex) || ex is DirectoryNotFoundException) { failed++; }
            }
        }
        progress?.Report("清理完成。");
        return new(deleted, freed, failed);
    }

    public static Task<IReadOnlyList<LargeFileEntry>> ScanLargeFilesAsync(string root, long minBytes, int maxResults,
        IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentOutOfRangeException.ThrowIfNegative(minBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxResults, 1);
        return Task.Run(() => ScanLargeFiles(root, minBytes, maxResults, progress, ct), CancellationToken.None);
    }

    private static IReadOnlyList<LargeFileEntry> ScanLargeFiles(string root, long minBytes, int maxResults,
        IProgress<string>? progress, CancellationToken ct)
    {
        // Min-heap of the current top-N: the smallest kept entry is at the front for cheap eviction.
        var heap = new PriorityQueue<LargeFileEntry, long>(maxResults);
        var pending = new Stack<string>();
        pending.Push(root);
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint, BufferSize = 16384 };
        var lastReport = TimeSpan.Zero;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        while (pending.TryPop(out var folder))
        {
            ct.ThrowIfCancellationRequested();
            if (clock.Elapsed - lastReport >= TimeSpan.FromMilliseconds(200))
            {
                progress?.Report("正在扫描 " + folder);
                lastReport = clock.Elapsed;
            }
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
            if (string.Equals(name, "$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "System Volume Information", StringComparison.OrdinalIgnoreCase)) continue;

            IEnumerable<string> children;
            try { children = Directory.EnumerateFileSystemEntries(ToLongPath(folder), "*", options); }
            catch (Exception ex) when (IsFileSystemReadFailure(ex)) { continue; }
            foreach (var child in children)
            {
                ct.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try { attributes = File.GetAttributes(child); }
                catch (Exception ex) when (IsFileSystemReadFailure(ex)) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(child); continue; }
                long length;
                DateTime modified;
                try
                {
                    var info = new FileInfo(child);
                    length = info.Length;
                    modified = info.LastWriteTime;
                }
                catch (Exception ex) when (IsFileSystemReadFailure(ex)) { continue; }
                if (length < minBytes) continue;
                // Strip the \\?\ prefix before showing the path to the user.
                var display = FromLongPath(child);
                if (heap.Count < maxResults) heap.Enqueue(new(display, length, modified), length);
                else if (heap.TryPeek(out _, out var smallest) && length > smallest)
                {
                    heap.Dequeue();
                    heap.Enqueue(new(display, length, modified), length);
                }
            }
        }
        clock.Stop();
        var result = new List<LargeFileEntry>(heap.Count);
        while (heap.TryDequeue(out var entry, out _)) result.Add(entry);
        result.Reverse();
        progress?.Report("扫描完成。");
        return result;
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }

    private static string ToLongPath(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : @"\\?\" + path;

    private static string FromLongPath(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;

    private static bool IsFileSystemReadFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or SecurityException;
}
