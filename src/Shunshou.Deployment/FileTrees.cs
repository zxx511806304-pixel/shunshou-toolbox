using System.Security.Cryptography;

namespace Shunshou.Deployment;

internal static class FileTrees
{
    internal static async Task<TreeSnapshot> CaptureAsync(string root, CancellationToken ct)
    {
        PathSafety.NoLinks(root);
        var files = new List<FileSnapshot>();
        var directories = new List<DirectorySnapshot>();
        var pending = new Stack<string>();
        pending.Push(root);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            PathSafety.PlainEntry(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                var relative = PathSafety.Relative(Path.GetRelativePath(root, entry).Replace('\\', '/'));
                if (!paths.Add(relative) || paths.Count > PathSafety.MaxEntries) throw new IOException("目录文件过多或路径存在大小写冲突，暂不自动更新。");
                PathSafety.PlainEntry(entry);
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Add(new(relative, Directory.GetLastWriteTimeUtc(entry), attributes));
                    pending.Push(entry);
                }
                else
                {
                    var info = new FileInfo(entry);
                    bytes = checked(bytes + info.Length);
                    if (bytes > PathSafety.MaxUserBytes + PathSafety.MaxPackageBytes) throw new IOException("此目录的数据超过 24 GB，请先单独备份数据再更新。");
                    var modified = info.LastWriteTimeUtc;
                    var length = info.Length;
                    await using var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
                    var zone = await ReadZoneAsync(entry, ct);
                    info.Refresh();
                    if (length != info.Length || modified != info.LastWriteTimeUtc || attributes != info.Attributes)
                        throw new IOException("文件正在变化，请关闭相关软件再更新：" + relative);
                    files.Add(new(relative, length, hash, modified, attributes, zone.Hash, zone.Bytes));
                }
            }
        }
        return new(files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(),
            directories.OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase).ToList());
    }

    internal static void ValidateSnapshot(TreeSnapshot? snapshot)
    {
        if (snapshot?.Files is null || snapshot.Directories is null
            || snapshot.Files.Count + snapshot.Directories.Count > PathSafety.MaxEntries)
            throw new InvalidDataException("更新恢复记录不完整。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var file in snapshot.Files)
        {
            if (file is null) throw new InvalidDataException("更新恢复记录无效。");
            PathSafety.Relative(file.Path);
            if (!paths.Add(file.Path) || file.Bytes < 0 || !PathSafety.IsHash(file.Sha256)
                || (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0
                || file.ZoneIdentifierBytes is < 0 or > 65536
                || file.ZoneIdentifierSha256 is not null && !PathSafety.IsHash(file.ZoneIdentifierSha256)
                || file.ZoneIdentifierSha256 is null && file.ZoneIdentifierBytes != 0)
                throw new InvalidDataException("更新恢复文件记录无效。");
            bytes = checked(bytes + file.Bytes);
            if (bytes > PathSafety.MaxUserBytes + PathSafety.MaxPackageBytes) throw new InvalidDataException("恢复记录体积过大。");
        }
        foreach (var dir in snapshot.Directories)
        {
            if (dir is null) throw new InvalidDataException("更新恢复记录无效。");
            PathSafety.Relative(dir.Path);
            if (!paths.Add(dir.Path) || (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("更新恢复目录记录无效。");
        }
    }

    internal static void Equal(TreeSnapshot expected, TreeSnapshot actual)
    {
        if (expected.Files.Count != actual.Files.Count || expected.Directories.Count != actual.Directories.Count)
            throw new IOException("软件目录在更新期间发生变化，已停止自动切换。");
        var actualFiles = actual.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var file in expected.Files)
        {
            if (!actualFiles.TryGetValue(file.Path, out var found) || file.Bytes != found.Bytes
                || !PathSafety.HashEquals(file.Sha256, found.Sha256) || file.LastWriteUtc != found.LastWriteUtc || file.Attributes != found.Attributes
                || !string.Equals(file.ZoneIdentifierSha256, found.ZoneIdentifierSha256, StringComparison.OrdinalIgnoreCase)
                || file.ZoneIdentifierBytes != found.ZoneIdentifierBytes)
                throw new IOException("文件在更新期间发生变化，已停止自动切换：" + file.Path);
        }
        var actualDirectories = actual.Directories.ToDictionary(d => d.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var directory in expected.Directories)
        {
            // Directory timestamps can change due to filesystem bookkeeping; contents and attributes are authoritative.
            if (!actualDirectories.TryGetValue(directory.Path, out var found) || directory.Attributes != found.Attributes)
                throw new IOException("目录在更新期间发生变化：" + directory.Path);
        }
    }

    internal static async Task<PackageManifest> ReadInstalledManifestAsync(string root, string architecture, TreeSnapshot tree, CancellationToken ct)
    {
        var path = PathSafety.Under(root, PathSafety.ManifestName);
        PathSafety.PlainEntry(path);
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("软件清单过大。");
        var manifest = PackageContent.ParseManifest(await File.ReadAllBytesAsync(path, ct), architecture);
        VerifyManaged(manifest.Files, tree);
        return manifest;
    }

    internal static void VerifyManaged(IEnumerable<PackageFile> files, TreeSnapshot tree)
    {
        var lookup = tree.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (!lookup.TryGetValue(file.Path, out var found) || file.Bytes != found.Bytes || !PathSafety.HashEquals(file.Sha256, found.Sha256))
                throw new IOException("软件文件缺失或已被修改，为保护现有内容未更新：" + file.Path);
        }
    }

    internal static async Task CopyPreservedAsync(string sourceRoot, string stage, TreeSnapshot preserved,
        IProgress<DeploymentProgress>? progress, CancellationToken ct)
    {
        foreach (var directory in preserved.Directories.OrderBy(d => d.Path.Count(c => c == '/')))
        {
            ct.ThrowIfCancellationRequested();
            var path = PathSafety.Under(stage, directory.Path);
            PathSafety.NoLinks(path);
            Directory.CreateDirectory(path);
        }
        var index = 0;
        foreach (var file in preserved.Files)
        {
            ct.ThrowIfCancellationRequested();
            var source = PathSafety.Under(sourceRoot, file.Path);
            var destination = PathSafety.Under(stage, file.Path);
            PathSafety.PlainEntry(source);
            PathSafety.NoLinks(destination);
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous))
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
            {
                await input.CopyToAsync(output, ct);
                output.Flush(flushToDisk: true);
            }
            if (file.ZoneIdentifierSha256 is not null)
            {
                await using var zoneSource = new FileStream(source + ":Zone.Identifier", FileMode.Open, FileAccess.Read, FileShare.Read);
                if (zoneSource.Length != file.ZoneIdentifierBytes) throw new IOException("文件下载来源标记在更新期间发生变化：" + file.Path);
                await using var zoneTarget = new FileStream(destination + ":Zone.Identifier", FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await zoneSource.CopyToAsync(zoneTarget, ct);
                zoneTarget.Flush(flushToDisk: true);
            }
            File.SetLastWriteTimeUtc(destination, file.LastWriteUtc);
            File.SetAttributes(destination, file.Attributes);
            progress?.Report(new("正在保留设置、历史记录和用户文件…", 45 + ++index * 25 / Math.Max(1, preserved.Files.Count)));
        }
        foreach (var directory in preserved.Directories.OrderByDescending(d => d.Path.Count(c => c == '/')))
        {
            var path = PathSafety.Under(stage, directory.Path);
            Directory.SetLastWriteTimeUtc(path, directory.LastWriteUtc);
            File.SetAttributes(path, directory.Attributes);
        }
    }

    private static async Task<(string? Hash, long Bytes)> ReadZoneAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(path + ":Zone.Identifier", FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 65536) throw new IOException("文件下载来源标记过大：" + path);
            return (Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)), stream.Length);
        }
        catch (FileNotFoundException) { return (null, 0); }
    }
}
