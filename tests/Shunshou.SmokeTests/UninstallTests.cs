using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Shunshou.Core;

namespace Shunshou.SmokeTests;

public static class UninstallTests
{
    public static async Task RunAsync(string root)
    {
        var fixture = Path.Combine(Path.GetFullPath(root), "uninstall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var install = Path.Combine(fixture, "Programs", "Example Notes");
        var data = Path.Combine(fixture, "AppData");
        var backupRoot = Path.Combine(fixture, "Backups");
        var appData = Path.Combine(data, "Example Notes");
        Directory.CreateDirectory(Path.Combine(install, "empty"));
        Directory.CreateDirectory(appData);
        await File.WriteAllTextAsync(Path.Combine(install, "program.bin"), "uninstall fixture; never a real executable");
        await File.WriteAllTextAsync(Path.Combine(appData, "settings.json"), "{\"notes\":\"用户的资料\"}");
        var key = new RegistryIdentity(RegistryHive.CurrentUser, RegistryView.Registry64, WindowsUninstallPlatform.UninstallRoot + "\\ExampleNotes");
        var app = new InstalledApplication("fixture-app", "Example Notes", "1.0", "Fixture Publisher", 1024,
            null, install, "\"C:\\Example Notes\\uninstall.exe\" /remove", false, InstalledApplicationKind.Desktop, key);
        var environment = new UninstallEnvironment(backupRoot, [data], [Path.Combine(fixture, "Programs"), data], [Path.Combine(fixture, "Protected")]);
        var platform = new FixturePlatform { Applications = [app] };
        platform.Registry[key] = Snapshot("DisplayName", app.DisplayName);
        var service = new UninstallService(platform, environment);
        Require((await service.ListAsync(default)).Single() == app, "Discovery exposes all UI metadata and stable identity.");
        TestCommands();
        TestRegistryGuards();

        var scan = await service.ScanLeftoversAsync(app, true, default);
        Require(scan.Items.Count == 3 && scan.Items.Count(i => i.Kind == LeftoverKind.Directory) == 2,
            "Scan finds exact install/data directories plus registered uninstall key.");
        Require(File.Exists(Path.Combine(install, "program.bin")) && platform.Registry.ContainsKey(key), "Preview never deletes.");
        await Expect<InvalidOperationException>(() => service.CleanupSelectedAsync(scan, ["forged-id"], default));
        await Expect<InvalidOperationException>(() => service.CleanupSelectedAsync(scan with { ScanId = scan.ScanId }, [scan.Items[0].Id], default));
        await Expect<InvalidOperationException>(() => service.CleanupSelectedAsync(scan, [], default));
        await Expect<InvalidOperationException>(() => service.ScanLeftoversAsync(app with { InstallLocation = fixture }, true, default));
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            await Expect<OperationCanceledException>(() => service.CleanupSelectedAsync(scan, [scan.Items[0].Id], cancel.Token));
        }
        Require(Directory.Exists(install) && !Directory.Exists(backupRoot), "Invalid or cancelled requests make no backup or deletion.");

        await File.AppendAllTextAsync(Path.Combine(install, "program.bin"), "changed after preview");
        await Expect<IOException>(() => service.CleanupSelectedAsync(scan, scan.Items.Select(i => i.Id), default));
        Require(Directory.Exists(appData) && platform.Registry.ContainsKey(key), "A changed original prevents the whole cleanup before any deletion.");

        scan = await service.ScanLeftoversAsync(app, true, default);
        platform.Registry[key] = Snapshot("DisplayName", "changed registry");
        await Expect<IOException>(() => service.CleanupSelectedAsync(scan, scan.Items.Select(i => i.Id), default));
        platform.Registry[key] = Snapshot("DisplayName", app.DisplayName);
        scan = await service.ScanLeftoversAsync(app, true, default);
        var cleanup = await service.CleanupSelectedAsync(scan, scan.Items.Select(i => i.Id), default);
        Require(cleanup.RemovedCount == 3 && cleanup.Errors.Count == 0, "Selected exact leftovers are removed after backup verification: " + string.Join(";", cleanup.Errors));
        Require(!Directory.Exists(install) && !Directory.Exists(appData) && !platform.Registry.ContainsKey(key), "Cleanup removes selected files and key.");
        Require(service.ListBackupManifests().Single() == cleanup.BackupManifestPath, "Backup is discoverable for later recovery.");
        var newSession = new UninstallService(platform, environment);
        var restored = await newSession.RestoreBackupAsync(cleanup.BackupManifestPath, default);
        Require(restored.RestoredCount == 3 && restored.Errors.Count == 0 && Directory.Exists(Path.Combine(install, "empty")),
            "A fresh service restores files, empty directories and registry values.");
        Require(await File.ReadAllTextAsync(Path.Combine(appData, "settings.json")) == "{\"notes\":\"用户的资料\"}" &&
            platform.Registry[key].Values.Single().Data == app.DisplayName, "Restoration retains exact bytes and registry types.");
        await Expect<InvalidOperationException>(() => newSession.RestoreBackupAsync(cleanup.BackupManifestPath, default));

        scan = await service.ScanLeftoversAsync(app, false, default);
        var onlyInstall = scan.Items.Single(i => i.Kind == LeftoverKind.Directory && i.Path == install);
        cleanup = await service.CleanupSelectedAsync(scan, [onlyInstall.Id], default);
        Require(cleanup.RemovedCount == 1 && Directory.Exists(appData) && platform.Registry.ContainsKey(key), "Unselected content is retained.");
        Directory.CreateDirectory(install);
        await File.WriteAllTextAsync(Path.Combine(install, "program.bin"), "newly installed version");
        await Expect<IOException>(() => service.RestoreBackupAsync(cleanup.BackupManifestPath, default));
        Require(await File.ReadAllTextAsync(Path.Combine(install, "program.bin")) == "newly installed version", "Restore refuses to overwrite later user or installer changes.");

        var envelopeText = await File.ReadAllTextAsync(cleanup.BackupManifestPath);
        using var json = JsonDocument.Parse(envelopeText);
        var tampered = JsonSerializer.Serialize(new { Payload = json.RootElement.GetProperty("Payload").GetString(), Signature = new string('0', 64) });
        await File.WriteAllTextAsync(cleanup.BackupManifestPath, tampered);
        await Expect<InvalidDataException>(() => service.RestoreBackupAsync(cleanup.BackupManifestPath, default));
        await File.WriteAllTextAsync(cleanup.BackupManifestPath, envelopeText);
        await Expect<InvalidOperationException>(() => service.RestoreBackupAsync(Path.Combine(fixture, "foreign", "backup.json"), default));
        await TestProtectedPathsAsync(fixture, platform, environment, app);
        await TestUninstallOutcomesAsync(environment, app);
        await TestRegistryKindsAsync(fixture, environment, app);
        await TestConcurrentOwnershipAndStreamsAsync(fixture, environment, app);
        await TestReadOnlyInventoryAsync(root);
        Console.WriteLine("PASS: software inventory, EXE/MSI parsing, injected uninstall and Store outcomes, exact leftover previews, selected-only cleanup, backup/restore, changes/tampering/cancellation/permissions/shared-path protection. No real software was uninstalled.");
    }

