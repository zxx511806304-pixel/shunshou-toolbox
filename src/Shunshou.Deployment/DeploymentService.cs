using System.Text.Json;

namespace Shunshou.Deployment;

/// <summary>Offline full-package deployment. Retains every previous folder; never deletes user data or backups.</summary>
public sealed class DeploymentService
{
    internal const int MaxJournalBytes = 64 * 1024 * 1024;
    private readonly Action<DeploymentCheckpoint>? _checkpoint;
    public DeploymentService() { }
    internal DeploymentService(Action<DeploymentCheckpoint> checkpoint) => _checkpoint = checkpoint;

    public async Task<DeploymentInspection> InspectAsync(DeploymentRequest request, CancellationToken ct = default)
    {
        var target = PathSafety.Target(request.TargetDirectory);
        using var package = await PackageContent.OpenAsync(request, ct);
        var source = await InspectSourceAsync(target, package, ct);
        return source.Inspection;
    }

    public async Task<DeploymentResult> InstallOrUpdateAsync(DeploymentRequest request,
        IProgress<DeploymentProgress>? progress = null, CancellationToken ct = default)
    {
        var target = PathSafety.Target(request.TargetDirectory);
        using var lease = AcquireLease(target);
        await RecoverCoreAsync(target, progress, ct);
        progress?.Report(new("正在检查安装包和现有文件…", 1));
        using var package = await PackageContent.OpenAsync(request, ct);
        var source = await InspectSourceAsync(target, package, ct);
        var free = new DriveInfo(Path.GetPathRoot(target)!).AvailableFreeSpace;
        if (free < source.Inspection.RequiredFreeBytes)
            throw new IOException("磁盘空间不足。更新需要额外空间保存新版和用户数据，旧版备份不会自动删除。");
        ct.ThrowIfCancellationRequested();
        var journalPath = PathSafety.JournalPath(target);
        ArchiveFinishedJournal(target, journalPath);
        var transaction = Guid.NewGuid().ToString("N");
        var journal = new DeploymentJournal
        {
            Transaction = transaction, Target = target,
            Stage = PathSafety.Sibling(target, "stage", transaction),
            Backup = PathSafety.Sibling(target, "backup", transaction),
            HadTarget = source.HadTarget, WasUpdate = source.Inspection.IsUpdate,
            Version = package.Manifest.Version, Architecture = package.Manifest.Architecture,
            OldVersion = source.Inspection.CurrentVersion, OldTree = source.Tree
        };
        if (Exists(journal.Stage) || Exists(journal.Backup)) throw new IOException("临时更新目录已经存在，请重试。");
        WriteJournal(journalPath, journal);
        PathSafety.NoLinks(journal.Stage);
        Directory.CreateDirectory(journal.Stage);
        try
        {
            FileTrees.PreparePermissions(journal.Stage, source.Tree, ct);
            await package.ExtractAsync(journal.Stage, progress, ct);
            await FileTrees.CopyPreservedAsync(target, journal.Stage, source.Preserved, progress, ct);
            progress?.Report(new("正在核对新版和保留文件…", 75));
            var newTree = await FileTrees.CaptureAsync(journal.Stage, ct);
            FileTrees.VerifyManaged(package.Files.Values, newTree);
            VerifyPreserved(source.Preserved, newTree);
            if (newTree.Files.Count != package.Files.Count + source.Preserved.Files.Count)
                throw new IOException("临时目录出现了额外文件，已停止更新。");
            journal.NewTree = newTree;
            await VerifyOldAsync(journal, ct);
            journal.State = "Prepared";
            WriteJournal(journalPath, journal);
            _checkpoint?.Invoke(DeploymentCheckpoint.Prepared);
            request.BeforeCommit?.Invoke();
            // Catch files changed during process checks before entering the uncancellable rename phase.
            await VerifyOldAsync(journal, ct);
            FileTrees.Equal(newTree, await FileTrees.CaptureAsync(journal.Stage, ct));
            ct.ThrowIfCancellationRequested();
        }
        catch
        {
            journal.State = "Abandoned";
            TryWriteJournal(journalPath, journal);
            throw;
        }

        progress?.Report(new("正在切换软件版本，请稍候…", 95));
        // From this point onward neither cancellation nor UI closure can interrupt rollback.
        try
        {
            PathSafety.NoLinks(target);
            PathSafety.NoLinks(journal.Stage);
            PathSafety.NoLinks(journal.Backup);
            if (journal.HadTarget) Directory.Move(target, journal.Backup);
            _checkpoint?.Invoke(DeploymentCheckpoint.OldRenamedBeforeJournal);
            journal.State = "OldMoved";
            WriteJournal(journalPath, journal);
            _checkpoint?.Invoke(DeploymentCheckpoint.OldMoved);
            Directory.Move(journal.Stage, target);
            _checkpoint?.Invoke(DeploymentCheckpoint.NewActivatedBeforeJournal);
            journal.State = "Completed";
            WriteJournal(journalPath, journal);
        }
        catch (Exception error)
        {
            // A failed completion-journal write can happen after activation. Verify that case and retain it.
            if (Directory.Exists(target) && !Exists(journal.Stage) && journal.NewTree is not null)
            {
                try
                {
                    FileTrees.Equal(journal.NewTree, await FileTrees.CaptureAsync(target, CancellationToken.None));
                    journal.State = "Completed";
                    TryWriteJournal(journalPath, journal);
                    progress?.Report(new("软件已更新，旧版备份已保留。", 100));
                    return Result(journal, journalPath);
                }
                catch { /* Leave the journal for conservative recovery below. */ }
            }
            if (journal.HadTarget && !Exists(target) && Directory.Exists(journal.Backup))
            {
                try
                {
                    await ValidateOldLocationAsync(journal, journal.Backup, CancellationToken.None);
                    PathSafety.NoLinks(target);
                    Directory.Move(journal.Backup, target);
                    journal.State = "RolledBack";
                    WriteJournal(journalPath, journal);
                }
                catch (Exception rollbackError)
                {
                    throw new IOException("版本切换中断，原文件仍保留在备份目录。请再次打开更新包恢复。备份：" + journal.Backup,
                        new AggregateException(error, rollbackError));
                }
            }
            else if (!journal.HadTarget && !Exists(target))
            {
                journal.State = "Abandoned";
                TryWriteJournal(journalPath, journal);
            }
            throw new IOException("更新未完成，原有文件已保留。可以关闭占用文件的程序后重试。", error);
        }
        progress?.Report(new("软件已准备好，旧版备份已保留。", 100));
        return Result(journal, journalPath);
    }

