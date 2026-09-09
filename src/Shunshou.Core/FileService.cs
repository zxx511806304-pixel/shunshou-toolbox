using System.Security.Cryptography;
using System.Text.Json;

namespace Shunshou.Core;

public sealed record FileSearchResult(string Name, string FullPath, bool IsDirectory, long Size);
public sealed record RenameItem(string SourcePath, string TargetPath);

/// <summary>Scope-limited filename search and reversible batch renaming. Never follows junctions.</summary>
public sealed class FileService
{
    private readonly string _journalDirectory;
    public FileService(string? journalDirectory = null) => _journalDirectory = journalDirectory
        ?? Path.Combine(AppContext.BaseDirectory, "data", "rename-history");
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff", ".heic", ".avif", ".svg" };

    public Task<IReadOnlyList<FileSearchResult>> SearchAsync(string root, string query, bool imagesOnly,
        IProgress<ToolProgress>? progress, CancellationToken ct) => Task.Run<IReadOnlyList<FileSearchResult>>(() =>
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("请选择存在的搜索文件夹。");
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("请输入文件名关键词。");
        var result = new List<FileSearchResult>();
        var pending = new Stack<string>(); pending.Push(root);
        var visited = 0; var skipped = 0;
        while (pending.TryPop(out var folder))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var entries = Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions
                { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint });
                foreach (var path in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var attr = File.GetAttributes(path);
                        if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                        var isDir = (attr & FileAttributes.Directory) != 0;
                        if (isDir) pending.Push(path);
                        var name = Path.GetFileName(path);
                        if (name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) &&
                            (!imagesOnly || !isDir && Images.Contains(Path.GetExtension(path))))
                            result.Add(new(name, path, isDir, isDir ? 0 : new FileInfo(path).Length));
                        if (++visited % 300 == 0) progress?.Report(new(0, $"已扫描 {visited:N0} 项，找到 {result.Count:N0} 项"));
                        if (result.Count >= 10000)
                            throw new InvalidOperationException("匹配超过 10,000 项，请缩小搜索范围或输入更具体的名称。");
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { skipped++; }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { skipped++; }
        }
        progress?.Report(new(100, $"找到 {result.Count:N0} 项；扫描 {visited:N0} 项" + (skipped > 0 ? $"，跳过 {skipped} 个无法访问的项目" : "")));
        return result.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }, ct);

    public IReadOnlyList<RenameItem> PreviewRename(IEnumerable<string> paths, string prefix, int startNumber)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("请输入有效的文件名前缀，不能包含路径符号。");
        if (startNumber < 0) throw new ArgumentOutOfRangeException(nameof(startNumber));
        var list = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (list.Length == 0) throw new ArgumentException("请先添加需要改名的文件。");
        var items = list.Select((p, i) => new RenameItem(p, Path.Combine(Path.GetDirectoryName(p)!,
            $"{prefix}_{checked(startNumber + i):D3}{Path.GetExtension(p)}"))).ToArray();
        ValidateRenames(items);
        return items;
    }

    public async Task<string> ApplyRenameAsync(IEnumerable<RenameItem> items, CancellationToken ct)
    {
        var changes = items.Where(x => !StringComparer.OrdinalIgnoreCase.Equals(x.SourcePath, x.TargetPath)).ToArray();
        ValidateRenames(changes);
        if (changes.Length == 0) throw new InvalidOperationException("文件名无需修改。");
        // Store recovery metadata before touching a source. State distinguishes incomplete operations.
        var directory = _journalDirectory;
        Directory.CreateDirectory(directory);
        var journalPath = OutputPaths.Unique(directory, $"rename-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        var rows = new List<JournalItem>();
        foreach (var item in changes)
        {
            ct.ThrowIfCancellationRequested();
            rows.Add(new(item.SourcePath, item.TargetPath, await HashAsync(item.SourcePath, ct)));
        }
        var journal = new RenameJournal("pending", rows.ToArray());
        await File.WriteAllTextAsync(journalPath, JsonSerializer.Serialize(journal, JsonOptions), ct);
        var renamed = false;
        try
        {
            MoveBatch(changes, ct);
            renamed = true;
            journal = journal with { State = "completed" };
            await File.WriteAllTextAsync(journalPath, JsonSerializer.Serialize(journal, JsonOptions), CancellationToken.None);
            return journalPath;
        }
        catch
        {
            // If journal completion failed, restore filenames instead of reporting an untracked success.
            if (renamed)
                MoveBatch(changes.Select(x => new RenameItem(x.TargetPath, x.SourcePath)).ToArray(), CancellationToken.None);
            throw;
        }
    }

    public async Task UndoRenameAsync(string journalPath, CancellationToken ct)
    {
        var journal = JsonSerializer.Deserialize<RenameJournal>(await File.ReadAllTextAsync(journalPath, ct), JsonOptions)
            ?? throw new InvalidDataException("无法读取改名记录。");
        if (journal.State != "completed") throw new InvalidOperationException("该记录没有完成或已经撤销，不能重复撤销。");
        foreach (var item in journal.Items)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(item.TargetPath) || !StringComparer.Ordinal.Equals(await HashAsync(item.TargetPath, ct), item.Sha256))
                throw new InvalidOperationException($"文件已移动或修改，停止撤销以保护内容：{Path.GetFileName(item.TargetPath)}");
        }
        var reverse = journal.Items.Select(x => new RenameItem(x.TargetPath, x.SourcePath)).ToArray();
        ValidateRenames(reverse);
        MoveBatch(reverse, ct);
        await File.WriteAllTextAsync(journalPath, JsonSerializer.Serialize(journal with { State = "undone" }, JsonOptions), CancellationToken.None);
    }

    private static void ValidateRenames(IReadOnlyList<RenameItem> items)
    {
        var source = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var target = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (!File.Exists(item.SourcePath) || (File.GetAttributes(item.SourcePath) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException($"只支持存在的普通文件：{item.SourcePath}");
            if (!source.Add(Path.GetFullPath(item.SourcePath)) || !target.Add(Path.GetFullPath(item.TargetPath)))
                throw new InvalidOperationException("改名列表存在重复文件名。");
            if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(Path.GetFullPath(item.SourcePath)), Path.GetDirectoryName(Path.GetFullPath(item.TargetPath))))
                throw new InvalidOperationException("批量改名必须在原文件夹中完成。");
        }
        foreach (var item in items)
            if ((File.Exists(item.TargetPath) && !source.Contains(Path.GetFullPath(item.TargetPath))) || Directory.Exists(item.TargetPath))
                throw new IOException($"目标名称已存在：{Path.GetFileName(item.TargetPath)}");
    }

    private static void MoveBatch(IReadOnlyList<RenameItem> items, CancellationToken ct)
    {
        var staged = new List<(RenameItem Item, string Temp)>();
        var moved = new List<(RenameItem Item, string Temp)>();
        try
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var temp = Path.Combine(Path.GetDirectoryName(item.SourcePath)!, $".shunshou-rename-{Guid.NewGuid():N}.tmp");
                File.Move(item.SourcePath, temp);
                staged.Add((item, temp));
            }
            foreach (var row in staged)
            {
                ct.ThrowIfCancellationRequested();
                File.Move(row.Temp, row.Item.TargetPath, false);
                moved.Add(row);
            }
        }
        catch
        {
            // Undo completed destination moves before restoring originals; supports swaps.
            foreach (var row in moved.AsEnumerable().Reverse()) File.Move(row.Item.TargetPath, row.Temp, false);
            foreach (var row in staged.AsEnumerable().Reverse()) File.Move(row.Temp, row.Item.SourcePath, false);
            throw;
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private sealed record JournalItem(string SourcePath, string TargetPath, string Sha256);
    private sealed record RenameJournal(string State, JournalItem[] Items);
}