    private static void TestCommands()
    {
        var command = UninstallService.ParseUninstallCommand("\"C:\\Program Files\\Notes\\uninstall.exe\" /remove \"two words\" \"x & y\"");
        Require(command.ExecutablePath == "C:\\Program Files\\Notes\\uninstall.exe" && command.Arguments.SequenceEqual(new[] { "/remove", "two words", "x & y" }), "Quoted arguments are preserved without shell interpolation.");
        command = UninstallService.ParseUninstallCommand("C:\\Program Files\\Notes\\uninstall.exe /remove");
        Require(command.ExecutablePath == "C:\\Program Files\\Notes\\uninstall.exe", "Unquoted registered paths never resolve C:\\Program.exe.");
        command = UninstallService.ParseUninstallCommand("MsiExec.exe /I{12345678-1234-1234-1234-123456789ABC}");
        Require(command.ExecutablePath == Path.Combine(Environment.SystemDirectory, "msiexec.exe") && command.Arguments.Single().StartsWith("/x{"), "MSI repair-style registration becomes native uninstall.");
        foreach (var invalid in new[] { "cmd.exe /c del a", "powershell -Command x", "https://example.com/file.exe", "\\\\server\\share\\uninstall.exe", "\"missing quote", "C:\\u.exe\ncalc.exe" })
            RequireThrows(() => UninstallService.ParseUninstallCommand(invalid), "Reject ambiguous, remote, script or malformed uninstall command.");
        RequireThrows(() => UninstallService.ParseUninstallCommand("C:\\Windows\\System32\\cmd.exe /c del a"), "Absolute script host paths are also rejected.");
        RequireThrows(() => UninstallService.ParseUninstallCommand("C:\\prefix.exe folder\\uninstall.exe /remove"),
            "An exe-named directory with a space cannot be truncated into another executable.");
        RequireThrows(() => UninstallService.ParseUninstallCommand("C:\\prefix.exe\\folder name\\uninstall.exe /remove"),
            "Ambiguous unquoted paths fail safely instead of executing a partial path.");
        command = UninstallService.ParseUninstallCommand("\"C:\\prefix.exe folder\\uninstall.exe\" /remove");
        Require(command.ExecutablePath == "C:\\prefix.exe folder\\uninstall.exe", "Quoted paths support exe-like directory names.");
    }