    public async Task<DeploymentRecoveryResult> RecoverAsync(string targetDirectory,
        IProgress<DeploymentProgress>? progress = null, CancellationToken ct = default)
    {
        var target = PathSafety.Target(targetDirectory);
        using var lease = AcquireLease(target);
        return await RecoverCoreAsync(target, progress, ct);
    }

    private static async Task<DeploymentRecoveryResult> RecoverCoreAsync(string target, IProgress<DeploymentProgress>? progress, CancellationToken ct)
    {
        var path = PathSafety.JournalPath(target);
        if (!File.Exists(path)) return new(false, "没有需要恢复的更新。", null);
        var journal = ReadJournal(target, path);
        if (journal.State is "Completed" or "RolledBack" or "Abandoned")
            return new(false, "上一次更新已结束。", journal.State == "Completed" && journal.HadTarget ? journal.Backup : null);
        progress?.Report(new("正在检查上次中断的更新…", null));
        ct.ThrowIfCancellationRequested();
        var hasTarget = Exists(target);
        var hasStage = Exists(journal.Stage);
        var hasBackup = Exists(journal.Backup);
        // A physical rename may have completed just before its next journal write.
        if (hasTarget && !hasStage && journal.NewTree is not null && (hasBackup == journal.HadTarget))
        {
            await ValidateNewLocationAsync(journal, target, ct);
            if (hasBackup) await ValidateOldLocationAsync(journal, journal.Backup, ct);
            journal.State = "Completed";
            WriteJournal(path, journal);
            return new(true, "已确认上次更新完成，旧版备份仍保留。", hasBackup ? journal.Backup : null);
        }
        if (!hasTarget && hasBackup && journal.HadTarget)
        {
            await ValidateOldLocationAsync(journal, journal.Backup, ct);
            ct.ThrowIfCancellationRequested();
            PathSafety.NoLinks(target);
            Directory.Move(journal.Backup, target);
            journal.State = "RolledBack";
            WriteJournal(path, journal);
            return new(true, "已恢复上次更新前的软件和数据。", null);
        }
        if (!hasBackup && hasTarget == journal.HadTarget)
        {
            if (hasTarget) await ValidateOldLocationAsync(journal, target, ct);
            journal.State = "Abandoned";
            WriteJournal(path, journal);
            return new(true, "上次更新尚未切换版本，原软件和临时文件均已保留。", null);
        }
        throw new IOException("上次更新目录发生了额外变化，无法自动恢复。请保留这些目录并检查：" + target + "；" + journal.Backup + "；" + journal.Stage);
    }

