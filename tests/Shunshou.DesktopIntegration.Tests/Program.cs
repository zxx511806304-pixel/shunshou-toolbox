using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shunshou.DesktopIntegration;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--wait-probe")
        {
            Console.WriteLine("READY");
            Console.ReadLine();
            return 0;
        }
        if (args.Length == 4 && args[0] == "--lease-probe")
        {
            using var lease = args[3] switch
            {
                "reader" => UpdateCoordination.AcquireApplicationLease(args[1], args[2]),
                "existing-reader" => UpdateCoordination.AcquireExistingApplicationLease(args[1], args[2]),
                _ => UpdateCoordination.AcquireUpdateLease(args[1], args[2])
            };
            return lease is null ? 77 : 0;
        }
        var root = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/desktop-integration");
        root = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var cases = new List<string>();
        try
        {
            ShortcutLifecycle(root, cases);
            LegacyPackageMigration(root, cases);
            CleanLayoutMigration(root, cases);
            PreserveUnrelated(root, cases);
            ExplicitOptOut(root, cases);
            RejectInvalidPackage(root, cases);
            RegistrationFailureNonfatal(root, cases);
            StartupEntryLifecycle(root, cases);
            SharedLeases(root, cases);
            ExistingRecoveryLeases(root, cases);
            RunningProcessDiscovery(root, cases);
            var report = new { Passed = true, Cases = cases, OutputDirectory = root, RealDesktopWrites = 0, RealRegistryWrites = 0 };
            File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { Passed = false, Cases = cases, Error = ex.ToString() }));
            return 1;
        }
    }

    private static void ShortcutLifecycle(string root, List<string> cases)
    {
        var scope = Path.Combine(root, "lifecycle");
        var directory = CreatePackage(Path.Combine(scope, "版本一"));
        var registration = new FixtureRegistration();
        var service = new DesktopShortcutService(Path.Combine(scope, "OneDrive 桌面"), registration);
        var first = service.InitializeOnNormalLaunch(directory);
        Assert(first.Shortcut?.Status == ShortcutStatus.Created && first.Warnings.Count == 0, "First normal launch creates a shortcut.");
        Assert(registration.LastDirectory == directory && registration.Writes == 1, "Current location is remembered only by injected registration.");
        var shortcutPath = first.Shortcut!.ShortcutPath!;
        var shortcut = DesktopShortcutService.ReadShortcut(shortcutPath);
        Assert(shortcut.TargetPath == Path.Combine(directory, "ShunshouToolbox.exe"), "Shell link points to the English executable.");
        Assert(shortcut.IconPath == shortcut.TargetPath && shortcut.IconIndex == 0, "Shell link uses the current application icon.");
        Assert(shortcut.WorkingDirectory == directory && shortcut.Arguments == "", "Working directory is the package and no command arguments are injected.");
        Assert(service.InitializeOnNormalLaunch(directory).Shortcut?.Status == ShortcutStatus.AlreadyCorrect, "Repeat launch is idempotent.");
        File.Delete(shortcutPath);
        Assert(service.InitializeOnNormalLaunch(directory).Shortcut?.Status == ShortcutStatus.KeptDeleted && !File.Exists(shortcutPath), "User deletion is respected.");
        Assert(service.EnsureShortcut(directory, explicitRequest: true).Status == ShortcutStatus.Created, "Explicit request can recreate a deleted shortcut.");
        var newer = CreatePackage(Path.Combine(scope, "版本二"));
        Assert(service.InitializeOnNormalLaunch(newer).Shortcut?.Status == ShortcutStatus.Updated, "A newly extracted version repoints the owned shortcut.");
        Assert(DesktopShortcutService.ReadShortcut(shortcutPath).TargetPath == Path.Combine(newer, PackageIdentity.ExecutableName), "Repointed shortcut has the new valid target.");
        Assert(Directory.EnumerateFiles(Path.GetDirectoryName(shortcutPath)!, "*.lnk").Count() == 1, "No duplicate or temporary desktop shortcuts remain.");
        cases.Add("Real IShellLink creation, idempotency, deletion respected, explicit recreation, new-version target update");
    }

    private static void LegacyPackageMigration(string root, List<string> cases)
    {
        foreach (var version in new[] { "0.2.0", "0.2.1" })
        {
            var scope = Path.Combine(root, "legacy-" + version);
            var directory = CreatePackage(Path.Combine(scope, "旧版目录"), PackageIdentity.LegacyExecutableName, version);
            Assert(PackageIdentity.IsValidDirectory(directory), "Legacy " + version + " package remains discoverable.");
            Assert(PackageIdentity.GetExecutablePath(directory) == Path.Combine(directory, "顺手工具箱.exe"), "Legacy entry point comes from its verified manifest.");
            File.WriteAllText(Path.Combine(directory, PackageIdentity.ExecutableName), "Unlisted stray English executable fixture.");
            Assert(PackageIdentity.GetExecutablePath(directory) == Path.Combine(directory, "顺手工具箱.exe"), "Unlisted English executable cannot override a legacy manifest.");
            var desktop = Path.Combine(scope, "desktop");
            var service = new DesktopShortcutService(desktop, new FixtureRegistration());
            var original = service.EnsureShortcut(directory);
            Assert(original.Status == ShortcutStatus.Created, "Legacy shortcut fixture is created with the legacy package target.");
            var originalInfo = DesktopShortcutService.ReadShortcut(original.ShortcutPath!);
            Assert(originalInfo.TargetPath == Path.Combine(directory, "顺手工具箱.exe") && originalInfo.IconPath == originalInfo.TargetPath, "Legacy shortcut records the Chinese target and old icon path.");

            CreatePackage(directory);
            var result = service.EnsureShortcut(directory);
            Assert(result.Status == ShortcutStatus.Updated, "In-place upgrade migrates its owned legacy shortcut.");
            var upgraded = DesktopShortcutService.ReadShortcut(result.ShortcutPath!);
            Assert(upgraded.TargetPath == Path.Combine(directory, "ShunshouToolbox.exe") && upgraded.IconPath == upgraded.TargetPath && upgraded.IconIndex == 0,
                "Upgraded shortcut target and icon both move to the English executable.");
            Assert(Path.GetFileName(result.ShortcutPath) == "顺手工具箱.lnk", "User-facing desktop display name remains Chinese.");

            EditFixtureShortcut(result.ShortcutPath!, iconPath: Path.Combine(directory, "顺手工具箱.exe"));
            Assert(DesktopShortcutService.ReadShortcut(result.ShortcutPath!).IconPath == Path.Combine(directory, "顺手工具箱.exe"),
                "Unicode shortcut fixture preserves the intentionally stale Chinese icon path.");
            Assert(service.EnsureShortcut(directory).Status == ShortcutStatus.Updated, "Owned shortcut with stale icon is repaired even when its target is already current.");
            Assert(DesktopShortcutService.ReadShortcut(result.ShortcutPath!).IconPath == upgraded.TargetPath, "Stale icon location is replaced by current executable icon.");

            var moved = CreatePackage(Path.Combine(scope, "ShunshouToolbox"));
            Assert(service.EnsureShortcut(moved).Status == ShortcutStatus.Updated, "Owned shortcut also follows the new English software directory.");
            Assert(DesktopShortcutService.ReadShortcut(result.ShortcutPath!).TargetPath == Path.Combine(moved, "ShunshouToolbox.exe"), "Moved package target is exact.");
            Assert(Directory.EnumerateFiles(desktop, "*.lnk").Count() == 1, "Migration retains one shortcut and leaves no temporary links.");
        }
        var mixed = CreatePackage(Path.Combine(root, "mixed-invalid"), includeLegacyExecutable: true);
        File.AppendAllText(Path.Combine(mixed, PackageIdentity.ExecutableName), "changed");
        Assert(PackageIdentity.GetExecutablePath(mixed) is null, "Invalid manifest-listed current executable cannot fall back to a valid legacy executable.");
        cases.Add("0.2.0 and 0.2.1 legacy manifest compatibility; in-place and moved-path shortcut migration; stale icon repair; no invalid-current fallback");
    }

    private static void PreserveUnrelated(string root, List<string> cases)
    {
        var scope = Path.Combine(root, "unrelated");
        var directory = CreatePackage(Path.Combine(scope, "app"));
        var desktop = Path.Combine(scope, "desktop");
        Directory.CreateDirectory(desktop);
        var path = Path.Combine(desktop, "顺手工具箱.lnk");
        var unrelated = "Unrelated existing shortcut fixture, never overwrite."u8.ToArray();
        File.WriteAllBytes(path, unrelated);
        var service = new DesktopShortcutService(desktop, new FixtureRegistration());
        Assert(service.EnsureShortcut(directory, explicitRequest: true).Status == ShortcutStatus.UnrelatedShortcut, "Unrelated existing filename is refused.");
        Assert(File.ReadAllBytes(path).SequenceEqual(unrelated), "Unrelated shortcut bytes are preserved exactly.");
        File.Delete(path);
        Assert(service.EnsureShortcut(directory, explicitRequest: true).Status == ShortcutStatus.Created, "Owned shortcut fixture is available for unrelated ownership change.");
        EditFixtureShortcut(path, description: "Unrelated application shortcut");
        var unrelatedLink = File.ReadAllBytes(path);
        Assert(service.EnsureShortcut(directory, explicitRequest: true).Status == ShortcutStatus.UnrelatedShortcut, "A valid shell link without our ownership description is refused.");
        Assert(File.ReadAllBytes(path).SequenceEqual(unrelatedLink), "Valid unrelated shell link is preserved byte-for-byte during migration.");
        cases.Add("Unrelated existing desktop shortcut preserved byte-for-byte");
    }

    private static void CleanLayoutMigration(string root, List<string> cases)
    {
        var scope = Path.Combine(root, "clean-layout");
        var directory = CreatePackage(Path.Combine(scope, "便携工具"));
        var desktop = Path.Combine(scope, "desktop");
        var service = new DesktopShortcutService(desktop, new FixtureRegistration());
        service.EnsureShortcut(directory);
        CreatePackage(directory, version: "0.3.0");
        Assert(PackageIdentity.GetExecutablePath(directory) == Path.Combine(directory, PackageIdentity.ExecutableName), "Clean manifest resolves the root launcher.");
        var result = service.EnsureShortcut(directory);
        Assert(result.Status == ShortcutStatus.AlreadyCorrect, "Existing root shortcut remains valid after private runtime migration.");
        var info = DesktopShortcutService.ReadShortcut(result.ShortcutPath!);
        Assert(info.TargetPath == Path.Combine(directory, PackageIdentity.ExecutableName) && info.WorkingDirectory == directory && info.IconPath == info.TargetPath,
            "Shortcut target, working directory and icon all use the clean root entry.");
        File.AppendAllText(Path.Combine(directory, "app", "Shunshou.App.exe"), "tampered");
        Assert(PackageIdentity.GetExecutablePath(directory) is null, "Modified private apphost invalidates package identity even when launcher is unchanged.");
        cases.Add("0.3.0 private runtime preserves root shortcut, icon and working directory; damaged inner apphost is rejected");
    }

    private static void ExplicitOptOut(string root, List<string> cases)
    {
        var scope = Path.Combine(root, "opt-out");
        var directory = CreatePackage(Path.Combine(scope, "app"));
        var desktop = Path.Combine(scope, "desktop");
        var service = new DesktopShortcutService(desktop, new FixtureRegistration());
        service.RecordOptOut(directory);
        Assert(service.InitializeOnNormalLaunch(directory).Shortcut?.Status == ShortcutStatus.KeptDeleted, "EXE unchecked shortcut option survives first launch.");
        Assert(!File.Exists(Path.Combine(desktop, "顺手工具箱.lnk")), "No shortcut is created after opt-out.");
        Assert(service.EnsureShortcut(directory, true).Status == ShortcutStatus.Created, "Later explicit selection supersedes the earlier opt-out.");
        cases.Add("EXE shortcut opt-out persists through normal startup");
    }

    private static void RejectInvalidPackage(string root, List<string> cases)
    {
        var scope = Path.Combine(root, "invalid-package");
        var directory = CreatePackage(Path.Combine(scope, "app"));
        File.AppendAllText(Path.Combine(directory, PackageIdentity.ExecutableName), "changed");
        var registration = new FixtureRegistration();
        var service = new DesktopShortcutService(Path.Combine(scope, "desktop"), registration);
        Assert(!PackageIdentity.IsValidDirectory(directory), "A changed executable does not match package identity.");
        Assert(service.InitializeOnNormalLaunch(directory).Shortcut?.Status == ShortcutStatus.InvalidPackage, "An invalid package does not create links.");
        Assert(registration.Writes == 0 && !Directory.Exists(Path.Combine(directory, "data")), "Invalid package initialization has no effects.");
        cases.Add("Invalid package identity blocks shortcut and remembered-directory changes");
    }

    private static void RegistrationFailureNonfatal(string root, List<string> cases)
    {
        var scope = Path.Combine(root, "registration-denied");
        var directory = CreatePackage(Path.Combine(scope, "app"));
        var service = new DesktopShortcutService(Path.Combine(scope, "desktop"), new FixtureRegistration { DenyWrite = true });
        var result = service.InitializeOnNormalLaunch(directory);
        Assert(result.Warnings.Count == 1 && result.Shortcut?.Status == ShortcutStatus.Created, "Denied registration is reported without breaking shortcut initialization.");
        cases.Add("Registration denial is nonfatal and isolated from shortcut creation");
    }

    /// <summary>Start-up entry behaviour is proven with an injected store, so the real Run key is never touched.</summary>
    private static void StartupEntryLifecycle(string root, List<string> cases)
    {
        string directory = Path.Combine(root, "startup", "顺手工具箱");
        Directory.CreateDirectory(directory);
        string launcher = Path.Combine(directory, "ShunshouToolbox.exe");
        File.WriteAllText(launcher, "inert fixture");
        var store = new FixtureStartupStore();
        var startup = new StartupRegistration(store);
        Assert(startup.Read(launcher) == StartupRegistrationState.Disabled, "A missing entry reads as disabled.");
        Assert(startup.Enable(launcher), "Enabling writes the entry.");
        string expected = StartupRegistration.ExpectedValue(launcher);
        Assert(store.Values[StartupRegistration.ValueName] == expected, "The entry quotes the launcher and passes the start-up switch.");
        Assert(expected.EndsWith("\" " + StartupRegistration.StartupArgument, StringComparison.Ordinal), "The start-up switch keeps the window minimised at boot.");
        Assert(startup.Read(launcher) == StartupRegistrationState.Enabled, "An entry that matches this copy reads as enabled.");
        Assert(startup.Disable() && !store.Values.ContainsKey(StartupRegistration.ValueName), "Disabling removes this product's entry.");
        Assert(startup.Read(launcher) == StartupRegistrationState.Disabled, "A removed entry reads as disabled again.");

        string moved = Path.Combine(root, "startup", "移动后的 顺手工具箱");
        Directory.CreateDirectory(moved);
        string movedLauncher = Path.Combine(moved, "ShunshouToolbox.exe");
        File.WriteAllText(movedLauncher, "inert fixture");
        store.Values[StartupRegistration.ValueName] = StartupRegistration.ExpectedValue(launcher);
        Assert(startup.Read(movedLauncher) == StartupRegistrationState.PointsElsewhere, "An entry pointing at another copy is reported, not silently accepted.");
        var repaired = startup.SyncOnLaunch(movedLauncher, out bool changed);
        Assert(changed && repaired == StartupRegistrationState.Enabled, "A normal launch repairs an enabled entry after the folder moved.");
        Assert(store.Values[StartupRegistration.ValueName] == StartupRegistration.ExpectedValue(movedLauncher), "The repaired entry names the running copy.");
        startup.Disable();
        var stillDisabled = startup.SyncOnLaunch(movedLauncher, out bool created);
        Assert(stillDisabled == StartupRegistrationState.Disabled && !created && !store.Values.ContainsKey(StartupRegistration.ValueName),
            "A launch never turns a disabled entry into an enabled one.");

        store.Values[StartupRegistration.ValueName] = "\"C:\\Windows\\notepad.exe\"";
        Assert(startup.Read(launcher) == StartupRegistrationState.PointsElsewhere, "A same-named entry from elsewhere is not treated as ours.");
        Assert(!startup.Disable(), "Disabling refuses to delete an entry that belongs to another program.");
        Assert(store.Values.ContainsKey(StartupRegistration.ValueName), "The unrelated entry survives untouched.");
        store.Values.Remove(StartupRegistration.ValueName);

        var denied = new StartupRegistration(new FixtureStartupStore { DenyAccess = true });
        Assert(denied.Read(launcher) == StartupRegistrationState.Unavailable, "A blocked store reads as unavailable instead of crashing.");
        Assert(!denied.Enable(launcher), "A blocked write reports failure so the menu can revert.");
        Assert(!denied.Disable(), "A blocked delete reports failure instead of claiming success.");
        cases.Add("Start-up entry: enable, disable, moved-folder repair, foreign-entry protection and denial");
    }

    private static void SharedLeases(string root, List<string> cases)
    {
        var directory = Path.Combine(root, "leases", "app");
        var lockRoot = Path.Combine(root, "leases", "locks");
        Directory.CreateDirectory(directory);
        var variant = Path.Combine(directory, ".") + Path.DirectorySeparatorChar;
        Assert(UpdateCoordination.NormalizeDirectory(directory) == UpdateCoordination.NormalizeDirectory(variant), "Equivalent paths share one identity.");
        using (var reader1 = UpdateCoordination.AcquireApplicationLease(directory, lockRoot))
        {
            Assert(reader1 is not null, "First app reader enters.");
            using (var reader2 = UpdateCoordination.AcquireApplicationLease(variant, lockRoot))
            {
                Assert(reader2 is not null, "Second app or elevated window can share the app lease.");
                using var denied = UpdateCoordination.AcquireUpdateLease(directory, lockRoot);
                Assert(denied is null, "Updater is blocked while applications run.");
                Assert(RunProbe(directory, lockRoot, "writer") == 77, "Separate updater process is also blocked.");
                Assert(RunProbe(directory, lockRoot, "reader") == 0, "Separate application process can share read access.");
            }
            using var stillDenied = UpdateCoordination.AcquireUpdateLease(directory, lockRoot);
            Assert(stillDenied is null, "Remaining application keeps update blocked.");
        }
        using (var writer = UpdateCoordination.AcquireUpdateLease(directory, lockRoot))
        {
            Assert(writer is not null, "Update enters after every application lease closes.");
            using var denied = UpdateCoordination.AcquireApplicationLease(directory, lockRoot);
            Assert(denied is null && RunProbe(directory, lockRoot, "reader") == 77, "Local and separate app launches are blocked during update.");
        }
        var disposable = UpdateCoordination.AcquireUpdateLease(directory, lockRoot);
        Assert(disposable is not null, "New update lease can be acquired.");
        Task.Run(() => disposable!.Dispose()).GetAwaiter().GetResult();
        using var resumed = UpdateCoordination.AcquireApplicationLease(directory, lockRoot);
        Assert(resumed is not null, "Lease can be released from an async continuation thread.");
        Assert(Directory.EnumerateFiles(lockRoot).Count() == 1, "One stable external lock file is retained.");
        cases.Add("Shared app versus exclusive update leases, normalized paths, cross-process exclusion and async release");
    }

    private static int RunProbe(string directory, string lockRoot, string mode)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var arg in new[] { "--lease-probe", directory, lockRoot, mode }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start fixture child.");
        if (!process.WaitForExit(15000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("Fixture lease probe timed out."); }
        return process.ExitCode;
    }

    private static void ExistingRecoveryLeases(string root, List<string> cases)
    {
        var scope = Path.Combine(root, "recovery-leases");
        var directory = Path.Combine(scope, "app");
        var lockRoot = Path.Combine(scope, "locks");
        Directory.CreateDirectory(directory);
        using (var absent = UpdateCoordination.AcquireExistingApplicationLease(directory, lockRoot))
            Assert(absent is null && !Directory.Exists(lockRoot), "Read-only recovery lease does not create a missing lock directory.");
        Assert(RunProbe(directory, lockRoot, "existing-reader") == 77 && !Directory.Exists(lockRoot), "Separate recovery process refuses a missing lease without filesystem writes.");
        using (var normal = UpdateCoordination.AcquireApplicationLease(directory, lockRoot))
            Assert(normal is not null, "Normal launch establishes its existing lease fixture.");
        var lockFile = Directory.GetFiles(lockRoot).Single();
        var bytes = File.ReadAllBytes(lockFile);
        var writeTime = File.GetLastWriteTimeUtc(lockFile);
        using (var recovery = UpdateCoordination.AcquireExistingApplicationLease(directory, lockRoot))
        {
            Assert(recovery is not null, "Read-only recovery acquires the existing lease.");
            Assert(RunProbe(directory, lockRoot, "existing-reader") == 0, "An elevated/second recovery process can share the existing lease.");
            using var blocked = UpdateCoordination.AcquireUpdateLease(directory, lockRoot);
            Assert(blocked is null, "Read-only recovery lease still blocks update.");
        }
        using (var updater = UpdateCoordination.AcquireUpdateLease(directory, lockRoot))
        {
            Assert(updater is not null, "Exclusive updater enters after recovery finishes.");
            using var blocked = UpdateCoordination.AcquireExistingApplicationLease(directory, lockRoot);
            Assert(blocked is null && RunProbe(directory, lockRoot, "existing-reader") == 77, "Active update blocks local and separate read-only recovery launches.");
        }
        Assert(Directory.GetFiles(lockRoot).Length == 1 && File.ReadAllBytes(lockFile).SequenceEqual(bytes) && File.GetLastWriteTimeUtc(lockFile) == writeTime,
            "Existing recovery lease does not change file bytes, timestamp or create extra files.");
        var otherDirectory = Path.Combine(scope, "new-package");
        Directory.CreateDirectory(otherDirectory);
        using (var missing = UpdateCoordination.AcquireExistingApplicationLease(otherDirectory, lockRoot))
            Assert(missing is null && Directory.GetFiles(lockRoot).Length == 1, "Existing lock directory cannot cause recovery to create a missing package lock.");
        cases.Add("Recovery leases only open existing files; missing leases refuse without writes and exclusive updates remain blocked across processes");
    }

    private static void RunningProcessDiscovery(string root, List<string> cases)
    {
        var packageRoot = Path.Combine(root, "processes", "app");
        var differentRoot = Path.Combine(root, "processes", "other-app");
        using var application = StartWaitingProbe(packageRoot, "ShunshouToolbox.exe");
        using var legacyApplication = StartWaitingProbe(packageRoot, "顺手工具箱.exe");
        using var internalApplication = StartWaitingProbe(packageRoot, "Shunshou.App.exe");
        using var tool = StartWaitingProbe(Path.Combine(packageRoot, "tools", "fixture"), "fixture-helper.exe");
        using var privateApplication = StartWaitingProbe(Path.Combine(packageRoot, "app"), "Shunshou.App.exe");
        using var privateTool = StartWaitingProbe(Path.Combine(packageRoot, "app", "tools", "recovery", "bin"), "photorec_win.exe");
        using var otherApplication = StartWaitingProbe(differentRoot, "ShunshouToolbox.exe");
        try
        {
            var blockers = UpdateCoordination.FindRunningAppProcesses(packageRoot);
            Assert(blockers.Any(p => p.ProcessId == application.Id && !p.LocationUncertain), "English application in the exact target directory is found.");
            Assert(blockers.Any(p => p.ProcessId == legacyApplication.Id && !p.LocationUncertain), "Legacy Chinese application in the exact target directory is found.");
            Assert(blockers.Any(p => p.ProcessId == internalApplication.Id && !p.LocationUncertain), "Internal apphost in the exact target directory is found.");
            Assert(blockers.Any(p => p.ProcessId == tool.Id && !p.LocationUncertain), "A child engine process inside the target tools folder is found.");
            Assert(blockers.Any(p => p.ProcessId == privateApplication.Id && !p.LocationUncertain), "Private apphost inside app is found even without its launcher.");
            Assert(blockers.Any(p => p.ProcessId == privateTool.Id && !p.LocationUncertain), "Recovery engine inside app/tools is found.");
            Assert(!blockers.Any(p => p.ProcessId == otherApplication.Id), "Identically named application in another directory is not blocked.");
        }
        finally
        {
            foreach (var process in new[] { application, legacyApplication, internalApplication, tool, privateApplication, privateTool, otherApplication })
            {
                process.StandardInput.WriteLine("exit");
                if (!process.WaitForExit(10000)) process.Kill(entireProcessTree: true);
            }
        }
        cases.Add("English, legacy Chinese and internal fixture app processes plus child engine found at exact target; other app location excluded");
    }

    private static Process StartWaitingProbe(string directory, string executableName)
    {
        Directory.CreateDirectory(directory);
        var sourceDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        const string assemblyName = "Shunshou.DesktopIntegration.Tests";
        foreach (var name in new[] { assemblyName + ".dll", assemblyName + ".deps.json", assemblyName + ".runtimeconfig.json", "Shunshou.DesktopIntegration.dll" })
            if (!File.Exists(Path.Combine(directory, name))) File.Copy(Path.Combine(sourceDirectory, name), Path.Combine(directory, name), overwrite: false);
        var target = Path.Combine(directory, executableName);
        File.Copy(Path.Combine(sourceDirectory, assemblyName + ".exe"), target, overwrite: false);
        var info = new ProcessStartInfo(target) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        info.ArgumentList.Add("--wait-probe");
        var process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start generated process fixture.");
        try
        {
            if (process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult() != "READY")
                throw new InvalidOperationException("Fixture child failed to become ready: " + process.StandardError.ReadToEnd());
            return process;
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.Dispose();
            throw;
        }
    }

    private static string CreatePackage(string directory, string executableName = PackageIdentity.ExecutableName, string version = "0.2.2", bool includeLegacyExecutable = false)
    {
        Directory.CreateDirectory(directory);
        var names = new List<string> { executableName, "Shunshou.App.dll" };
        if (Version.Parse(version) >= new Version(0, 3, 0))
            names = [executableName, "app/Shunshou.App.dll", "app/Shunshou.App.exe", "app/Shunshou.App.pri"];
        if (includeLegacyExecutable && executableName != PackageIdentity.LegacyExecutableName) names.Add(PackageIdentity.LegacyExecutableName);
        var files = names.Select(name =>
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("Non-executable package identity fixture: " + name);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(directory, name))!);
            File.WriteAllBytes(Path.Combine(directory, name), bytes);
            return new { Path = name, Bytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
        }).ToArray();
        File.WriteAllText(Path.Combine(directory, PackageIdentity.ManifestName), JsonSerializer.Serialize(new { Product = PackageIdentity.ProductName, Version = version, Architecture = "win-x64", Files = files }));
        return directory;
    }

    private static void EditFixtureShortcut(string path, string? description = null, string? iconPath = null)
    {
        // IShellLinkW and IPersistFile keep the fixture path and icon Unicode on every Windows locale.
        // The fixture intentionally retains its Chinese .lnk filename and legacy Chinese icon path.
        var shortcut = (IFixtureShellLinkW)new FixtureShellLink();
        try
        {
            ((IPersistFile)shortcut).Load(path, 0);
            if (description is not null) shortcut.SetDescription(description);
            if (iconPath is not null) shortcut.SetIconLocation(iconPath, 0);
            ((IPersistFile)shortcut).Save(path, true);
        }
        finally { Marshal.FinalReleaseComObject(shortcut); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class FixtureShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFixtureShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, nint findData, uint flags);
        void GetIDList(out nint itemIdList);
        void SetIDList(nint itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int capacity);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int capacity);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int capacity);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(nint window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FixtureRegistration : IInstallationRegistration
    {
        public string? LastDirectory { get; private set; }
        public int Writes { get; private set; }
        public bool DenyWrite { get; init; }
        public string? ReadLastDirectory() => LastDirectory;
        public void RememberDirectory(string directory)
        {
            if (DenyWrite) throw new UnauthorizedAccessException("Fixture denied registration.");
            LastDirectory = directory;
            Writes++;
        }
    }

    /// <summary>Stands in for HKCU\...\Run so the start-up logic is tested without writing the real registry.</summary>
    private sealed class FixtureStartupStore : IStartupValueStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        /// <summary>Simulates a locked-down registry key: every access is refused.</summary>
        public bool DenyAccess { get; init; }

        public string? Read(string name)
        {
            if (DenyAccess) throw new UnauthorizedAccessException("Fixture denied the start-up read.");
            return Values.TryGetValue(name, out var value) ? value : null;
        }

        public bool Write(string name, string value)
        {
            if (DenyAccess) throw new UnauthorizedAccessException("Fixture denied the start-up write.");
            Values[name] = value;
            return true;
        }

        public bool Delete(string name)
        {
            if (DenyAccess) throw new UnauthorizedAccessException("Fixture denied the start-up delete.");
            Values.Remove(name);
            return true;
        }
    }
}