    private static void TestRegistryGuards()
    {
        foreach (var path in new[] { "SOFTWARE", "SOFTWARE\\Microsoft", "SOFTWARE\\Classes", "SOFTWARE\\Microsoft\\Windows", WindowsUninstallPlatform.UninstallRoot, "SOFTWARE\\..\\SAM", "SOFTWARE\\App\\Child" })
            RequireThrows(() => UninstallService.ValidateRegistryIdentity(new(RegistryHive.CurrentUser, RegistryView.Registry64, path)), "Registry roots/system/escaping paths are never cleanup targets.");
        RequireThrows(() => UninstallService.ValidateRegistryIdentity(new(RegistryHive.ClassesRoot, RegistryView.Registry64, "SOFTWARE\\Fixture")), "Other registry hives cannot be modified.");
        UninstallService.ValidateRegistryIdentity(new(RegistryHive.LocalMachine, RegistryView.Registry32, "SOFTWARE\\FixtureNotes"));
    }

    private static async Task TestProtectedPathsAsync(string fixture, FixturePlatform platform, UninstallEnvironment environment, InstalledApplication app)
    {
        var dangerous = new[] { Path.GetPathRoot(fixture)!, Path.Combine(fixture, "Programs"), Path.Combine(fixture, "Protected", "Example Notes") };
        foreach (var path in dangerous)
        {
            if (path != Path.GetPathRoot(fixture)) Directory.CreateDirectory(path);
            var selected = app with { InstallLocation = path };
            platform.Applications = [selected];
            var service = new UninstallService(platform, environment);
            await service.ListAsync(default);
            var scan = await service.ScanLeftoversAsync(selected, false, default);
            Require(!scan.Items.Any(i => i.Kind == LeftoverKind.Directory && i.Path.Equals(path, StringComparison.OrdinalIgnoreCase)), "Protected directory is excluded before hashing.");
        }
        var shared = Path.Combine(fixture, "Programs", "SharedInstall");
        Directory.CreateDirectory(Path.Combine(shared, "OtherApp"));
        platform.Applications = [app with { InstallLocation = shared }, app with { Id = "other", DisplayName = "Other App", InstallLocation = Path.Combine(shared, "OtherApp") }];
        var sharedService = new UninstallService(platform, environment);
        var selectedApps = await sharedService.ListAsync(default);
        var sharedScan = await sharedService.ScanLeftoversAsync(selectedApps[0], false, default);
        Require(!sharedScan.Items.Any(i => i.Path == shared), "Shared parent of another app is excluded.");
        platform.Applications = [app];
        if (!UninstallService.IsAdministrator)
        {
            var elevatedKey = new RegistryIdentity(RegistryHive.LocalMachine, RegistryView.Registry64, WindowsUninstallPlatform.UninstallRoot + "\\FixtureAdmin");
            var admin = app with { Id = "admin", RegistryIdentity = elevatedKey, RequiresElevation = true };
            platform.Applications = [admin]; platform.Registry[elevatedKey] = Snapshot("DisplayName", "Admin Fixture");
            var service = new UninstallService(platform, environment); await service.ListAsync(default);
            var scan = await service.ScanLeftoversAsync(admin, true, default);
            await Expect<UnauthorizedAccessException>(() => service.CleanupSelectedAsync(scan, scan.Items.Select(i => i.Id), default));
            Require(platform.Registry.ContainsKey(elevatedKey) && Directory.Exists(app.InstallLocation!), "Mixed selection needing UAC is rejected with zero deletion.");
        }
        platform.Applications = [app];
    }

