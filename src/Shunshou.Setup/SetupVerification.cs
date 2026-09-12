using System.Security.Cryptography;
using System.Text.Json;
using Shunshou.Deployment;
using Shunshou.DesktopIntegration;

namespace Shunshou.Setup;

internal static class SetupVerification
{
    public static int RunShortcutPreferences(string outputDirectory)
    {
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("Shortcut verification output must be a new empty directory.");
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        try
        {
            foreach (var scenario in new[] { "existing", "deleted", "fresh", "unrelated" })
            {
                var root = Path.Combine(output, scenario);
                var target = Path.Combine(root, "installed");
                var desktop = Path.Combine(root, "Desktop");
                var registration = new FixtureRegistration();
                var shortcuts = new DesktopShortcutService(desktop, registration);
                var link = Path.Combine(desktop, PackageIdentity.ProductName + ".lnk");
                if (scenario is "existing" or "deleted")
                {
                    CreateShortcutFixture(target, legacy: true);
                    Require(shortcuts.EnsureShortcut(target, true).Status == ShortcutStatus.Created, "Legacy shortcut fixture must be created");
                    Require(DesktopShortcutService.ReadShortcut(link).TargetPath == Path.Combine(target, PackageIdentity.LegacyExecutableName), "Fixture must start with the Chinese apphost");
                    if (scenario == "deleted") File.Delete(link); // Only this generated fixture Desktop.
                }
                CreateShortcutFixture(target, legacy: false);
                var legacyExecutable = Path.Combine(target, PackageIdentity.LegacyExecutableName);
                if (File.Exists(legacyExecutable)) File.Delete(legacyExecutable); // Simulates removal of a managed old entry in this fixture.
                byte[]? unrelated = null;
                if (scenario == "unrelated")
                {
                    Directory.CreateDirectory(desktop);
                    unrelated = [1, 7, 3, 9, 2];
                    File.WriteAllBytes(link, unrelated);
                }
                var notes = SetupShortcutPreference.Apply(shortcuts, target, createRequested: false);
                if (scenario == "existing")
                {
                    var actual = DesktopShortcutService.ReadShortcut(link);
                    var expected = Path.Combine(target, PackageIdentity.ExecutableName);
                    Require(notes.Count == 0 && actual.TargetPath == expected && actual.IconPath == expected,
                        "Unchecked setup must repair the existing owned target and icon without launching the app");
                    checks.Add("Unchecked setup repairs the legacy Chinese apphost link to the English target and icon without app launch");
                }
                else if (scenario == "unrelated")
                {
                    Require(notes.Count == 1 && File.ReadAllBytes(link).SequenceEqual(unrelated!), "Unrelated shortcut must remain byte-for-byte unchanged with a notice");
                    checks.Add("Unchecked setup preserves an unrelated existing shortcut byte-for-byte and reports a notice");
                }
                else
                {
                    Require(notes.Count == 0 && !File.Exists(link), "Unchecked setup must not create or recreate a missing shortcut");
                    shortcuts.InitializeOnNormalLaunch(target);
                    Require(!File.Exists(link), "Later app startup must respect the unchecked choice");
                    checks.Add(scenario == "deleted" ? "Deleted shortcut remains absent during update and later startup" : "Fresh unchecked install creates no shortcut during setup or later startup");
                }
            }
            File.WriteAllText(Path.Combine(output, "setup-shortcut-verification.json"), JsonSerializer.Serialize(new
            {
                Passed = true, Checks = checks, UserDesktopWrites = 0, UserRegistryWrites = 0, UserProgramLaunches = 0
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "setup-shortcut-verification.json"), JsonSerializer.Serialize(new
            {
                Passed = false, Checks = checks, Error = ex.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }

    private static void CreateShortcutFixture(string directory, bool legacy)
    {
        Directory.CreateDirectory(directory);
        var executable = legacy ? PackageIdentity.LegacyExecutableName : PackageIdentity.ExecutableName;
        var content = new Dictionary<string, string>
        {
            [executable] = "inert shortcut fixture",
            [Path.ChangeExtension(executable, ".pri")] = "inert resource fixture",
            ["Shunshou.App.dll"] = "inert application fixture"
        };
        foreach (var (name, text) in content) File.WriteAllText(Path.Combine(directory, name), text);
        var files = content.Keys.Select(name =>
        {
            var bytes = File.ReadAllBytes(Path.Combine(directory, name));
            return new { Path = name, Bytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
        }).ToArray();
        File.WriteAllText(Path.Combine(directory, "package-manifest.json"), JsonSerializer.Serialize(new
        {
            Product = PackageIdentity.ProductName, Version = legacy ? "0.2.1" : "0.2.2", Architecture = "win-x64", Files = files
        }));
    }

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
            Require(RunShortcutPreferences(Path.Combine(output, "shortcut-preferences")) == 0, "Setup shortcut opt-out regression");
            checks.Add("Unchecked setup repairs existing owned shortcuts without creating missing or replacing unrelated links");
            var payload = await Payload.ExtractAsync(Path.Combine(output, "payload"), metadata, null, default);
            checks.Add("Embedded payload length and SHA256 match build metadata");
            var service = new DeploymentService();
            var fresh = Path.Combine(output, "fresh");
            var freshRequest = new DeploymentRequest(payload, metadata.Sha256, fresh) { ExpectedVersion = metadata.Version, ZipRoot = metadata.ZipRoot };
            var installed = await service.InstallOrUpdateAsync(freshRequest);
            Require(!installed.WasUpdate && installed.PreviousBackupDirectory is null, "Initial deployment result");
            await VerifyFilesAsync(fresh);
            Require(PackageIdentity.GetExecutablePath(fresh) == Path.Combine(fresh, PackageIdentity.ExecutableName)
                && File.Exists(Path.Combine(fresh, "app", "Shunshou.App.pri")), "Native launcher and private WinUI resources");
            Require(Directory.EnumerateFileSystemEntries(fresh).Count() == 4 &&
                Directory.Exists(Path.Combine(fresh, "app")) && Directory.Exists(Path.Combine(fresh, "docs")),
                "Fresh package has a clean four-entry root");
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
            Require(info.TargetPath == Path.Combine(fresh, PackageIdentity.ExecutableName) && info.WorkingDirectory == fresh, "Shortcut destination");
            File.Delete(shortcutPath); // Only the fixture Desktop under the requested empty output root.
            Require(shortcutService.EnsureShortcut(fresh).Status == ShortcutStatus.KeptDeleted, "Respect manual deletion");
            Require(shortcutService.EnsureShortcut(fresh, true).Status == ShortcutStatus.Created, "Explicit shortcut recreation");
            checks.Add("Fixture desktop shortcut target, no duplicates, deletion preference and explicit recreation");

            var target = Path.Combine(output, "updated");
            int baselineIndex = Array.IndexOf(args, "--baseline-zip");
            var baseline = baselineIndex >= 0 ? Path.GetFullPath(args[baselineIndex + 1]) : payload;
            var baselineHash = await HashAsync(baseline);
            await service.InstallOrUpdateAsync(new(baseline, baselineHash, target));
            var baselineExecutable = PackageIdentity.GetExecutablePath(target);
            Require(baselineExecutable is not null, "Legacy or current baseline entry is verified before update");
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
            var updatedShortcut = shortcutService.EnsureShortcut(target, true);
            Require(updatedShortcut.Status is ShortcutStatus.AlreadyCorrect or ShortcutStatus.Updated, "Shortcut follows the current entry after update");
            Require(DesktopShortcutService.ReadShortcut(shortcutPath).TargetPath == Path.Combine(target, PackageIdentity.ExecutableName), "Updated shortcut targets the English apphost");
            if (Path.GetFileName(baselineExecutable) == PackageIdentity.LegacyExecutableName)
            {
                Require(!File.Exists(Path.Combine(target, PackageIdentity.LegacyExecutableName)), "Obsolete managed Chinese entry is absent from current folder");
                Require(File.Exists(Path.Combine(updated.PreviousBackupDirectory!, PackageIdentity.LegacyExecutableName)), "Previous Chinese entry remains in retained backup");
                checks.Add("Legacy Chinese entry → English apphost upgrade repairs the existing Chinese desktop shortcut");
            }
            if (File.Exists(Path.Combine(updated.PreviousBackupDirectory!, "Shunshou.App.dll")))
            {
                Require(!File.Exists(Path.Combine(target, "Shunshou.App.dll")) &&
                    File.Exists(Path.Combine(target, "app", "Shunshou.App.dll")), "Flat runtime moves under app after update");
                checks.Add("Flat legacy runtime moves under app while the full previous layout remains in backup");
            }
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
