namespace Shunshou.Deployment;

public sealed record DeploymentProgress(string Message, int? Percent);

public sealed record DeploymentInspection(bool IsUpdate, string? CurrentVersion, string Version,
    long PreservedBytes, int PreservedFiles, long RequiredFreeBytes);

public sealed record DeploymentRequest(string ZipPath, string ExpectedZipSha256, string TargetDirectory)
{
    public string? ExpectedVersion { get; init; }
    public string Architecture { get; init; } = "win-x64";
    public string? ZipRoot { get; init; }
    public Action? BeforeCommit { get; init; }
}

public sealed record DeploymentResult(string TargetDirectory, string Version,
    string? PreviousBackupDirectory, string JournalPath, bool WasUpdate);

public sealed record DeploymentRecoveryResult(bool Recovered, string Message, string? PreviousBackupDirectory);

internal enum DeploymentCheckpoint { Prepared, OldRenamedBeforeJournal, OldMoved, NewActivatedBeforeJournal }

internal sealed record PackageFile(string Path, long Bytes, string Sha256);
internal sealed record PackageManifest(string Product, string Version, string Architecture, List<PackageFile> Files);
internal sealed record FileSnapshot(string Path, long Bytes, string Sha256, DateTime LastWriteUtc, FileAttributes Attributes,
    string? ZoneIdentifierSha256 = null, long ZoneIdentifierBytes = 0, string? AccessSddl = null);
internal sealed record DirectorySnapshot(string Path, DateTime LastWriteUtc, FileAttributes Attributes, string? AccessSddl = null);
internal sealed record TreeSnapshot(List<FileSnapshot> Files, List<DirectorySnapshot> Directories, string? RootAccessSddl = null);

internal sealed class DeploymentJournal
{
    public int Format { get; set; } = 1;
    public string Product { get; set; } = "顺手工具箱";
    public string Transaction { get; set; } = "";
    public string Target { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Backup { get; set; } = "";
    public string State { get; set; } = "Staging";
    public bool HadTarget { get; set; }
    public bool WasUpdate { get; set; }
    public string Version { get; set; } = "";
    public string Architecture { get; set; } = "win-x64";
    public string? OldVersion { get; set; }
    public TreeSnapshot? OldTree { get; set; }
    public TreeSnapshot? NewTree { get; set; }
}