    private static async Task TestUninstallOutcomesAsync(UninstallEnvironment environment, InstalledApplication app)
    {
        var platform = new FixturePlatform { Applications = [app], ExitCode = 0 };
        var service = new UninstallService(platform, environment);
        var result = await service.UninstallAsync(app, default);
        Require(result.StillInstalled && platform.RunCount == 1, "Exit zero alone cannot claim uninstall completed.");
        platform.OnRun = () => platform.Applications = [];
        result = await service.UninstallAsync(app, default);
        Require(!result.StillInstalled && platform.RunCount == 2, "Success is confirmed only after refreshed inventory.");
        platform.Applications = [app with { UninstallCommand = "C:\\changed.exe" }];
        await Expect<InvalidOperationException>(() => service.UninstallAsync(app, default));
        Require(platform.RunCount == 2, "Changed registered command is not executed.");
        using (var cancel = new CancellationTokenSource())
        { cancel.Cancel(); await Expect<OperationCanceledException>(() => service.UninstallAsync(app, cancel.Token)); }
        var store = app with { Id = "store:fixture", Kind = InstalledApplicationKind.Store, PackageFullName = "Fixture_1.0_x64__publisher", RegistryIdentity = null, UninstallCommand = null };
        platform.Applications = [store]; platform.OnStore = () => platform.Applications = [];
        result = await service.UninstallAsync(store, default);
        Require(!result.StillInstalled && platform.StoreCount == 1 && platform.RunCount == 2, "Store removal uses a dedicated package API, not shell commands.");
    }

    private static async Task TestRegistryKindsAsync(string fixture, UninstallEnvironment environment, InstalledApplication app)
    {
        var key = app.RegistryIdentity!;
        var values = new RegistrySnapshot([
            new("text", RegistryValueKind.String, "原文"), new("expand", RegistryValueKind.ExpandString, "%TEMP%\\test"),
            new("many", RegistryValueKind.MultiString, "[\"a\",\"b\"]"), new("dword", RegistryValueKind.DWord, "-1"),
            new("qword", RegistryValueKind.QWord, "9223372036854775807"), new("bytes", RegistryValueKind.Binary, "AAECAw==")],
            new Dictionary<string, RegistrySnapshot> { ["Nested"] = Snapshot("", "default value") });
        var platform = new FixturePlatform { Applications = [app] }; platform.Registry[key] = values;
        var service = new UninstallService(platform, environment with { BackupDirectory = Path.Combine(fixture, "KindsBackups"), ApplicationDataRoots = [] });
        await service.ListAsync(default);
        var scan = await service.ScanLeftoversAsync(app, true, default);
        var cleanup = await service.CleanupSelectedAsync(scan, scan.Items.Where(i => i.Kind == LeftoverKind.RegistryKey).Select(i => i.Id), default);
        var result = await service.RestoreBackupAsync(cleanup.BackupManifestPath, default);
        Require(result.RestoredCount == 1 && JsonSerializer.Serialize(values) == JsonSerializer.Serialize(platform.Registry[key]),
            "Registry backup round-trips string/expand/multi/dword/qword/binary/default/nested values without expanding data.");
    }