    private static async Task<SourceInspection> InspectSourceAsync(string target, PackageContent package, CancellationToken ct)
    {
        var hadTarget = Directory.Exists(target);
        var tree = hadTarget ? await FileTrees.CaptureAsync(target, ct) : new TreeSnapshot([], []);
        PackageManifest? old = null;
        if (tree.Files.Count != 0 || tree.Directories.Count != 0)
        {
            if (!tree.Files.Any(f => f.Path.Equals(PathSafety.ManifestName, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("所选文件夹不是完整的顺手工具箱目录。首次解压请选择新建或空文件夹。");
            old = await FileTrees.ReadInstalledManifestAsync(target, package.Manifest.Architecture, tree, ct);
            if (PackageContent.VersionNumber(old.Version) > PackageContent.VersionNumber(package.Manifest.Version))
                throw new IOException("此更新包比已安装版本旧，不能降级覆盖。");
        }
        var managed = old?.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        managed.Add(PathSafety.ManifestName);
        var preserved = new TreeSnapshot(tree.Files.Where(f => !managed.Contains(f.Path)).ToList(), tree.Directories, tree.RootAccessSddl);
        var preservedBytes = preserved.Files.Sum(f => f.Bytes);
        if (preservedBytes > PathSafety.MaxUserBytes) throw new IOException("需要保留的数据超过 20 GB，请先单独备份后更新。");
        foreach (var file in preserved.Files)
            if (package.Files.ContainsKey(file.Path) || package.Directories.Contains(file.Path))
                throw new IOException("新版文件与您保存的文件重名，未覆盖：" + file.Path);
        foreach (var directory in preserved.Directories)
            if (package.Files.ContainsKey(directory.Path)) throw new IOException("新版文件与现有目录重名，未覆盖：" + directory.Path);
        var required = checked(package.Files.Values.Sum(f => f.Bytes) + preservedBytes + 64L * 1024 * 1024);
        return new(hadTarget, tree, preserved,
            new(old is not null, old?.Version, package.Manifest.Version, preservedBytes, preserved.Files.Count, required));
    }

    private static void VerifyPreserved(TreeSnapshot expected, TreeSnapshot actual)
    {
        var files = expected.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dirs = expected.Directories.Select(d => d.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        FileTrees.Equal(expected, new(actual.Files.Where(f => files.Contains(f.Path)).ToList(), actual.Directories.Where(d => dirs.Contains(d.Path)).ToList(), actual.RootAccessSddl));
    }

    private static async Task VerifyOldAsync(DeploymentJournal journal, CancellationToken ct)
    {
        if (journal.HadTarget) await ValidateOldLocationAsync(journal, journal.Target, ct);
        else if (Exists(journal.Target)) throw new IOException("目标文件夹在更新期间被创建，已停止切换。");
    }

    private static async Task ValidateOldLocationAsync(DeploymentJournal journal, string location, CancellationToken ct)
    {
        var tree = await FileTrees.CaptureAsync(location, ct);
        FileTrees.Equal(journal.OldTree!, tree);
        if (journal.WasUpdate)
        {
            var old = await FileTrees.ReadInstalledManifestAsync(location, journal.Architecture, tree, ct);
            if (old.Version != journal.OldVersion) throw new IOException("旧版文件夹与恢复记录不一致。");
        }
        else if (tree.Files.Count != 0 || tree.Directories.Count != 0) throw new IOException("首次安装的原目录不再为空。");
    }

    private static async Task ValidateNewLocationAsync(DeploymentJournal journal, string location, CancellationToken ct)
    {
        var tree = await FileTrees.CaptureAsync(location, ct);
        var manifest = await FileTrees.ReadInstalledManifestAsync(location, journal.Architecture, tree, ct);
        if (manifest.Version != journal.Version) throw new IOException("新版文件夹与恢复记录不一致。");
        // Once stage has been renamed, the app can create settings or users can save output. Those
        // changes must never cause a rollback of an activated target. Pin the exact package manifest
        // and every managed file; leave all post-activation user data at its existing location.
        var originalManifest = journal.NewTree!.Files.Single(f => f.Path.Equals(PathSafety.ManifestName, StringComparison.OrdinalIgnoreCase));
        FileTrees.VerifyManaged([new(PathSafety.ManifestName, originalManifest.Bytes, originalManifest.Sha256)], tree);
    }

    private static DeploymentResult Result(DeploymentJournal journal, string path) =>
        new(journal.Target, journal.Version, journal.HadTarget ? journal.Backup : null, path, journal.WasUpdate);

    private static FileStream AcquireLease(string target)
    {
        var path = PathSafety.JournalPath(target) + ".lock";
        PathSafety.NoLinks(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("另一个更新正在处理此软件目录，或目录当前不可写。", ex); }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static DeploymentJournal ReadJournal(string target, string path)
    {
        PathSafety.PlainEntry(path);
        if (new FileInfo(path).Length > MaxJournalBytes) throw new InvalidDataException("更新恢复记录过大。");
        DeploymentJournal journal;
        try { journal = JsonSerializer.Deserialize<DeploymentJournal>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("更新恢复记录为空。"); }
        catch (JsonException ex) { throw new InvalidDataException("更新恢复记录损坏，请保留更新目录。", ex); }
        if (journal.Format != 1 || journal.Product != "顺手工具箱" || !Guid.TryParseExact(journal.Transaction, "N", out _)
            || !string.Equals(journal.Target, target, StringComparison.OrdinalIgnoreCase)
            || journal.Stage != PathSafety.Sibling(target, "stage", journal.Transaction)
            || journal.Backup != PathSafety.Sibling(target, "backup", journal.Transaction)
            || journal.Architecture != "win-x64" || journal.WasUpdate && !journal.HadTarget
            || journal.State is not ("Staging" or "Prepared" or "OldMoved" or "Completed" or "RolledBack" or "Abandoned"))
            throw new InvalidDataException("更新恢复记录不属于此软件目录。");
        _ = PackageContent.VersionNumber(journal.Version);
        if (journal.WasUpdate) _ = PackageContent.VersionNumber(journal.OldVersion);
        PathSafety.NoLinks(journal.Stage);
        PathSafety.NoLinks(journal.Backup);
        FileTrees.ValidateSnapshot(journal.OldTree);
        if (journal.NewTree is not null) FileTrees.ValidateSnapshot(journal.NewTree);
        if (journal.State is "Prepared" or "OldMoved" or "Completed" && journal.NewTree is null)
            throw new InvalidDataException("更新恢复记录缺少新版文件校验。");
        return journal;
    }

    private static void WriteJournal(string path, DeploymentJournal journal)
    {
        PathSafety.NoLinks(path);
        var temporary = path + ".write-" + Guid.NewGuid().ToString("N");
        PathSafety.NoLinks(temporary);
        var bytes = SerializeJournal(journal);
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
        {
            output.Write(bytes);
            output.Flush(flushToDisk: true);
        }
        PathSafety.NoLinks(path);
        File.Move(temporary, path, overwrite: true);
    }

    internal static byte[] SerializeJournal(DeploymentJournal journal)
    {
        using var output = new BoundedJournalStream();
        JsonSerializer.Serialize(output, journal);
        return output.ToArray();
    }

    private sealed class BoundedJournalStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Guard(count);
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Guard(buffer.Length);
            base.Write(buffer);
        }
        private void Guard(int count)
        {
            if (Length + count > MaxJournalBytes)
                throw new IOException("更新恢复记录超过 64 MB，目录文件或路径过多。原软件未切换，请先单独备份整理数据。");
        }
    }

    private static void TryWriteJournal(string path, DeploymentJournal journal) { try { WriteJournal(path, journal); } catch { } }

    private static void ArchiveFinishedJournal(string target, string path)
    {
        if (!File.Exists(path)) return;
        var journal = ReadJournal(target, path);
        if (journal.State is not ("Completed" or "RolledBack" or "Abandoned")) throw new IOException("上次更新尚未恢复。");
        var archive = path + "." + journal.Transaction + "." + journal.State.ToLowerInvariant();
        PathSafety.NoLinks(archive);
        File.Move(path, archive, overwrite: false);
    }

    private sealed record SourceInspection(bool HadTarget, TreeSnapshot Tree, TreeSnapshot Preserved, DeploymentInspection Inspection);
}
