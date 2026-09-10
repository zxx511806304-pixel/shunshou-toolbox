using System.Security.Cryptography;
using System.Text.Json;
using Shunshou.Deployment;
using Shunshou.DesktopIntegration;

namespace Shunshou.Setup;

internal static class SetupVerification
{
    public static int RunUi(string outputDirectory)
    {
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("UI verification output must be a new empty directory.");
        Directory.CreateDirectory(output);
        var registration = new FixtureRegistration();
        var environment = new SetupEnvironment(Path.Combine(output, "installed"), Path.Combine(output, "Desktop"), Path.Combine(output, "locks"), registration);
        int result = 1;
        using var form = new SetupForm(false, environment);
        form.Shown += async (_, _) =>
        {
            try
            {
                await form.VerifyUserFlowAsync(Path.Combine(output, "completed.png"));
                Require(registration.Directory == environment.TargetDirectory, "The setup completion must remember the fixture target");
                await File.WriteAllTextAsync(Path.Combine(output, "setup-ui-verification.json"), JsonSerializer.Serialize(new
                {
                    Passed = true,
                    Checks = new[] { "Real setup window uses the production RunAsync flow", "Embedded payload deployed and UI controls recovered", "Checked shortcut created in fixture Desktop", "Fixture registration remembers target", "Completion status and primary action fit the client area" },
                    UserDesktopWrites = 0, UserRegistryWrites = 0, UserProgramLaunches = 0
                }, new JsonSerializerOptions { WriteIndented = true }));
                result = 0;
            }
            catch (Exception ex)
            {
                await File.WriteAllTextAsync(Path.Combine(output, "setup-ui-verification.json"), JsonSerializer.Serialize(new { Passed = false, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { form.Close(); }
        };
        Application.Run(form);
        return result;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        string output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("Verification output must be a new empty directory.");
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        try
        {
            var metadata = Payload.ReadMetadata();
            var payload = await Payload.ExtractAsync(Path.Combine(output, "payload"), metadata, null, default);
            checks.Add("Embedded payload length and SHA256 match build metadata");
            var service = new DeploymentService();
            var fresh = Path.Combine(output, "fresh");
            var freshRequest = new DeploymentRequest(payload, metadata.Sha256, fresh) { ExpectedVersion = metadata.Version, ZipRoot = metadata.ZipRoot };
            var installed = await service.InstallOrUpdateAsync(freshRequest);
            Require(!installed.WasUpdate && installed.PreviousBackupDirectory is null, "Initial deployment result");
            await VerifyFilesAsync(fresh);
            checks.Add("Fresh extraction: every managed file matches the embedded manifest");

            var locks = Path.Combine(output, "locks");
            using (var app = UpdateCoordination.AcquireApplicationLease(fresh, locks))
            using (var elevated = UpdateCoordination.AcquireApplicationLease(fresh, locks))
            {
                Require(app is not null && elevated is not null, "Shared application leases");
                using var blocked = UpdateCoordination.AcquireUpdateLease(fresh, locks);
                Require(blocked is null, "Running app prevents update");
            }
            using (var update = UpdateCoordination.AcquireUpdateLease(fresh, locks))
            {
                Require(update is not null, "Update lease after app exits");
                using var blocked = UpdateCoordination.AcquireApplicationLease(fresh, locks);
                Require(blocked is null, "Update prevents application launch");
            }
            checks.Add("Shared app/elevated leases and exclusive update lease exclude each other");

            var registration = new FixtureRegistration();
            var desktop = Path.Combine(output, "Desktop");
            var shortcutService = new DesktopShortcutService(desktop, registration);
            var link = shortcutService.InitializeOnNormalLaunch(fresh);
            Require(link.Shortcut?.Status == ShortcutStatus.Created && registration.Directory == fresh, "First launch creates one shortcut and remembers path");
            var shortcutPath = Path.Combine(desktop, "顺手工具箱.lnk");
            var info = DesktopShortcutService.ReadShortcut(shortcutPath);
            Require(info.TargetPath == Path.Combine(fresh, "顺手工具箱.exe") && info.WorkingDirectory == fresh, "Shortcut destination");
            File.Delete(shortcutPath); // Only the fixture Desktop under the requested empty output root.
            Require(shortcutService.EnsureShortcut(fresh).Status == ShortcutStatus.KeptDeleted, "Respect manual deletion");
            Require(shortcutService.EnsureShortcut(fresh, true).Status == ShortcutStatus.Created, "Explicit shortcut recreation");
            checks.Add("Fixture desktop shortcut target, no duplicates, deletion preference and explicit recreation");

            var target = Path.Combine(output, "updated");
            int baselineIndex = Array.IndexOf(args, "--baseline-zip");
            var baseline = baselineIndex >= 0 ? Path.GetFullPath(args[baselineIndex + 1]) : payload;
            var baselineHash = await HashAsync(baseline);
            await service.InstallOrUpdateAsync(new(baseline, baselineHash, target));
            var preserved = new Dictionary<string, string>
            {
                ["data/rename-history/session.json"] = "{\"test\":\"rename recovery\"}",
                ["data/uninstall-backups/.integrity-key"] = "fixture-backup-key-keep-exactly",
                ["data/uninstall-backups/session/manifest.json"] = "{\"test\":\"backup restore\"}",
                ["我的文件/输出.txt"] = "用户生成的文字\r\n第二行",
                ["user-settings.json"] = "{\"quality\":95}"
            };
            foreach (var (relative, content) in preserved)
            {
                var path = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, content);
            }
            shortcutService.EnsureShortcut(target, true);
            DeploymentResult updated;
            using (var lease = UpdateCoordination.AcquireUpdateLease(target, locks))
            {
                Require(lease is not null, "Fixture update lease");
                updated = await service.InstallOrUpdateAsync(new(payload, metadata.Sha256, target) { ExpectedVersion = metadata.Version, ZipRoot = metadata.ZipRoot });
            }
            Require(updated.WasUpdate && Directory.Exists(updated.PreviousBackupDirectory), "Retained old version backup");
            await VerifyFilesAsync(target);
            foreach (var (relative, content) in preserved)
            {
                Require(await File.ReadAllTextAsync(Path.Combine(target, relative)) == content, "New version preserves " + relative);
                Require(await File.ReadAllTextAsync(Path.Combine(updated.PreviousBackupDirectory!, relative)) == content, "Backup preserves " + relative);
            }
            Require(shortcutService.EnsureShortcut(target, true).Status == ShortcutStatus.AlreadyCorrect, "Shortcut remains stable after update");
            checks.Add("Full old ZIP → embedded version update, exact settings/output/history/key preservation and retained backup");
            checks.Add("Desktop and registration are isolated fixtures; no real user desktop/registry writes or program launch");
            await File.WriteAllTextAsync(Path.Combine(output, "setup-verification.json"), JsonSerializer.Serialize(new
            {
                Passed = true, metadata.Version, metadata.Sha256, Checks = checks,
                FreshDirectory = fresh, UpdatedDirectory = target, updated.PreviousBackupDirectory,
                UserDesktopWrites = 0, UserRegistryWrites = 0
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "setup-verification.json"), JsonSerializer.Serialize(new { Passed = false, Checks = checks, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }

    private static async Task VerifyFilesAsync(string root)
    {
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "package-manifest.json")));
        foreach (var file in manifest.RootElement.GetProperty("Files").EnumerateArray())
        {
            string path = Path.Combine(root, file.GetProperty("Path").GetString()!);
            Require(new FileInfo(path).Length == file.GetProperty("Bytes").GetInt64(), "Managed file length: " + path);
            Require((await HashAsync(path)).Equals(file.GetProperty("Sha256").GetString(), StringComparison.OrdinalIgnoreCase), "Managed file hash: " + path);
        }
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class FixtureRegistration : IInstallationRegistration
    {
        public string? Directory { get; private set; }
        public string? ReadLastDirectory() => Directory;
        public void RememberDirectory(string directory) => Directory = directory;
    }
}
