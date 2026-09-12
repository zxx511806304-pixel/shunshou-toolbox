using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shunshou.Deployment;

if (args.FirstOrDefault() == "--crash-child")
{
    var checkpoint = Enum.Parse<DeploymentCheckpoint>(args[4]);
    var service = new DeploymentService(point => { if (point == checkpoint) Environment.Exit(86); });
    await service.InstallOrUpdateAsync(new(args[1], args[2], args[3]));
    return 87;
}

var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/deployment");
var run = Path.Combine(output, "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(run);
var results = new List<object>();
var failures = 0;
await Run("initial install uses exact folder and verified manifest", async f =>
{
    var package = f.Package("0.2.2");
    var inspect = await new DeploymentService().InspectAsync(package.Request(f.Target));
    Check(!inspect.IsUpdate && inspect.Version == "0.2.2" && inspect.PreservedBytes == 0, "Initial inspection metadata.");
    var result = await new DeploymentService().InstallOrUpdateAsync(package.Request(f.Target));
    Check(!result.WasUpdate && result.PreviousBackupDirectory is null, "New folder must not report an old install.");
    Check(result.TargetDirectory == f.Target && File.Exists(Path.Combine(f.Target, PackageContent.CurrentExecutableName))
        && File.Exists(Path.Combine(f.Target, PackageContent.CurrentResourceName)), "Install must use chosen folder with English executable and matching PRI.");
    Check(!File.Exists(Path.Combine(f.Target, PackageContent.LegacyExecutableName)), "New package must not add a Chinese apphost.");
    Check(await f.Version(f.Target) == "0.2.2", "Version after install.");
});
await Run("legacy 0.2.1 Chinese entry upgrades to English 0.2.2 while preserving user names and backup", async f =>
{
    await f.Install("0.2.1");
    var user = f.WriteUser("data/用户记录/课程.txt", "unchanged-user-data");
    var backupKey = f.WriteUser("data/uninstall-backups/.integrity-key", "unchanged-key");
    var original = await FileTrees.CaptureAsync(f.Target, default);
    Check(File.Exists(Path.Combine(f.Target, PackageContent.LegacyExecutableName)), "Fixture must start with the legacy Chinese entry.");
    var result = await new DeploymentService().InstallOrUpdateAsync(f.Package("0.2.2").Request(f.Target));
    Check(result.WasUpdate && result.TargetDirectory == f.Target, "Upgrade must keep the user-selected directory.");
    Check(File.Exists(Path.Combine(f.Target, PackageContent.CurrentExecutableName))
        && File.Exists(Path.Combine(f.Target, PackageContent.CurrentResourceName)), "English executable and matching PRI deployed.");
    Check(!File.Exists(Path.Combine(f.Target, PackageContent.LegacyExecutableName))
        && !File.Exists(Path.Combine(f.Target, "顺手工具箱.pri")), "Old managed aliases must not remain in the active version.");
    Check(File.ReadAllText(user) == "unchanged-user-data" && File.ReadAllText(backupKey) == "unchanged-key", "Chinese user paths and backup key remain exact.");
    FileTrees.Equal(original, await FileTrees.CaptureAsync(result.PreviousBackupDirectory!, default));
});
foreach (var collision in new[] { PackageContent.CurrentExecutableName, PackageContent.CurrentResourceName })
    await Run("English entry migration preserves conflicting untracked user file: " + collision, async f =>
    {
        await f.Install("0.2.1");
        f.WriteUser(collision, "user-content-that-must-not-be-replaced");
        var before = await FileTrees.CaptureAsync(f.Target, default);
        await Refuses(() => new DeploymentService().InstallOrUpdateAsync(f.Package("0.2.2").Request(f.Target)));
        FileTrees.Equal(before, await FileTrees.CaptureAsync(f.Target, default));
    });
await Run("English apphost without its matching PRI is rejected before deployment", async f =>
{
    await f.Install("0.2.1");
    var before = await FileTrees.CaptureAsync(f.Target, default);
    await Refuses(() => new DeploymentService().InstallOrUpdateAsync(f.Package("0.2.2", includeEntryResource: false).Request(f.Target)));
    FileTrees.Equal(before, await FileTrees.CaptureAsync(f.Target, default));
});
await Run("update preserves all user files empty folders metadata and retained backup", async f =>
{
    await f.Install("0.2.0");
    var user = f.WriteUser("data/uninstall-backups/.integrity-key", "secret-fixture-key");
    f.WriteUser("data/rename-history/history.json", "fixture-history");
    f.WriteUser("我的导出/课程图片.jpg", "fixture-image-bytes");
    Directory.CreateDirectory(Path.Combine(f.Target, "data", "空目录"));
    var stamp = new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    File.SetLastWriteTimeUtc(user, stamp);
    File.SetAttributes(user, FileAttributes.Hidden | FileAttributes.ReadOnly);
    var before = await FileTrees.CaptureAsync(f.Target, default);
    var package = f.Package("0.2.1");
    var inspection = await new DeploymentService().InspectAsync(package.Request(f.Target));
    Check(inspection.IsUpdate && inspection.CurrentVersion == "0.2.0" && inspection.PreservedFiles == 3, "Preserved file inspection.");
    var result = await new DeploymentService().InstallOrUpdateAsync(package.Request(f.Target));
    Check(result.WasUpdate && Directory.Exists(result.PreviousBackupDirectory), "Old folder must be retained.");
    FileTrees.Equal(before, await FileTrees.CaptureAsync(result.PreviousBackupDirectory!, default));
    Check(File.ReadAllText(user) == "secret-fixture-key" && File.GetLastWriteTimeUtc(user) == stamp
        && File.GetAttributes(user).HasFlag(FileAttributes.ReadOnly), "User bytes, attributes and time preserved.");
    Check(Directory.Exists(Path.Combine(f.Target, "data", "空目录")), "Empty user folder preserved.");
    Check(await f.Version(f.Target) == "0.2.1", "Updated version.");
});
await Run("0.2.2 flat runtime upgrades to 0.3.0 clean layout while preserving user data and empty user folders", async f =>
{
    var baseline = f.Package("0.2.2", extra: new() {
        ["zh-CN/runtime.resources.dll"] = "old-language-runtime", ["de-DE/runtime.resources.dll"] = "old-other-language",
        ["Assets/icon.png"] = "old-icon", ["old-tools/sub/runtime.dll"] = "old-tool"
    });
    await new DeploymentService().InstallOrUpdateAsync(baseline.Request(f.Target));
    f.WriteUser("data/uninstall-backups/.integrity-key", "exact-user-key");
    f.WriteUser("data/rename-history/session.json", "exact-history");
    f.WriteUser("我的文件/图片.png", "exact-picture");
    f.WriteUser("zh-CN/user-note.txt", "user-file-in-runtime-folder");
    Directory.CreateDirectory(Path.Combine(f.Target, "old-tools", "my-empty-folder"));
    Directory.CreateDirectory(Path.Combine(f.Target, "empty-user-folder"));
    var before = await FileTrees.CaptureAsync(f.Target, default);
    var update = await new DeploymentService().InstallOrUpdateAsync(f.Package("0.3.0").Request(f.Target));
    Check(File.Exists(Path.Combine(f.Target, "ShunshouToolbox.exe")) && File.Exists(Path.Combine(f.Target, "app/Shunshou.App.exe")), "Root launcher and private runtime both exist.");
    Check(!File.Exists(Path.Combine(f.Target, "Shunshou.App.dll")) && !Directory.Exists(Path.Combine(f.Target, "de-DE")) && !Directory.Exists(Path.Combine(f.Target, "Assets")), "Obsolete managed root files and empty runtime directories are absent.");
    Check(!Directory.Exists(Path.Combine(f.Target, "old-tools/sub")), "Obsolete nested managed runtime folder is not copied.");
    Check(Directory.Exists(Path.Combine(f.Target, "old-tools/my-empty-folder")) && Directory.Exists(Path.Combine(f.Target, "empty-user-folder")), "Even empty user folders and their ancestors survive.");
    foreach (var relative in new[] { "data/uninstall-backups/.integrity-key", "data/rename-history/session.json", "我的文件/图片.png", "zh-CN/user-note.txt" })
        Check(File.ReadAllBytes(Path.Combine(f.Target, relative)).SequenceEqual(File.ReadAllBytes(Path.Combine(update.PreviousBackupDirectory!, relative))), "User bytes remain exact: " + relative);
    FileTrees.Equal(before, await FileTrees.CaptureAsync(update.PreviousBackupDirectory!, default));
});
await Run("clean-layout collision with an existing user app file refuses without touching the old tree", async f =>
{
    await f.Install("0.2.2");
    f.WriteUser("app/Shunshou.App.exe", "this-is-user-content");
    var before = await FileTrees.CaptureAsync(f.Target, default);
    await Refuses(() => new DeploymentService().InstallOrUpdateAsync(f.Package("0.3.0").Request(f.Target)));
    FileTrees.Equal(before, await FileTrees.CaptureAsync(f.Target, default));
});
await Run("clean-layout package without private WinUI resources is refused before deployment", async f =>
{
    await f.Install("0.2.2");
    var before = await FileTrees.CaptureAsync(f.Target, default);
    await Refuses(() => new DeploymentService().InstallOrUpdateAsync(f.Package("0.3.0", includeEntryResource: false).Request(f.Target)));
    FileTrees.Equal(before, await FileTrees.CaptureAsync(f.Target, default));
});
await Run("same version verified reinstall retains prior backups across further updates", async f =>
{
    await f.Install("0.2.1");
    f.WriteUser("data/settings.json", "settings");
    var package = f.Package("0.2.1");
    var first = await new DeploymentService().InstallOrUpdateAsync(package.Request(f.Target));
    var second = await new DeploymentService().InstallOrUpdateAsync(f.Package("0.2.2").Request(f.Target));
    Check(first.PreviousBackupDirectory != second.PreviousBackupDirectory
        && Directory.Exists(first.PreviousBackupDirectory) && Directory.Exists(second.PreviousBackupDirectory), "Every backup retained uniquely.");
    Check(File.ReadAllText(Path.Combine(f.Target, "data/settings.json")) == "settings", "Settings survive repeated updates.");
});
await Run("downgrade and modified managed file refuse without changing source", async f =>
{
    await f.Install("0.3.0");
    var original = await FileTrees.CaptureAsync(f.Target, default);
    await Refuses(() => new DeploymentService().InstallOrUpdateAsync(f.Package("0.2.9").Request(f.Target)));
    FileTrees.Equal(original, await FileTrees.CaptureAsync(f.Target, default));
    File.WriteAllText(Path.Combine(f.Target, "app", "Shunshou.Core.dll"), "user-modified");
    var modified = await FileTrees.CaptureAsync(f.Target, default);
    await Refuses(() => new DeploymentService().InstallOrUpdateAsync(f.Package("0.3.1").Request(f.Target)));
    FileTrees.Equal(modified, await FileTrees.CaptureAsync(f.Target, default));
});
await Run("initial install rejects arbitrary nonempty folder including empty subfolder", async f =>
{
    Directory.CreateDirectory(Path.Combine(f.Target, "user-folder"));
    var package = f.Package("0.2.1");
    await Refuses(() => new DeploymentService().InstallOrUpdateAsync(package.Request(f.Target)));
    Check(Directory.Exists(Path.Combine(f.Target, "user-folder")) && !File.Exists(Path.Combine(f.Target, "package-manifest.json")), "Arbitrary directory untouched.");
});
await Run("initial empty target can be installed and its empty backup is retained", async f =>
{
    Directory.CreateDirectory(f.Target);
    var result = await new DeploymentService().InstallOrUpdateAsync(f.Package("0.2.1").Request(f.Target));
    Check(!result.WasUpdate && Directory.Exists(result.PreviousBackupDirectory)
        && !Directory.EnumerateFileSystemEntries(result.PreviousBackupDirectory!).Any(), "Empty original retained.");
});
await Run("corrupt outer ZIP hash and inner file hash fail before original mutation", async f =>
{
    await f.Install("0.2.0");
    var before = await FileTrees.CaptureAsync(f.Target, default);
    var outer = f.Package("0.2.1");
    await Refuses(() => new DeploymentService().InstallOrUpdateAsync(outer.Request(f.Target) with { ExpectedZipSha256 = new string('0', 64) }));
    var inner = f.Package("0.2.1", modifyManifest: m => m.Files[0] = m.Files[0] with { Sha256 = new string('0', 64) });
    await Refuses(() => new DeploymentService().InstallOrUpdateAsync(inner.Request(f.Target)));
    FileTrees.Equal(before, await FileTrees.CaptureAsync(f.Target, default));
});
await Run("wrong product architecture version and malformed manifests are rejected", async f =>
{
    foreach (var value in new[] { "Product", "Architecture", "Version", "Null", "Json" })
    {
        var package = f.Package("0.2.1", manifestTransform: bytes => value switch
        {
            "Product" => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("顺手工具箱", "AnotherProduct")),
            "Architecture" => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("win-x64", "win-arm64")),
            "Version" => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("0.2.1", "latest")),
            "Null" => Encoding.UTF8.GetBytes("null"),
            _ => Encoding.UTF8.GetBytes("{invalid")
        });
        // JSON escapes non-ASCII product by default; explicitly create wrong product below.
        if (value == "Product") package = f.Package("0.2.1", manifestTransform: _ => Encoding.UTF8.GetBytes("{\"Product\":\"Other\",\"Version\":\"0.2.1\",\"Architecture\":\"win-x64\",\"Files\":[]}"));
        await Refuses(() => new DeploymentService().InstallOrUpdateAsync(package.Request(f.Target)));
    }
    await Refuses(() => new DeploymentService().InstallOrUpdateAsync(f.Package("0.2.1").Request(f.Target) with { ExpectedVersion = "0.2.2" }));
    Check(!Directory.Exists(f.Target), "No failed package created target.");
});
await Run("ZIP traversal extra files case aliases links and managed data are refused", async f =>
{
    foreach (var path in new[] { "../escape.txt", "/absolute.txt", "C:/escape.txt", "folder\\escape.txt", "folder/../escape.txt", "CON.txt", "trailing./x", "a//b" })
    {
        var package = f.Package("0.2.1", extra: new() { [path] = "bad" });
        await Refuses(() => new DeploymentService().InstallOrUpdateAsync(package.Request(f.Target)));
    }
    var unlisted = f.Package("0.2.1", unlisted: "not-listed.txt");
    await Refuses(() => new DeploymentService().InstallOrUpdateAsync(unlisted.Request(f.Target)));
    var aliases = f.Package("0.2.1", extra: new() { ["Shunshou.core.dll"] = "alias" });
    await Refuses(() => new DeploymentService().InstallOrUpdateAsync(aliases.Request(f.Target)));
    var data = f.Package("0.2.1", extra: new() { ["data/settings.json"] = "overwrite" });
    await Refuses(() => new DeploymentService().InstallOrUpdateAsync(data.Request(f.Target)));
    var link = f.Package("0.2.1", symlink: true);
    await Refuses(() => new DeploymentService().InstallOrUpdateAsync(link.Request(f.Target)));
    Check(!Directory.Exists(f.Target) && !File.Exists(Path.Combine(f.Root, "escape.txt")), "No traversal writes or partial install.");
});
await Run("new managed paths cannot overwrite user files or directories", async f =>
{
    await f.Install("0.2.0");
    f.WriteUser("tools/custom.txt", "must-survive");
    Directory.CreateDirectory(Path.Combine(f.Target, "exports"));
    var before = await FileTrees.CaptureAsync(f.Target, default);
    foreach (var extra in new Dictionary<string, string>[]
    {
        new() { ["tools/custom.txt"] = "replacement" }, new() { ["exports"] = "not-a-directory" },
        new() { ["tools/custom.txt/child"] = "ancestor-collision" }
    }) await Refuses(() => new DeploymentService().InstallOrUpdateAsync(f.Package("0.2.1", extra).Request(f.Target)));
    FileTrees.Equal(before, await FileTrees.CaptureAsync(f.Target, default));
});
await Run("Zone.Identifier survives on user files and downloaded managed files permit updating", async f =>
{
    await f.Install("0.2.0");
    var user = f.WriteUser("data/history.txt", "user-history");
    const string zone = "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.invalid/fixture\r\n";
    File.WriteAllText(user + ":Zone.Identifier", zone);
    File.WriteAllText(Path.Combine(f.Target, "顺手工具箱.exe") + ":Zone.Identifier", zone);
    var result = await new DeploymentService().InstallOrUpdateAsync(f.Package("0.2.1").Request(f.Target));
    Check(File.ReadAllText(user + ":Zone.Identifier") == zone, "User download mark must survive byte-for-byte.");
    Check(File.ReadAllText(Path.Combine(result.PreviousBackupDirectory!, "顺手工具箱.exe") + ":Zone.Identifier") == zone, "Original download mark retained in backup.");
});
await Run("unknown alternate stream refuses before user metadata can be discarded", async f =>
{
    await f.Install("0.2.0");
    var user = f.WriteUser("data/history.txt", "user-history");
    File.WriteAllText(user + ":private-metadata", "must-survive");
    await Refuses(() => new DeploymentService().InstallOrUpdateAsync(f.Package("0.2.1").Request(f.Target)));
    Check(File.ReadAllText(user + ":private-metadata") == "must-survive" && await f.Version(f.Target) == "0.2.0", "ADS and old install untouched.");
});
await Run("cancel at commit boundary and late file mutation leave source unchanged", async f =>
{
    await f.Install("0.2.0");
    f.WriteUser("data/settings.txt", "settings");
    var before = await FileTrees.CaptureAsync(f.Target, default);
    using var cts = new CancellationTokenSource();
    try
    {
        await new DeploymentService().InstallOrUpdateAsync(f.Package("0.2.1").Request(f.Target) with { BeforeCommit = cts.Cancel }, ct: cts.Token);
        throw new Exception("Cancellation was ignored before commit.");
    }
    catch (OperationCanceledException) { }
    FileTrees.Equal(before, await FileTrees.CaptureAsync(f.Target, default));
    await Refuses(() => new DeploymentService().InstallOrUpdateAsync(f.Package("0.2.1").Request(f.Target) with
    { BeforeCommit = () => f.WriteUser("late-export.txt", "created-during-update") }));
    Check(await f.Version(f.Target) == "0.2.0" && File.ReadAllText(Path.Combine(f.Target, "late-export.txt")) == "created-during-update", "Late output not lost.");
});
await Run("failed switch rolls original folder back including user files", async f =>
{
    await f.Install("0.2.0");
    f.WriteUser("data/important.txt", "original");
    var before = await FileTrees.CaptureAsync(f.Target, default);
    var service = new DeploymentService(point => { if (point == DeploymentCheckpoint.OldMoved) throw new IOException("Fixture simulated rename denial."); });
    await Refuses(() => service.InstallOrUpdateAsync(f.Package("0.2.1").Request(f.Target)));
    FileTrees.Equal(before, await FileTrees.CaptureAsync(f.Target, default));
    Check((await new DeploymentService().RecoverAsync(f.Target)).Recovered == false, "Rollback journal is already finalized.");
});
await Run("cancellation after old rename cannot interrupt commit", async f =>
{
    await f.Install("0.2.0");
    using var cts = new CancellationTokenSource();
    var service = new DeploymentService(point => { if (point == DeploymentCheckpoint.OldMoved) cts.Cancel(); });
    var result = await service.InstallOrUpdateAsync(f.Package("0.2.1").Request(f.Target), ct: cts.Token);
    Check(await f.Version(f.Target) == "0.2.1" && Directory.Exists(result.PreviousBackupDirectory), "Commit must finish uncancelled.");
});
await Run("process crash after old rename recovers original without deleting stage", async f =>
{
    await f.Install("0.2.0");
    f.WriteUser("data/important.txt", "crash-preserved");
    var before = await FileTrees.CaptureAsync(f.Target, default);
    await Crash(f.Package("0.2.1"), f.Target, DeploymentCheckpoint.OldRenamedBeforeJournal);
    Check(!Directory.Exists(f.Target), "Crash must happen in actual rename gap.");
    var journal = f.Journal();
    Check(Directory.Exists(journal.Backup) && Directory.Exists(journal.Stage), "Both physical directories retained after crash.");
    var result = await new DeploymentService().RecoverAsync(f.Target);
    Check(result.Recovered && Directory.Exists(journal.Stage), "Recovery restores original and retains stage.");
    FileTrees.Equal(before, await FileTrees.CaptureAsync(f.Target, default));
    await new DeploymentService().InstallOrUpdateAsync(f.Package("0.2.1").Request(f.Target));
    Check(await f.Version(f.Target) == "0.2.1", "Can retry after recovery.");
});
await Run("process crash after activation finalizes new version and retained old backup", async f =>
{
    await f.Install("0.2.0");
    f.WriteUser("data/user.txt", "retained");
    await Crash(f.Package("0.2.1"), f.Target, DeploymentCheckpoint.NewActivatedBeforeJournal);
    var journal = f.Journal();
    Check(await f.Version(f.Target) == "0.2.1", "Crash happened after activation.");
    f.WriteUser("data/user.txt", "updated-after-activation");
    f.WriteUser("data/desktop-shortcut.json", "marker-created-after-activation");
    f.WriteUser("new-export.txt", "output-after-crash");
    var result = await new DeploymentService().RecoverAsync(f.Target);
    Check(result.Recovered && result.PreviousBackupDirectory == journal.Backup
        && await f.Version(journal.Backup) == "0.2.0", "Recover finalizes activation without deleting backup.");
    Check(File.ReadAllText(Path.Combine(f.Target, "data/user.txt")) == "updated-after-activation"
        && File.ReadAllText(Path.Combine(f.Target, "new-export.txt")) == "output-after-crash", "Recovery must keep post-activation user changes.");
});
await Run("tampered recovery paths and modified backup refuse without moving any data", async f =>
{
    await f.Install("0.2.0");
    f.WriteUser("data/user.txt", "original");
    await Crash(f.Package("0.2.1"), f.Target, DeploymentCheckpoint.OldRenamedBeforeJournal);
    var journal = f.Journal();
    var originalBytes = File.ReadAllBytes(PathSafety.JournalPath(f.Target));
    var outsider = Path.Combine(f.Root, "outside");
    Directory.CreateDirectory(outsider);
    File.WriteAllText(Path.Combine(outsider, "untouched.txt"), "outside");
    journal.Backup = outsider;
    File.WriteAllBytes(PathSafety.JournalPath(f.Target), JsonSerializer.SerializeToUtf8Bytes(journal));
    await Refuses(() => new DeploymentService().RecoverAsync(f.Target));
    Check(File.ReadAllText(Path.Combine(outsider, "untouched.txt")) == "outside" && !Directory.Exists(f.Target), "Forged path not moved.");
    File.WriteAllBytes(PathSafety.JournalPath(f.Target), originalBytes);
    journal = f.Journal();
    File.WriteAllText(Path.Combine(journal.Backup, "data/user.txt"), "changed-after-crash");
    await Refuses(() => new DeploymentService().RecoverAsync(f.Target));
    Check(!Directory.Exists(f.Target) && File.ReadAllText(Path.Combine(journal.Backup, "data/user.txt")) == "changed-after-crash", "Changed backup must not move automatically.");
});
await Run("directory junction in source and linked target ancestors are refused", async f =>
{
    await f.Install("0.2.0");
    var outside = Path.Combine(f.Root, "outside");
    Directory.CreateDirectory(outside);
    File.WriteAllText(Path.Combine(outside, "user.txt"), "outside-data");
    var junction = Path.Combine(f.Target, "linked-user-data");
    await MakeJunction(junction, outside);
    await Refuses(() => new DeploymentService().InstallOrUpdateAsync(f.Package("0.2.1").Request(f.Target)));
    await Refuses(() => new DeploymentService().InstallOrUpdateAsync(f.Package("0.2.1").Request(Path.Combine(junction, "new-app"))));
    Check(File.ReadAllText(Path.Combine(outside, "user.txt")) == "outside-data" && await f.Version(f.Target) == "0.2.0", "Junction targets untouched.");
});
await Run("custom restrictive root protected subtree and per-file access permissions survive update", async f =>
{
    await f.Install("0.2.0");
    var user = f.WriteUser("data/protected/user.txt", "permission-sensitive");
    var customFile = f.WriteUser("data/custom-file.txt", "custom-file-content");
    var sid = WindowsIdentity.GetCurrent().User!;
    var rootSecurity = new DirectorySecurity();
    rootSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
    rootSecurity.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
    new DirectoryInfo(f.Target).SetAccessControl(rootSecurity);
    var protectedDirectory = Path.GetDirectoryName(user)!;
    var protectedSecurity = new DirectorySecurity();
    protectedSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
    protectedSecurity.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
    new DirectoryInfo(protectedDirectory).SetAccessControl(protectedSecurity);
    var fileSecurity = new FileSecurity();
    fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
    fileSecurity.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
    new FileInfo(customFile).SetAccessControl(fileSecurity);
    var before = await FileTrees.CaptureAsync(f.Target, default);
    var result = await new DeploymentService().InstallOrUpdateAsync(f.Package("0.2.1").Request(f.Target));
    var after = await FileTrees.CaptureAsync(f.Target, default);
    Check(before.RootAccessSddl == after.RootAccessSddl, "Root DACL and protection must be identical.");
    foreach (var directory in before.Directories)
        Check(directory.AccessSddl == after.Directories.Single(d => d.Path == directory.Path).AccessSddl, "Original directory permissions unchanged: " + directory.Path);
    foreach (var file in before.Files.Where(file => file.Path.StartsWith("data/")))
        Check(file.AccessSddl == after.Files.Single(d => d.Path == file.Path).AccessSddl, "User file permissions unchanged: " + file.Path);
    FileTrees.Equal(before, await FileTrees.CaptureAsync(result.PreviousBackupDirectory!, default));
    var newRules = new FileInfo(Path.Combine(f.Target, "顺手工具箱.exe")).GetAccessControl(AccessControlSections.Access)
        .GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
    Check(newRules.Count > 0 && newRules.All(rule => rule.IdentityReference == sid && rule.IsInherited), "New managed files inherit restricted original root policy.");
    Check(File.ReadAllText(user) == "permission-sensitive" && File.ReadAllText(customFile) == "custom-file-content", "Restricted user content preserved.");
});
await Run("fresh child with legitimate explicit default DACL supports initial install and update", async f =>
{
    var security = new DirectorySecurity();
    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
    security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
        InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
    new DirectoryInfo(f.Root).SetAccessControl(security);
    var child = Path.Combine(f.Root, "default-child");
    Directory.CreateDirectory(child);
    var childSecurity = new DirectoryInfo(child).GetAccessControl(AccessControlSections.Access);
    var rules = childSecurity.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
    Check(!childSecurity.AreAccessRulesProtected && rules.Any(rule => !rule.IsInherited), "Fixture must reproduce normal new child explicit ACEs.");
    await f.Install("0.2.0");
    f.WriteUser("data/user.txt", "default-dacl-user-content");
    var before = await FileTrees.CaptureAsync(f.Target, default);
    var result = await new DeploymentService().InstallOrUpdateAsync(f.Package("0.2.1").Request(f.Target));
    Check(AccessPermissions.Equivalent(before.RootAccessSddl, AccessPermissions.Read(f.Target, true)), "Default root DACL preserved.");
    FileTrees.Equal(before, await FileTrees.CaptureAsync(result.PreviousBackupDirectory!, default));
    Check(await f.Version(f.Target) == "0.2.1" && File.ReadAllText(Path.Combine(f.Target, "data/user.txt")) == "default-dacl-user-content", "Default explicit ACL deployment succeeds.");
});
await Run("legacy journal without DACL fields recovers folders without changing permissions", async f =>
{
    await f.Install("0.2.0");
    f.WriteUser("data/legacy.txt", "legacy-data");
    var before = await FileTrees.CaptureAsync(f.Target, default);
    await Crash(f.Package("0.2.1"), f.Target, DeploymentCheckpoint.OldRenamedBeforeJournal);
    var journal = f.Journal();
    static TreeSnapshot Legacy(TreeSnapshot tree) => new(tree.Files.Select(file => file with { AccessSddl = null }).ToList(),
        tree.Directories.Select(dir => dir with { AccessSddl = null }).ToList());
    journal.OldTree = Legacy(journal.OldTree!);
    journal.NewTree = Legacy(journal.NewTree!);
    File.WriteAllBytes(PathSafety.JournalPath(f.Target), JsonSerializer.SerializeToUtf8Bytes(journal));
    Check((await new DeploymentService().RecoverAsync(f.Target)).Recovered, "Legacy recovery should succeed.");
    FileTrees.Equal(before, await FileTrees.CaptureAsync(f.Target, default));
});
await Run("oversized journal is refused by writer before it can become unrecoverable", f =>
{
    var path = string.Join('/', Enumerable.Repeat(new string('测', 100), 8));
    var files = Enumerable.Range(0, 16_000).Select(i => new FileSnapshot(path + "/" + i, 0, new string('0', 64), DateTime.UtcNow, FileAttributes.Normal)).ToList();
    var tree = new TreeSnapshot(files, []);
    var journal = new DeploymentJournal { OldTree = tree, NewTree = tree };
    try { DeploymentService.SerializeJournal(journal); }
    catch (IOException)
    {
        Check(!Directory.Exists(f.Target), "Synthetic journal test must not create an install.");
        return Task.CompletedTask;
    }
    throw new Exception("Writer accepted journal exceeding reader limit.");
});

