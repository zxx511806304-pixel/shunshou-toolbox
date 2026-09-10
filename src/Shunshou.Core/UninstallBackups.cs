using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Shunshou.Core;

public sealed partial class UninstallService
{
    public IReadOnlyList<string> ListBackupManifests()
    {
        var root = Path.GetFullPath(_environment.BackupDirectory);
        if (!Directory.Exists(root)) return [];
        EnsureNoLinks(root);
        return Directory.EnumerateDirectories(root).Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0)
            .Select(d => Path.Combine(d, "backup.json")).Where(File.Exists).OrderDescending(StringComparer.Ordinal).ToArray();
    }

    public async Task<RestoreResult> RestoreBackupAsync(string manifestPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var journal = await LoadJournalAsync(manifestPath, ct).ConfigureAwait(false);
        var directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        if (journal.Version != 1 || journal.Items.Count > 1000) throw new InvalidDataException("备份版本或条目数量无效。");
        var pending = journal.Items.Where(e => e.State is "removed" or "removing" or "error").ToArray();
        if (pending.Length == 0) throw new InvalidOperationException("此记录没有需要恢复的已清理条目。");
        if (pending.Any(e => e.Item.RequiresElevation) && !IsAdministrator)
            throw new UnauthorizedAccessException("此备份需要管理员权限恢复，请以管理员身份重新打开软件。");
        // Preflight the complete restore before writing anything. Never overwrite later user edits.
        foreach (var entry in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Tree is not null)
            {
                ValidateDirectory(entry.Item.Path, journal.Application, []);
                var backup = BackupPath(directory, entry.BackupRelativePath);
                if (!TreesEqual(entry.Tree, await CaptureTreeAsync(backup, ct).ConfigureAwait(false)))
                    throw new IOException("备份文件发生变化，无法自动恢复。");
                await ValidateRestoreDestinationAsync(entry.Item.Path, entry.Tree, ct).ConfigureAwait(false);
            }
            else
            {
                ValidateRegistryIdentity(entry.RegistryIdentity ?? throw new InvalidDataException("备份注册表身份缺失。"));
                var exists = _platform.ReadRegistry(entry.RegistryIdentity);
                if (exists is not null && RegistryFingerprint(exists) != RegistryFingerprint(entry.Registry!))
                    throw new IOException("原注册表项已经存在且内容不同，未覆盖任何内容。");
            }
        }
        var errors = new List<string>();
        var restored = 0;
        foreach (var entry in pending)
        {
            if (ct.IsCancellationRequested) { errors.Add("已取消恢复，尚未恢复的条目仍保留在备份中。"); break; }
            try
            {
                if (entry.Tree is not null)
                {
                    await ValidateRestoreDestinationAsync(entry.Item.Path, entry.Tree, ct).ConfigureAwait(false);
                    await CopyTreeAsync(BackupPath(directory, entry.BackupRelativePath), entry.Item.Path, entry.Tree, ct,
                        allowMatchingExisting: true).ConfigureAwait(false);
                    if (!TreesEqual(entry.Tree, await CaptureTreeAsync(entry.Item.Path, ct).ConfigureAwait(false)))
                        throw new IOException("恢复后校验未通过，备份已保留。");
                }
                else if (_platform.ReadRegistry(entry.RegistryIdentity!) is null)
                    _platform.RestoreRegistry(entry.RegistryIdentity!, entry.Registry!);
                entry.State = "restored"; entry.Error = null; restored++;
            }
            catch (Exception ex) { errors.Add($"{entry.Item.Path}：{ex.Message}"); entry.Error = ex.Message; }
            await SaveJournalAsync(manifestPath, journal).ConfigureAwait(false);
        }
        journal.State = errors.Count == 0 ? "restored" : "restore-partial";
        await SaveJournalAsync(manifestPath, journal).ConfigureAwait(false);
        return new(restored, errors);
    }

    private async Task SaveJournalAsync(string path, CleanupJournal journal)
    {
        ValidateManifestPath(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions);
        var keyPath = Path.Combine(Path.GetFullPath(_environment.BackupDirectory), ".integrity-key");
        if (!File.Exists(keyPath))
        {
            using var stream = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await stream.WriteAsync(RandomNumberGenerator.GetBytes(32)).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        EnsureNoLinks(keyPath);
        var key = await File.ReadAllBytesAsync(keyPath).ConfigureAwait(false);
        if (key.Length != 32) throw new InvalidDataException("备份完整性密钥无效。");
        var signature = Convert.ToHexString(HMACSHA256.HashData(key, bytes));
        // Single authenticated envelope avoids a crash between separate manifest/signature writes.
        var envelope = new JournalEnvelope(Convert.ToBase64String(bytes), signature);
        var temporary = path + ".tmp";
        EnsureNoLinks(temporary);
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(envelope, JsonOptions), CancellationToken.None).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    private async Task<CleanupJournal> LoadJournalAsync(string path, CancellationToken ct)
    {
        ValidateManifestPath(path);
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("备份记录过大。");
        var envelope = JsonSerializer.Deserialize<JournalEnvelope>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false))
            ?? throw new InvalidDataException("无法读取备份记录。");
        var bytes = Convert.FromBase64String(envelope.Payload);
        var keyPath = Path.Combine(Path.GetFullPath(_environment.BackupDirectory), ".integrity-key");
        EnsureNoLinks(keyPath);
        var key = await File.ReadAllBytesAsync(keyPath, ct).ConfigureAwait(false);
        if (key.Length != 32 || !CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, bytes), Convert.FromHexString(envelope.Signature)))
            throw new InvalidDataException("备份记录完整性校验失败，拒绝执行恢复。");
        return JsonSerializer.Deserialize<CleanupJournal>(bytes) ?? throw new InvalidDataException("备份记录无效。");
    }

    private void ValidateManifestPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(_environment.BackupDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (Path.GetFileName(full) != "backup.json" || !string.Equals(Path.GetDirectoryName(Path.GetDirectoryName(full)), root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只能恢复工具箱在自身备份目录中生成的记录。");
        EnsureNoLinks(full);
    }

    private static string BackupPath(string directory, string relative)
    {
        if (relative.Length != 32 || !Guid.TryParseExact(relative, "N", out _)) throw new InvalidDataException("备份目录标识无效。");
        var path = Path.Combine(directory, relative);
        EnsureNoLinks(path);
        return path;
    }

    private static async Task<DirectorySnapshot> CaptureTreeAsync(string directory, CancellationToken ct)
    {
        directory = Path.GetFullPath(directory);
        EnsureNoLinks(directory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("目录已不存在。");
        var dirs = new List<string>();
        var files = new List<FileSnapshot>();
        var stack = new Stack<string>(); stack.Push(directory);
        long total = 0;
        while (stack.TryPop(out var current))
        {
            ct.ThrowIfCancellationRequested();
            EnsureNoLinks(current);
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                ct.ThrowIfCancellationRequested();
                if (dirs.Count + files.Count > 20_000) throw new IOException("目录包含过多文件，跳过自动清理。");
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Encrypted | FileAttributes.Offline)) != 0)
                    throw new IOException("目录包含链接、加密文件或云占位文件，跳过自动清理。");
                var relative = Path.GetRelativePath(directory, entry);
                if ((attributes & FileAttributes.Directory) != 0) { dirs.Add(relative); stack.Push(entry); }
                else
                {
                    EnsureNoAlternateStreams(entry);
                    var info = new FileInfo(entry);
                    total += info.Length;
                    if (total > 2L * 1024 * 1024 * 1024) throw new IOException("目录超过 2 GB，跳过自动清理。");
                    await using var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, useAsync: true);
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false));
                    files.Add(new(relative, input.Length, hash, info.LastWriteTimeUtc.Ticks, attributes));
                }
            }
        }
        return new(dirs.Order(StringComparer.Ordinal).ToArray(), files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToArray());
    }

    private static bool TreesEqual(DirectorySnapshot a, DirectorySnapshot b) =>
        a.Directories.SequenceEqual(b.Directories, StringComparer.Ordinal) && a.Files.Count == b.Files.Count &&
        a.Files.Zip(b.Files).All(p => p.First.RelativePath == p.Second.RelativePath && p.First.Length == p.Second.Length &&
            p.First.Sha256 == p.Second.Sha256 && p.First.LastWriteUtcTicks == p.Second.LastWriteUtcTicks &&
            p.First.Attributes == p.Second.Attributes);

    private static async Task CopyTreeAsync(string source, string destination, DirectorySnapshot tree, CancellationToken ct,
        bool allowMatchingExisting = false)
    {
        EnsureNoLinks(source); EnsureNoLinks(destination);
        Directory.CreateDirectory(destination);
        foreach (var relative in tree.Directories.OrderBy(d => d.Length))
        { ct.ThrowIfCancellationRequested(); Directory.CreateDirectory(ResolveRelative(destination, relative)); }
        foreach (var file in tree.Files)
        {
            ct.ThrowIfCancellationRequested();
            var from = ResolveRelative(source, file.RelativePath);
            var to = ResolveRelative(destination, file.RelativePath);
            EnsureNoLinks(from); EnsureNoLinks(to);
            if (allowMatchingExisting && File.Exists(to))
            {
                await using var existing = new FileStream(to, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                if (Convert.ToHexString(await SHA256.HashDataAsync(existing, ct).ConfigureAwait(false)) != file.Sha256)
                    throw new IOException("原位置已有不同内容，未覆盖。");
                continue;
            }
            await using (var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
            await using (var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            { await input.CopyToAsync(output, ct).ConfigureAwait(false); output.Flush(flushToDisk: true); }
            File.SetLastWriteTimeUtc(to, new DateTime(file.LastWriteUtcTicks, DateTimeKind.Utc));
            File.SetAttributes(to, file.Attributes);
        }
    }

    private static async Task ValidateRestoreDestinationAsync(string path, DirectorySnapshot tree, CancellationToken ct)
    {
        EnsureNoLinks(path);
        if (File.Exists(path)) throw new IOException("原目录位置被文件占用。");
        if (!Directory.Exists(path)) return;
        var current = await CaptureTreeAsync(path, ct).ConfigureAwait(false);
        if (current.Directories.Except(tree.Directories, StringComparer.Ordinal).Any() ||
            current.Files.Any(f => !tree.Files.Any(original => original == f)))
            throw new IOException("原位置已有新增或修改的内容，未覆盖任何文件。");
    }

    private static async Task RemoveTreeAsync(string directory, DirectorySnapshot tree, CancellationToken ct)
    {
        EnsureNoLinks(directory);
        // Every deletion targets a verified open file handle, with writers and renames excluded.
        // No recursive Directory.Delete can follow a newly introduced link or remove an unlisted file.
        foreach (var file in tree.Files)
        {
            ct.ThrowIfCancellationRequested();
            var path = ResolveRelative(directory, file.RelativePath);
            EnsureNoLinks(path);
            using var handle = CreateFile(path, 0x80000000u | 0x00010000u, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法锁定待清理文件，备份已保留。");
            using var input = new FileStream(handle, FileAccess.Read);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false));
            if (input.Length != file.Length || hash != file.Sha256) throw new IOException("文件在清理前发生变化，已停止清理该目录。");
            ct.ThrowIfCancellationRequested();
            var disposition = new FileDisposition { DeleteFile = true };
            if (!SetFileInformationByHandle(handle, 4, ref disposition, 1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法清理文件，备份已保留。");
        }
        foreach (var relative in tree.Directories.OrderByDescending(d => d.Length))
        {
            ct.ThrowIfCancellationRequested();
            var path = ResolveRelative(directory, relative); EnsureNoLinks(path);
            Directory.Delete(path, recursive: false);
        }
        EnsureNoLinks(directory);
        Directory.Delete(directory, recursive: false);
    }

    private static string ResolveRelative(string root, string relative)
    {
        if (Path.IsPathFullyQualified(relative) || relative.Split('\\', '/').Any(p => p is ".." or "." or ""))
            throw new InvalidDataException("文件快照路径无效。");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsInside(path, root) || path.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("文件快照越过目录范围。");
        return path;
    }

    private static void EnsureNoLinks(string path)
    {
        for (var cursor = Path.GetFullPath(path); !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
        {
            if ((Directory.Exists(cursor) || File.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("路径包含目录链接或重解析点，已跳过。");
        }
    }
    private static void EnsureNoAlternateStreams(string path)
    {
        var handle = FindFirstStream(path, 0, out var stream, 0);
        if (handle == new IntPtr(-1))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 38 or 1 or 50) return; // End of streams, or a filesystem without named streams.
            throw new Win32Exception(error, "无法检查文件的附加数据流。");
        }
        try
        {
            do
            {
                if (!stream.Name.Equals("::$DATA", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("文件包含附加数据流，跳过自动清理以保留全部内容。");
            } while (FindNextStream(handle, out stream));
            if (Marshal.GetLastWin32Error() != 38) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { FindClose(handle); }
    }
    private sealed record JournalEnvelope(string Payload, string Signature);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamData { public long Size; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name; }
    [DllImport("kernel32.dll", EntryPoint = "FindFirstStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStream(string name, int level, out StreamData data, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "FindNextStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindNextStream(IntPtr handle, out StreamData data);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindClose(IntPtr handle);
    [StructLayout(LayoutKind.Sequential)] private struct FileDisposition { [MarshalAs(UnmanagedType.U1)] public bool DeleteFile; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref FileDisposition information, uint size);
}