    private static async Task TestReadOnlyInventoryAsync(string root)
    {
        var applications = await new WindowsUninstallPlatform().ListAsync(default);
        Require(applications.All(a => !string.IsNullOrWhiteSpace(a.Id) && !string.IsNullOrWhiteSpace(a.DisplayName)) &&
            applications.Select(a => a.Id).Distinct().Count() == applications.Count, "Read-only Windows registry/Store discovery has stable nonempty distinct identities.");
        await File.WriteAllTextAsync(Path.Combine(root, "uninstall-readonly-summary.json"), JsonSerializer.Serialize(new {
            desktop = applications.Count(a => a.Kind == InstalledApplicationKind.Desktop),
            store = applications.Count(a => a.Kind == InstalledApplicationKind.Store),
            withInstallLocation = applications.Count(a => !string.IsNullOrWhiteSpace(a.InstallLocation)),
            mode = "read-only; no real uninstall/cleanup"
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task TestConcurrentOwnershipAndStreamsAsync(string fixture, UninstallEnvironment environment, InstalledApplication app)
    {
        var platform = new FixturePlatform { Applications = [app] };
        platform.Registry[app.RegistryIdentity!] = Snapshot("DisplayName", app.DisplayName);
        var service = new UninstallService(platform, environment);
        await service.ListAsync(default);
        var scan = await service.ScanLeftoversAsync(app, true, default);
        platform.Applications = [app, app with { Id = "new-owner", DisplayName = "New owner", InstallLocation = null }];
        await Expect<InvalidOperationException>(() => service.CleanupSelectedAsync(scan,
            scan.Items.Where(i => i.Kind == LeftoverKind.RegistryKey).Select(i => i.Id), default));
        Require(platform.Registry.ContainsKey(app.RegistryIdentity!), "New application ownership after preview prevents deleting shared registration.");
        platform.Applications = [app];
        scan = await service.ScanLeftoversAsync(app, true, default);
        var listCount = 0;
        platform.OnList = () => { if (++listCount >= 2) platform.Applications = [app, app with { Id = "owner-during-backup", DisplayName = "Another owner", InstallLocation = null }]; };
        await Expect<InvalidOperationException>(() => service.CleanupSelectedAsync(scan,
            scan.Items.Where(i => i.Kind == LeftoverKind.RegistryKey).Select(i => i.Id), default));
        Require(platform.Registry.ContainsKey(app.RegistryIdentity!), "Ownership is refreshed again after all backups before first deletion.");

        var adsDir = Path.Combine(fixture, "Programs", "StreamFixture");
        Directory.CreateDirectory(adsDir);
        var file = Path.Combine(adsDir, "stream.txt");
        await File.WriteAllTextAsync(file, "main stream");
        await File.WriteAllTextAsync(file + ":extra", "extra stream must not be lost");
        var streamApp = app with { Id = "stream-app", DisplayName = "StreamFixture", InstallLocation = adsDir };
        platform.OnList = null; platform.Applications = [streamApp];
        await service.ListAsync(default);
        scan = await service.ScanLeftoversAsync(streamApp, false, default);
        Require(scan.Items.All(i => i.Path != adsDir) && await File.ReadAllTextAsync(file + ":extra") == "extra stream must not be lost",
            "Files with alternate data streams are retained instead of receiving an incomplete backup.");

        await TestJunctionGuardsAsync(fixture, platform, service, app);
    }

    private static async Task TestJunctionGuardsAsync(string fixture, FixturePlatform platform, UninstallService service, InstalledApplication app)
    {
        // Every path here is generated under the unique smoke-test fixture. Nothing outside that
        // directory participates in creation, scanning or attempted cleanup.
        var junctionFixture = Path.Combine(fixture, "junction-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(junctionFixture, "TargetOutsideInstall");
        Directory.CreateDirectory(target);
        var targetFile = Path.Combine(target, "must-retain.bin");
        await File.WriteAllBytesAsync(targetFile, Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
        var originalHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(targetFile)));

        var rootLink = Path.Combine(junctionFixture, "RootJunction");
        await CreateJunctionFixtureAsync(junctionFixture, rootLink, target);
        var rootApp = app with { Id = "root-junction-app", DisplayName = "RootJunction", InstallLocation = rootLink };
        platform.Applications = [rootApp]; await service.ListAsync(default);
        var scan = await service.ScanLeftoversAsync(rootApp, false, default);
        Require(scan.Items.All(i => i.Path != rootLink) && scan.SkippedReasons.Count > 0,
            "An installation root that is a real directory junction is rejected.");

        var parent = Path.Combine(junctionFixture, "ChildJunctionInstall");
        Directory.CreateDirectory(parent);
        var originalFile = Path.Combine(parent, "original.bin");
        await File.WriteAllTextAsync(originalFile, "installation fixture must remain unchanged");
        await CreateJunctionFixtureAsync(junctionFixture, Path.Combine(parent, "LinkedChild"), target);
        var childApp = app with { Id = "child-junction-app", DisplayName = "ChildJunctionInstall", InstallLocation = parent };
        platform.Applications = [childApp]; await service.ListAsync(default);
        scan = await service.ScanLeftoversAsync(childApp, false, default);
        Require(scan.Items.All(i => i.Path != parent) && scan.SkippedReasons.Count > 0,
            "An installation directory containing a child junction is rejected as a whole.");
        Require(await File.ReadAllTextAsync(originalFile) == "installation fixture must remain unchanged",
            "Rejecting a child junction leaves the installation's regular files intact.");

        var changed = Path.Combine(junctionFixture, "ChangedAfterPreview");
        Directory.CreateDirectory(changed);
        var unchangedFile = Path.Combine(changed, "original.bin");
        await File.WriteAllTextAsync(unchangedFile, "previewed original");
        var changedApp = app with { Id = "new-junction-app", DisplayName = "ChangedAfterPreview", InstallLocation = changed };
        platform.Applications = [changedApp]; await service.ListAsync(default);
        scan = await service.ScanLeftoversAsync(changedApp, false, default);
        var candidate = scan.Items.Single(i => i.Kind == LeftoverKind.Directory && i.Path == changed);
        await CreateJunctionFixtureAsync(junctionFixture, Path.Combine(changed, "AddedAfterPreview"), target);
        await Expect<IOException>(() => service.CleanupSelectedAsync(scan, [candidate.Id], default));
        Require(await File.ReadAllTextAsync(unchangedFile) == "previewed original",
            "A junction added after preview blocks cleanup before the first original file is deleted.");
        var finalHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(targetFile)));
        Require(finalHash == originalHash && Directory.Exists(target),
            "Root, child and post-preview junction handling never changes target bytes or deletes the target directory.");
        Console.WriteLine("PASS: real directory junction fixtures guard installation roots, children and post-preview changes; target hash unchanged.");
    }

    private static async Task CreateJunctionFixtureAsync(string fixtureRoot, string link, string target)
    {
        var root = Path.GetFullPath(fixtureRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        link = Path.GetFullPath(link);
        target = Path.GetFullPath(target);
        Require(link.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                target.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                !Directory.Exists(link) && !File.Exists(link) && Directory.Exists(target),
            "Junction setup only accepts new links and generated targets strictly inside this test fixture.");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        // New-Item does not support LiteralPath in Windows PowerShell 5.1. Single-quoted fixture
        // paths passed through -Path contain no wildcard characters and no executable interpolation.
        Require(link.IndexOfAny(['[', ']', '*', '?']) < 0 && target.IndexOfAny(['[', ']', '*', '?']) < 0,
            "Generated junction fixture paths must not contain PowerShell wildcard characters.");
        var script = $"New-Item -Path {Quote(link)} -ItemType Junction -Value {Quote(target)} -ErrorAction Stop | Out-Null";
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new Exception("Could not create isolated junction fixture.");
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Require(process.ExitCode == 0, "Isolated junction fixture creation failed: " + await errors);
        await output;
        Require((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0,
            "Created fixture must be an actual directory reparse point.");
    }

    private static RegistrySnapshot Snapshot(string name, string value) => new([new(name, RegistryValueKind.String, value)], new Dictionary<string, RegistrySnapshot>());
    private static void Require(bool success, string message) { if (!success) throw new Exception(message); }
    private static void RequireThrows(Action action, string message) { try { action(); } catch { return; } throw new Exception(message); }
    private static async Task Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }

    public sealed class FixturePlatform : IUninstallPlatform
    {
        public IReadOnlyList<InstalledApplication> Applications { get; set; } = [];
        public Dictionary<RegistryIdentity, RegistrySnapshot> Registry { get; } = [];
        public int RunCount { get; private set; }
        public int StoreCount { get; private set; }
        public int? ExitCode { get; set; }
        public Action? OnRun { get; set; }
        public Action? OnStore { get; set; }
        public Action? OnList { get; set; }
        public Task<IReadOnlyList<InstalledApplication>> ListAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); OnList?.Invoke(); return Task.FromResult(Applications); }
        public Task<int?> RunUninstallerAsync(UninstallCommand command, bool elevated, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); RunCount++; OnRun?.Invoke(); return Task.FromResult(ExitCode); }
        public Task RemoveStorePackageAsync(string packageFullName, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); StoreCount++; OnStore?.Invoke(); return Task.CompletedTask; }
        public RegistrySnapshot? ReadRegistry(RegistryIdentity identity) => Registry.GetValueOrDefault(identity);
        public void DeleteRegistry(RegistryIdentity identity) { if (!Registry.Remove(identity)) throw new IOException("Missing fixture key."); }
        public void RestoreRegistry(RegistryIdentity identity, RegistrySnapshot snapshot) => Registry.Add(identity, snapshot);
    }
}
