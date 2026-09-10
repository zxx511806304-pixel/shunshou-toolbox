using Microsoft.Win32;

namespace Shunshou.Core;

public enum InstalledApplicationKind { Desktop, Store }
public enum LeftoverKind { Directory, RegistryKey }
public enum LeftoverConfidence { ExactRegistration, ExactLocation, ExactName }

public sealed record RegistryIdentity(RegistryHive Hive, RegistryView View, string SubKey)
{
    public override string ToString() => $"{(Hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM")} [{(View == RegistryView.Registry64 ? "64" : "32")} 位]\\{SubKey}";
}

public sealed record InstalledApplication(string Id, string DisplayName, string Version, string Publisher,
    long EstimatedSizeBytes, string? IconPath, string? InstallLocation, string? UninstallCommand,
    bool RequiresElevation, InstalledApplicationKind Kind, RegistryIdentity? RegistryIdentity = null,
    string? PackageFullName = null);

public sealed record UninstallCommand(string ExecutablePath, IReadOnlyList<string> Arguments);
public sealed record UninstallResult(int? ExitCode, bool StillInstalled, string Message);
public sealed record LeftoverItem(string Id, LeftoverKind Kind, string Path, long SizeBytes,
    LeftoverConfidence Confidence, string Reason, bool RequiresElevation);
public sealed record LeftoverScan(string ScanId, InstalledApplication Application, IReadOnlyList<LeftoverItem> Items,
    IReadOnlyList<string> SkippedReasons);
public sealed record CleanupResult(string BackupManifestPath, int RemovedCount, IReadOnlyList<string> Errors);
public sealed record RestoreResult(int RestoredCount, IReadOnlyList<string> Errors);

// The platform seam permits tests to exercise real file handling with an isolated in-memory registry
// and an injected process executor. No test needs to start a genuine uninstall program.
public interface IUninstallPlatform
{
    Task<IReadOnlyList<InstalledApplication>> ListAsync(CancellationToken ct);
    Task<int?> RunUninstallerAsync(UninstallCommand command, bool elevated, CancellationToken ct);
    Task RemoveStorePackageAsync(string packageFullName, CancellationToken ct);
    RegistrySnapshot? ReadRegistry(RegistryIdentity identity);
    void DeleteRegistry(RegistryIdentity identity);
    void RestoreRegistry(RegistryIdentity identity, RegistrySnapshot snapshot);
}

public sealed record RegistryValueSnapshot(string Name, RegistryValueKind Kind, string Data);
public sealed record RegistrySnapshot(IReadOnlyList<RegistryValueSnapshot> Values,
    IReadOnlyDictionary<string, RegistrySnapshot> SubKeys);

public sealed record UninstallEnvironment(string BackupDirectory, IReadOnlyList<string> ApplicationDataRoots,
    IReadOnlyList<string> ProtectedDirectories, IReadOnlyList<string> ProtectedTrees)
{
    public static UninstallEnvironment Default => new(
        System.IO.Path.Combine(AppContext.BaseDirectory, "data", "uninstall-backups"),
        [Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
         Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
         Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)],
        [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
         Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
         Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
         Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
         Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
         Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)],
        [Environment.GetFolderPath(Environment.SpecialFolder.Windows),
         Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
         Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
         Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
         Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
         Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
         System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
         AppContext.BaseDirectory]);
}
