using Microsoft.UI.Xaml.Controls;
using Shunshou.Core;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;

namespace Shunshou.App;

public sealed partial class UninstallerWorkspace
{
    /// <summary>Exercises UI actions against generated files and a fake platform; never executes a program or uses the machine registry.</summary>
    internal async Task VerifyFixtureAsync(string directory)
    {
        string reportDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(reportDirectory);
        directory = Path.Combine(reportDirectory, "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string install = Path.Combine(directory, "installed", "StudyPad");
        string data = Path.Combine(directory, "appdata", "StudyPad");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(data);
        await File.WriteAllTextAsync(Path.Combine(install, "fixture.txt"), "Generated application fixture; no executable.");
        await File.WriteAllTextAsync(Path.Combine(data, "preferences.txt"), "Generated preferences fixture.");
        string uninstaller = Path.Combine(install, "fixture-uninstaller.exe");
        await File.WriteAllTextAsync(uninstaller, "This is inert test data, not a Windows executable.");
        var hashes = Directory.GetFiles(install).Concat(Directory.GetFiles(data))
            .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        var platform = new UiUninstallPlatform([
            new("fixture:study", "StudyPad 学习笔记", "2.6.1", "顺手实验室", 184_000_000, null, install, "\"" + uninstaller + "\"", false, InstalledApplicationKind.Desktop),
            new("fixture:archive", "ArchiveDesk 文件管理", "4.2.0", "Workspace Tools", 72_000_000, null, null, null, false, InstalledApplicationKind.Desktop),
            new("fixture:store", "Screen Notes", "1.8.0", "Study Tools", 0, null, null, null, false, InstalledApplicationKind.Store, PackageFullName: "fixture.store")
        ]);
        _service = new UninstallService(platform, new UninstallEnvironment(Path.Combine(directory, "backups"),
            [Path.Combine(directory, "appdata")], [directory], []));
        _administratorOverride = true;
        _allApps = [];
        _snapshot = null;
        _loaded = false;
        _loadTask = null;
        Task initial = LoadAsync();
        Task selection = LoadAsync("fixture:archive");
        await Task.WhenAll(initial, selection);
        if (_snapshot?.Id != "fixture:archive" || _allApps.Count != 3 || _canUninstall)
            throw new InvalidOperationException("Software fixture loading must honor a concurrent selection request and flag the broken uninstaller.");
        await LoadAsync("fixture:study");
        if (_snapshot?.Id != "fixture:study" || !_canUninstall)
            throw new InvalidOperationException("The generated executable path enables the UI action, but the fake platform must intercept every execution.");
        await ScanAsync(_snapshot);
        if (_scan == null || _leftovers.Count != 2 || _leftovers.Any(row => row.IsSelected) || CleanupButton.IsEnabled)
            throw new InvalidOperationException("Software fixture must show two unchecked candidates and keep cleanup disabled by default.");
        _leftovers[0].IsSelected = true;
        if (!CleanupButton.IsEnabled) throw new InvalidOperationException("Explicit selection must enable the cleanup confirmation action.");
        _leftovers[0].IsSelected = false;
        if (CleanupButton.IsEnabled || !File.Exists(Path.Combine(install, "fixture.txt")) || platform.DestructiveCalls != 0)
            throw new InvalidOperationException("UI selection and scanning must never modify originals or call an uninstaller.");
        SearchBox.Text = "ArchiveDesk";
        await WaitForFixtureConditionAsync(() => _visibleApps.Count == 1 && _visibleApps[0].Name.StartsWith("ArchiveDesk", StringComparison.Ordinal),
            "Installed application name search must filter the visible list.");
        SearchBox.Text = "";
        await WaitForFixtureConditionAsync(() => _visibleApps.Count == 3, "Clearing installed application search must restore its list.");
        await LoadAsync("fixture:study");
        int confirmationCount = 0;
        try
        {
            ConfirmOverride = (_, _, _) => { confirmationCount++; return Task.FromResult(false); };
            await UninstallSelectedAsync();
            if (confirmationCount != 1 || platform.DestructiveCalls != 0 || platform.Applications.All(app => app.Id != "fixture:study"))
                throw new InvalidOperationException("Cancelling the actual uninstall UI action must not call the fake executor or remove its entry.");
            RequireOriginals(hashes);
            ConfirmOverride = (_, _, _) => { confirmationCount++; return Task.FromResult(true); };
            await UninstallSelectedAsync();
            if (confirmationCount != 2 || platform.DestructiveCalls != 1 || platform.Applications.Any(app => app.Id == "fixture:study") || _snapshot?.Id != "fixture:study" || !_uninstallFinished)
                throw new InvalidOperationException("Confirming uninstall must call only the fake executor, refresh its list and retain the application snapshot.");
            RequireOriginals(hashes);
            await ScanAsync(_snapshot);
            if (_scan == null || _leftovers.Count != 2 || _leftovers.Any(row => row.IsSelected))
                throw new InvalidOperationException("Uninstalled application leftovers must be available through the retained snapshot and start unchecked.");

            await CleanupSelectedAsync();
            if (_service.ListBackupManifests().Count != 0 || confirmationCount != 2)
                throw new InvalidOperationException("Cleanup with no selected candidates must stop before confirmation or backup creation.");
            foreach (var row in _leftovers) row.IsSelected = true;
            ConfirmOverride = (_, _, _) => { confirmationCount++; return Task.FromResult(false); };
            await CleanupSelectedAsync();
            if (confirmationCount != 3 || _service.ListBackupManifests().Count != 0)
                throw new InvalidOperationException("Cancelling actual cleanup must not create a backup or change generated originals.");
            RequireOriginals(hashes);
            ConfirmOverride = (_, _, _) => { confirmationCount++; return Task.FromResult(true); };
            await CleanupSelectedAsync();
            var manifests = _service.ListBackupManifests();
            if (confirmationCount != 4 || manifests.Count != 1 || Directory.Exists(install) || Directory.Exists(data) || _scan != null || _leftovers.Count != 0)
                throw new InvalidOperationException("Confirmed cleanup must back up and remove only the two generated candidate directories and invalidate the UI scan.");
            await WithBusyAsync("正在验证备份恢复…", ct => RestoreManifestAsync(manifests[0], ct));
            RequireOriginals(hashes);
            if (platform.DestructiveCalls != 1 || platform.RegistryWrites != 0)
                throw new InvalidOperationException("UI fixture actions must never execute a genuine program or mutate the machine registry.");
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, "ui-action-verification.json"), JsonSerializer.Serialize(new
            {
                uninstallCancelled = true, uninstallConfirmedThroughFakePlatform = true,
                cleanupRequiresSelection = true, cleanupCancelled = true,
                cleanupCreatedSignedBackup = true, restoreRestoredOriginalHashes = true,
                confirmationCount, fakeExecutorCalls = platform.DestructiveCalls,
                registryWrites = platform.RegistryWrites, manifest = manifests[0]
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { ConfirmOverride = null; }
        // Return the initial list for the screenshot while keeping the same generated candidates.
        platform.RestoreToList();
        _allApps = await _service.ListAsync(default);
        _snapshot = null;
        ApplyFilter("fixture:study");
        await ScanAsync(_snapshot!);
        ShowStatus("检查完成", "已找到 2 项关联条目，请核对内容后勾选。", InfoBarSeverity.Informational);
    }

    private static void RequireOriginals(IReadOnlyDictionary<string, string> hashes)
    {
        foreach (var (path, hash) in hashes)
            if (!File.Exists(path) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) != hash)
                throw new InvalidOperationException("UI fixture cancellation/restoration failed to preserve generated file bytes: " + path);
    }

    private async Task WaitForFixtureConditionAsync(Func<bool> condition, string failure)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(25);
        if (!condition()) throw new InvalidOperationException(failure +
            $" Query='{SearchBox.Text}', Busy={_busy}, Visible={_visibleApps.Count}, Names=[{string.Join(", ", _visibleApps.Select(row => row.Name))}]");
    }

    private sealed class UiUninstallPlatform(IReadOnlyList<InstalledApplication> applications) : IUninstallPlatform
    {
        private readonly IReadOnlyList<InstalledApplication> _original = applications;
        private IReadOnlyList<InstalledApplication> _applications = applications;
        public int DestructiveCalls { get; private set; }
        public int RegistryWrites { get; private set; }
        public IReadOnlyList<InstalledApplication> Applications => _applications;
        public void RemoveFromList(string id) => _applications = _applications.Where(app => app.Id != id).ToArray();
        public void RestoreToList() => _applications = _original;
        public async Task<IReadOnlyList<InstalledApplication>> ListAsync(CancellationToken ct)
        {
            await Task.Delay(30, ct);
            return _applications;
        }
        public Task<int?> RunUninstallerAsync(UninstallCommand command, bool elevated, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            DestructiveCalls++;
            var matching = _applications.Single(app => app.UninstallCommand != null && UninstallService.ParseUninstallCommand(app.UninstallCommand).ExecutablePath == command.ExecutablePath);
            RemoveFromList(matching.Id);
            return Task.FromResult<int?>(0);
        }
        public Task RemoveStorePackageAsync(string packageFullName, CancellationToken ct)
        {
            DestructiveCalls++;
            throw new InvalidOperationException("UI fixtures cannot remove Windows applications.");
        }
        public RegistrySnapshot? ReadRegistry(RegistryIdentity identity) => null;
        public void DeleteRegistry(RegistryIdentity identity)
        {
            RegistryWrites++;
            throw new InvalidOperationException("UI fixtures cannot delete registry keys.");
        }
        public void RestoreRegistry(RegistryIdentity identity, RegistrySnapshot snapshot)
        {
            RegistryWrites++;
            throw new InvalidOperationException("UI fixtures cannot restore registry keys.");
        }
    }
}