Directory.CreateDirectory(output);
var report = new { Passed = results.Count - failures, Failed = failures, FixtureRoot = run, Results = results };
await File.WriteAllTextAsync(Path.Combine(output, "deployment-verification.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Deployment verification: {results.Count - failures} passed, {failures} failed. Fixtures: {run}");
return failures == 0 ? 0 : 1;

async Task Run(string name, Func<Fixture, Task> test)
{
    var fixture = new Fixture(Path.Combine(run, (results.Count + 1).ToString("D2")));
    try { await test(fixture); results.Add(new { Name = name, Passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; results.Add(new { Name = name, Passed = false, Error = ex.ToString() }); Console.WriteLine("FAIL " + name + "\n" + ex); }
}

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Refuses(Func<Task> action)
{
    try { await action(); }
    catch (IOException) { return; }
    catch (InvalidDataException) { return; }
    throw new Exception("Expected conservative refusal, operation succeeded.");
}

static async Task Crash(Package package, string target, DeploymentCheckpoint checkpoint)
{
    var executable = Environment.ProcessPath ?? throw new Exception("No current executable.");
    var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    foreach (var arg in new[] { "--crash-child", package.Path, package.Sha, target, checkpoint.ToString() }) info.ArgumentList.Add(arg);
    using var child = Process.Start(info) ?? throw new Exception("Failed to start fixture child.");
    var error = child.StandardError.ReadToEndAsync();
    var output = child.StandardOutput.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
    await child.WaitForExitAsync(timeout.Token);
    if (child.ExitCode != 86) throw new Exception($"Crash child exit {child.ExitCode}: {await error}\n{await output}");
}

static async Task MakeJunction(string path, string target)
{
    // mklink only creates a junction between these test-generated absolute paths; no deletion is performed.
    if (path.IndexOfAny(['"', '%', '\r', '\n']) >= 0 || target.IndexOfAny(['"', '%', '\r', '\n']) >= 0) throw new Exception("Unexpected fixture path.");
    var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    info.Arguments = $"/d /v:off /c mklink /J \"{path}\" \"{target}\"";
    using var process = Process.Start(info) ?? throw new Exception("Could not create fixture junction.");
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new Exception("Fixture junction failed: " + await process.StandardError.ReadToEndAsync() + await process.StandardOutput.ReadToEndAsync());
}

sealed record Package(string Path, string Sha)
{
    internal DeploymentRequest Request(string target) => new(Path, Sha, target);
}

sealed class Fixture
{
    internal string Root { get; }
    internal string Target => Path.Combine(Root, "顺手工具箱");
    internal Fixture(string root) { Root = root; Directory.CreateDirectory(root); }
    internal Package Package(string version, Dictionary<string, string>? extra = null,
        Action<PackageManifest>? modifyManifest = null, Func<byte[], byte[]>? manifestTransform = null,
        string? unlisted = null, bool symlink = false, bool includeEntryResource = true)
    {
        var executable = System.Version.Parse(version) >= new System.Version(0, 2, 2)
            ? PackageContent.CurrentExecutableName : PackageContent.LegacyExecutableName;
        var content = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [executable] = "fixture-executable-" + version,
            ["Shunshou.App.dll"] = "fixture-app-" + version,
            ["Shunshou.Core.dll"] = "fixture-core-" + version,
            ["tools/ocr/model.onnx"] = "fixture-stable-model"
        };
        if (System.Version.Parse(version) >= new System.Version(0, 3, 0))
        {
            content = new(StringComparer.Ordinal) {
                [executable] = "fixture-native-launcher-" + version,
                ["app/Shunshou.App.exe"] = "fixture-apphost-" + version,
                ["app/Shunshou.App.dll"] = "fixture-app-" + version,
                ["app/Shunshou.Core.dll"] = "fixture-core-" + version,
                ["app/tools/ocr/model.onnx"] = "fixture-stable-model"
            };
            if (includeEntryResource) content.Add("app/Shunshou.App.pri", "fixture-pri-" + version);
        }
        else if (includeEntryResource) content.Add(System.IO.Path.ChangeExtension(executable, ".pri"), "fixture-pri-" + version);
        if (extra is not null) foreach (var pair in extra) content.Add(pair.Key, pair.Value);
        var files = content.Select(kv => new PackageFile(kv.Key, Encoding.UTF8.GetByteCount(kv.Value), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kv.Value))))).ToList();
        var manifest = new PackageManifest("顺手工具箱", version, "win-x64", files);
        modifyManifest?.Invoke(manifest);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        if (manifestTransform is not null) manifestBytes = manifestTransform(manifestBytes);
        var path = Path.Combine(Root, "package-" + Guid.NewGuid().ToString("N") + ".zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var pair in content)
            {
                var entry = archive.CreateEntry("ShunshouToolbox-" + version + "/" + pair.Key, CompressionLevel.Optimal);
                if (symlink && pair.Key == "Shunshou.App.dll") entry.ExternalAttributes = (0xA000 | 0x1FF) << 16;
                using var output = entry.Open(); output.Write(Encoding.UTF8.GetBytes(pair.Value));
            }
            using (var output = archive.CreateEntry("ShunshouToolbox-" + version + "/package-manifest.json").Open()) output.Write(manifestBytes);
            if (unlisted is not null) using (var output = archive.CreateEntry("ShunshouToolbox-" + version + "/" + unlisted).Open()) output.WriteByte(1);
        }
        return new(path, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }
    internal Task<DeploymentResult> Install(string version) => new DeploymentService().InstallOrUpdateAsync(Package(version).Request(Target));
    internal string WriteUser(string relative, string text)
    {
        var path = Path.Combine(Target, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }
    internal async Task<string> Version(string root)
    {
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "package-manifest.json")));
        return manifest.RootElement.GetProperty("Version").GetString()!;
    }
    internal DeploymentJournal Journal() => JsonSerializer.Deserialize<DeploymentJournal>(File.ReadAllBytes(PathSafety.JournalPath(Target)))!;
}
