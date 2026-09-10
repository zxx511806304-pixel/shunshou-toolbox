using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Shunshou.Core;

/// <summary>Explicit, preview-first software maintenance. Scanning never mutates a file or key.</summary>
public sealed partial class UninstallService
{
    private readonly IUninstallPlatform _platform;
    private readonly UninstallEnvironment _environment;
    private readonly Dictionary<string, InstalledApplication> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScanState> _scans = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] GenericNames = ["Microsoft", "Windows", "Common Files", "Shared", "Common", "Programs", "Apps", "Applications", "Software", "Packages", "Temp", "Data", "Cache", "Google", "Adobe", "Tencent", "Intel", "AMD", "NVIDIA", "Classes", "Clients", "Policies", "RegisteredApplications", "Wow6432Node"];
    public UninstallService(IUninstallPlatform? platform = null, UninstallEnvironment? environment = null)
    {
        _platform = platform ?? new WindowsUninstallPlatform();
        _environment = environment ?? UninstallEnvironment.Default;
    }
    public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static UninstallCommand ParseUninstallCommand(string commandLine) => WindowsUninstallPlatform.ParseUninstallCommand(commandLine);

    public async Task<IReadOnlyList<InstalledApplication>> ListAsync(CancellationToken ct)
    {
        var applications = await _platform.ListAsync(ct).ConfigureAwait(false);
        foreach (var app in applications) _known[app.Id] = app;
        return applications;
    }

    public async Task<UninstallResult> UninstallAsync(InstalledApplication application, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Refresh identity and registered command immediately before execution, not just the display row.
        var current = (await ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(a => a.Id == application.Id);
        if (current is null) return new(null, false, "该软件已不在已安装列表中，可以检查残留。");
        if (current != application) throw new InvalidOperationException("软件信息发生变化，请刷新列表后重新选择。");
        int? exitCode = null;
        if (current.Kind == InstalledApplicationKind.Store)
            await _platform.RemoveStorePackageAsync(current.PackageFullName ?? throw new InvalidDataException("应用包标识缺失。"), ct).ConfigureAwait(false);
        else
            exitCode = await _platform.RunUninstallerAsync(ParseUninstallCommand(current.UninstallCommand ?? ""),
                current.RequiresElevation && !IsAdministrator, ct).ConfigureAwait(false);
        var stillInstalled = (await ListAsync(ct).ConfigureAwait(false)).Any(a => a.Id == current.Id);
        var message = stillInstalled
            ? "卸载程序已结束，但软件仍在已安装列表中。它可能尚未完成卸载；请确认卸载窗口并刷新。"
            : "软件已从已安装列表移除，可以检查残留。";
        if (exitCode is not null && exitCode != 0 && exitCode != 3010) message += $" 卸载程序退出码：{exitCode}。";
        if (exitCode == 3010) message += " 卸载程序提示需要重新启动 Windows。";
        return new(exitCode, stillInstalled, message);
    }

    public async Task<LeftoverScan> ScanLeftoversAsync(InstalledApplication application, bool includeUninstallRegistration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_known.TryGetValue(application.Id, out var known) || known != application)
            throw new InvalidOperationException("请先从已安装软件列表选择软件，再扫描残留。");
        if (application.Kind == InstalledApplicationKind.Store)
            return new(Guid.NewGuid().ToString("N"), application, [], ["Windows 应用的数据由系统卸载程序管理。"]);
        var currentApps = await ListAsync(ct).ConfigureAwait(false);
        var candidates = new List<Candidate>();
        var skipped = new List<string>();
        var paths = new Dictionary<string, (LeftoverConfidence Confidence, string Reason)>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(application.InstallLocation))
            paths[application.InstallLocation] = (LeftoverConfidence.ExactLocation, "软件登记的专属安装目录");
        var names = new[] { application.DisplayName, SafeLeaf(application.InstallLocation) }
            .Where(IsSpecificName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var root in _environment.ApplicationDataRoots.Where(p => !string.IsNullOrWhiteSpace(p)))
        foreach (var name in names)
            paths.TryAdd(Path.Combine(root, name!), (LeftoverConfidence.ExactName, "与软件名称或安装目录完全同名的数据目录，可能包含设置和个人数据"));
        foreach (var (path, evidence) in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(path)) continue;
            try
            {
                ValidateDirectory(path, application, currentApps);
                var tree = await CaptureTreeAsync(path, ct).ConfigureAwait(false);
                var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
                if (candidates.Any(c => c.Item.Kind == LeftoverKind.Directory && IsInside(full, c.Item.Path))) continue;
                candidates.RemoveAll(c => c.Item.Kind == LeftoverKind.Directory && IsInside(c.Item.Path, full));
                candidates.Add(new(new(Guid.NewGuid().ToString("N"), LeftoverKind.Directory, full,
                    tree.Files.Sum(f => f.Length), evidence.Confidence, evidence.Reason, PathRequiresElevation(full)), tree, null, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { skipped.Add($"{path}：{ex.Message}"); }
        }
        var keys = new List<(RegistryIdentity Identity, LeftoverConfidence Confidence, string Reason)>();
        if (includeUninstallRegistration && application.RegistryIdentity is not null)
            keys.Add((application.RegistryIdentity, LeftoverConfidence.ExactRegistration, "该软件的卸载列表记录；移除记录本身不代表软件已卸载"));
        foreach (var name in names)
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in hive == RegistryHive.CurrentUser ? new[] { Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32 }
                     : Environment.Is64BitOperatingSystem ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : [RegistryView.Registry32])
            keys.Add((new(hive, view, "SOFTWARE\\" + name), LeftoverConfidence.ExactName,
                "与软件名称或安装目录完全同名的专属设置项"));
        foreach (var (identity, confidence, reason) in keys.DistinctBy(k => k.Identity))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                ValidateRegistryIdentity(identity);
                ValidateRegistryOwnership(identity, application, currentApps, confidence);
                var snapshot = _platform.ReadRegistry(identity);
                if (snapshot is null) continue;
                candidates.Add(new(new(Guid.NewGuid().ToString("N"), LeftoverKind.RegistryKey, identity.ToString(),
                    Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(snapshot)), confidence, reason,
                    identity.Hive == RegistryHive.LocalMachine), null, identity, snapshot));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { skipped.Add($"{identity}：{ex.Message}"); }
        }
        var scan = new LeftoverScan(Guid.NewGuid().ToString("N"), application, candidates.Select(c => c.Item).ToArray(), skipped);
        _scans[scan.ScanId] = new(scan, candidates);
        // Keep only a small number of previews; old previews must be rescanned rather than silently reused.
        if (_scans.Count > 12) _scans.Remove(_scans.Keys.First());
        return scan;
    }

    public async Task<CleanupResult> CleanupSelectedAsync(LeftoverScan scan, IEnumerable<string> selectedIds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_scans.TryGetValue(scan.ScanId, out var state) || !ReferenceEquals(scan, state.Scan))
            throw new InvalidOperationException("此预览已失效，请重新扫描。");
        var ids = selectedIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) throw new InvalidOperationException("请勾选需要清理的条目。");
        if (ids.Any(id => state.Candidates.All(c => c.Item.Id != id))) throw new InvalidOperationException("选择中包含未扫描的条目。");
        var chosen = state.Candidates.Where(c => ids.Contains(c.Item.Id, StringComparer.Ordinal)).ToArray();
        if (chosen.Any(c => c.Item.RequiresElevation) && !IsAdministrator)
            throw new UnauthorizedAccessException("所选条目需要管理员权限。请以管理员身份重新打开软件后扫描并清理；当前未删除任何条目。");
        var currentApps = await ListAsync(ct).ConfigureAwait(false);
        foreach (var candidate in chosen) await ValidateCandidateAsync(candidate, scan.Application, currentApps, ct).ConfigureAwait(false);
        var directory = Path.Combine(Path.GetFullPath(_environment.BackupDirectory), DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        foreach (var c in chosen.Where(c => c.Item.Kind == LeftoverKind.Directory))
            if (IsInside(directory, c.Item.Path)) throw new InvalidOperationException("备份目录不能位于待清理目录内。");
        EnsureNoLinks(_environment.BackupDirectory);
        Directory.CreateDirectory(directory);
        EnsureNoLinks(directory);
        var manifestPath = Path.Combine(directory, "backup.json");
        var journal = new CleanupJournal(1, scan.Application, DateTimeOffset.UtcNow, "backing-up", []);
        await SaveJournalAsync(manifestPath, journal).ConfigureAwait(false);
        // Complete and verify ALL backups before the first mutation of an original.
        foreach (var candidate in chosen)
        {
            ct.ThrowIfCancellationRequested();
            var relative = candidate.Item.Id;
            if (candidate.Tree is not null)
            {
                await CopyTreeAsync(candidate.Item.Path, Path.Combine(directory, relative), candidate.Tree, ct).ConfigureAwait(false);
                var actual = await CaptureTreeAsync(Path.Combine(directory, relative), ct).ConfigureAwait(false);
                if (!TreesEqual(candidate.Tree, actual)) throw new IOException("文件备份校验失败，未开始清理。");
            }
            journal.Items.Add(new(candidate.Item, candidate.Tree, candidate.RegistryIdentity, candidate.Registry,
                relative, "backed-up", null));
            await SaveJournalAsync(manifestPath, journal).ConfigureAwait(false);
        }
        currentApps = await ListAsync(ct).ConfigureAwait(false);
        foreach (var candidate in chosen) await ValidateCandidateAsync(candidate, scan.Application, currentApps, ct).ConfigureAwait(false);
        journal.State = "cleaning";
        await SaveJournalAsync(manifestPath, journal).ConfigureAwait(false);
        var errors = new List<string>();
        foreach (var entry in journal.Items)
        {
            if (ct.IsCancellationRequested) { errors.Add("已取消，尚未清理的条目保持原样。"); break; }
            try
            {
                var candidate = chosen.Single(c => c.Item.Id == entry.Item.Id);
                currentApps = await ListAsync(ct).ConfigureAwait(false);
                await ValidateCandidateAsync(candidate, scan.Application, currentApps, ct).ConfigureAwait(false);
                entry.State = "removing";
                await SaveJournalAsync(manifestPath, journal).ConfigureAwait(false);
                if (entry.Tree is not null) await RemoveTreeAsync(entry.Item.Path, entry.Tree, ct).ConfigureAwait(false);
                else _platform.DeleteRegistry(entry.RegistryIdentity!);
                entry.State = "removed";
            }
            catch (Exception ex)
            {
                entry.State = "error"; entry.Error = ex.Message;
                errors.Add($"{entry.Item.Path}：{ex.Message}");
            }
            await SaveJournalAsync(manifestPath, journal).ConfigureAwait(false);
        }
        journal.State = errors.Count == 0 ? "completed" : "partial";
        await SaveJournalAsync(manifestPath, journal).ConfigureAwait(false);
        _scans.Remove(scan.ScanId);
        return new(manifestPath, journal.Items.Count(i => i.State == "removed"), errors);
    }

    private async Task ValidateCandidateAsync(Candidate candidate, InstalledApplication app,
        IReadOnlyList<InstalledApplication> currentApps, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (candidate.Tree is not null)
        {
            ValidateDirectory(candidate.Item.Path, app, currentApps);
            if (!TreesEqual(candidate.Tree, await CaptureTreeAsync(candidate.Item.Path, ct).ConfigureAwait(false)))
                throw new IOException("文件在扫描后发生变化，请重新扫描；未清理此目录。");
        }
        else
        {
            ValidateRegistryIdentity(candidate.RegistryIdentity!);
            ValidateRegistryOwnership(candidate.RegistryIdentity!, app, currentApps, candidate.Item.Confidence);
            var current = _platform.ReadRegistry(candidate.RegistryIdentity!);
            if (current is null || RegistryFingerprint(current) != RegistryFingerprint(candidate.Registry!))
                throw new IOException("注册表项在扫描后发生变化，请重新扫描。");
        }
    }

    private void ValidateDirectory(string path, InstalledApplication app, IReadOnlyList<InstalledApplication> currentApps)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (!Path.IsPathFullyQualified(path) || full.StartsWith("\\\\", StringComparison.Ordinal) ||
            string.Equals(full, Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            !IsSpecificName(Path.GetFileName(full))) throw new InvalidOperationException("受保护的系统或共享目录。");
        foreach (var protectedRoot in _environment.ProtectedDirectories.Where(p => !string.IsNullOrWhiteSpace(p)))
            if (IsInside(protectedRoot, full)) throw new InvalidOperationException("不能清理系统、用户或共享目录的根目录。");
        foreach (var protectedTree in _environment.ProtectedTrees.Where(p => !string.IsNullOrWhiteSpace(p)))
            if (IsInside(full, protectedTree) || IsInside(protectedTree, full)) throw new InvalidOperationException("跳过受保护的文件或工具箱所在目录。");
        if (IsInside(_environment.BackupDirectory, full) || IsInside(full, _environment.BackupDirectory))
            throw new InvalidOperationException("不能清理卸载备份目录。");
        foreach (var other in currentApps.Where(a => a.Id != app.Id && !string.IsNullOrWhiteSpace(a.InstallLocation)))
            if (IsInside(other.InstallLocation!, full) || IsInside(full, other.InstallLocation!))
                throw new InvalidOperationException("该目录还与其他软件的安装位置关联。");
        if (currentApps.Any(a => a.Id != app.Id && (string.Equals(a.DisplayName, Path.GetFileName(full), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(SafeLeaf(a.InstallLocation), Path.GetFileName(full), StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("同名目录可能由其他软件共用。");
        EnsureNoLinks(full);
    }

    private static void ValidateRegistryOwnership(RegistryIdentity identity, InstalledApplication app,
        IReadOnlyList<InstalledApplication> currentApps, LeftoverConfidence confidence)
    {
        if (currentApps.Any(a => a.Id != app.Id && a.RegistryIdentity is not null &&
            a.RegistryIdentity.Hive == identity.Hive &&
            (a.RegistryIdentity.View == identity.View || identity.Hive == RegistryHive.CurrentUser) &&
            (RegistryInside(a.RegistryIdentity.SubKey, identity.SubKey) || RegistryInside(identity.SubKey, a.RegistryIdentity.SubKey))))
            throw new InvalidOperationException("该注册表项还被其他软件使用。");
        if (confidence == LeftoverConfidence.ExactName && currentApps.Any(a => a.Id != app.Id &&
            (string.Equals(a.DisplayName, identity.SubKey.Split('\\').Last(), StringComparison.OrdinalIgnoreCase) ||
             string.Equals(SafeLeaf(a.InstallLocation), identity.SubKey.Split('\\').Last(), StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("同名设置项可能由其他软件共用。");
    }

    public static bool PathRequiresElevation(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows) }
            .Where(p => !string.IsNullOrWhiteSpace(p)).Any(root => IsInside(path, root));
    }
    public static void ValidateRegistryIdentity(RegistryIdentity identity)
    {
        if (identity.Hive is not (RegistryHive.CurrentUser or RegistryHive.LocalMachine) ||
            identity.View is not (RegistryView.Registry32 or RegistryView.Registry64) ||
            identity.SubKey.IndexOfAny(['/', '\0']) >= 0)
            throw new InvalidOperationException("注册表范围无效。");
        var parts = identity.SubKey.Split('\\');
        if (parts.Any(p => string.IsNullOrWhiteSpace(p) || p is "." or "..")) throw new InvalidOperationException("注册表路径无效。");
        var isUninstallEntry = identity.SubKey.StartsWith(WindowsUninstallPlatform.UninstallRoot + "\\", StringComparison.OrdinalIgnoreCase)
            && parts.Length == WindowsUninstallPlatform.UninstallRoot.Split('\\').Length + 1;
        var isSpecificSoftware = parts.Length == 2 && parts[0].Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase) && IsSpecificName(parts[1]);
        if (!isUninstallEntry && !isSpecificSoftware) throw new InvalidOperationException("只能清理准确的软件专属项，不能操作系统或共享注册表根。");
    }
    internal static bool IsInside(string path, string root)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return full.Equals(parent, StringComparison.OrdinalIgnoreCase) || full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static bool RegistryInside(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    private static string? SafeLeaf(string? path) { try { return string.IsNullOrWhiteSpace(path) ? null : Path.GetFileName(path.TrimEnd('\\', '/')); } catch { return null; } }
    private static bool IsSpecificName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length >= 3 &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !GenericNames.Contains(name, StringComparer.OrdinalIgnoreCase);
    private static string RegistryFingerprint(RegistrySnapshot snapshot) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot))));
    private sealed record Candidate(LeftoverItem Item, DirectorySnapshot? Tree, RegistryIdentity? RegistryIdentity, RegistrySnapshot? Registry);
    private sealed record ScanState(LeftoverScan Scan, IReadOnlyList<Candidate> Candidates);
    public sealed record FileSnapshot(string RelativePath, long Length, string Sha256, long LastWriteUtcTicks, FileAttributes Attributes);
    public sealed record DirectorySnapshot(IReadOnlyList<string> Directories, IReadOnlyList<FileSnapshot> Files);
    public sealed record CleanupJournal(int Version, InstalledApplication Application, DateTimeOffset CreatedAt, string InitialState, List<BackupEntry> Items)
    { public string State { get; set; } = InitialState; }
    public sealed record BackupEntry(LeftoverItem Item, DirectorySnapshot? Tree, RegistryIdentity? RegistryIdentity,
        RegistrySnapshot? Registry, string BackupRelativePath, string InitialState, string? InitialError)
    { public string State { get; set; } = InitialState; public string? Error { get; set; } = InitialError; }
}
