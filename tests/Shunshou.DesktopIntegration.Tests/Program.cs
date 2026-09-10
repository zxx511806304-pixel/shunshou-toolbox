using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
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
            using var lease = args[3] == "reader"
                ? UpdateCoordination.AcquireApplicationLease(args[1], args[2])
                : UpdateCoordination.AcquireUpdateLease(args[1], args[2]);
            return lease is null ? 77 : 0;
        }
        var root = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/desktop-integration");
        root = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var cases = new List<string>();
        try
        {
            ShortcutLifecycle(root, cases);
            PreserveUnrelated(root, cases);
            ExplicitOptOut(root, cases);
            RejectInvalidPackage(root, cases);
            RegistrationFailureNonfatal(root, cases);
            SharedLeases(root, cases);
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
        Assert(shortcut.TargetPath == Path.Combine(directory, PackageIdentity.ExecutableName), "Shell link points to the Chinese executable.");
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
        cases.Add("Unrelated existing desktop shortcut preserved byte-for-byte");
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

    private static void RunningProcessDiscovery(string root, List<string> cases)
    {
        var packageRoot = Path.Combine(root, "processes", "app");
        var differentRoot = Path.Combine(root, "processes", "other-app");
        using var application = StartWaitingProbe(packageRoot, "Shunshou.App.exe");
        using var tool = StartWaitingProbe(Path.Combine(packageRoot, "tools", "fixture"), "fixture-helper.exe");
        using var otherApplication = StartWaitingProbe(differentRoot, "Shunshou.App.exe");
        try
        {
            var blockers = UpdateCoordination.FindRunningAppProcesses(packageRoot);
            Assert(blockers.Any(p => p.ProcessId == application.Id && !p.LocationUncertain), "Legacy application in the exact target directory is found.");
            Assert(blockers.Any(p => p.ProcessId == tool.Id && !p.LocationUncertain), "A child engine process inside the target tools folder is found.");
            Assert(!blockers.Any(p => p.ProcessId == otherApplication.Id), "Identically named application in another directory is not blocked.");
        }
        finally
        {
            foreach (var process in new[] { application, tool, otherApplication })
            {
                process.StandardInput.WriteLine("exit");
                if (!process.WaitForExit(10000)) process.Kill(entireProcessTree: true);
            }
        }
        cases.Add("Generated fixture app and child engine are found at the exact target; other app location excluded");
    }

    private static Process StartWaitingProbe(string directory, string executableName)
    {
        Directory.CreateDirectory(directory);
        var sourceDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        const string assemblyName = "Shunshou.DesktopIntegration.Tests";
        foreach (var name in new[] { assemblyName + ".dll", assemblyName + ".deps.json", assemblyName + ".runtimeconfig.json", "Shunshou.DesktopIntegration.dll" })
            File.Copy(Path.Combine(sourceDirectory, name), Path.Combine(directory, name), overwrite: false);
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

    private static string CreatePackage(string directory)
    {
        Directory.CreateDirectory(directory);
        var files = new[] { PackageIdentity.ExecutableName, "Shunshou.App.dll" }.Select(name =>
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("Non-executable package identity fixture: " + name);
            File.WriteAllBytes(Path.Combine(directory, name), bytes);
            return new { Path = name, Bytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
        }).ToArray();
        File.WriteAllText(Path.Combine(directory, PackageIdentity.ManifestName), JsonSerializer.Serialize(new { Product = PackageIdentity.ProductName, Version = "0.3.0", Architecture = "win-x64", Files = files }));
        return directory;
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
}
