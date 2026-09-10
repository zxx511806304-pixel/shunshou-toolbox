using System.Security.Cryptography;
using System.Text.Json;

namespace Shunshou.DesktopIntegration;

/// <summary>Identifies a complete application entry point; the updater validates the full payload separately.</summary>
public static class PackageIdentity
{
    public const string ProductName = "顺手工具箱";
    public const string ExecutableName = "ShunshouToolbox.exe";
    public const string LegacyExecutableName = "顺手工具箱.exe";
    public const string ManifestName = "package-manifest.json";

    public static bool IsValidDirectory(string? directory) => GetExecutablePath(directory) is not null;

    public static bool IsSupportedExecutableName(string? name) =>
        string.Equals(name, ExecutableName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, LegacyExecutableName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the manifest-verified entry point, including the Chinese executable in older packages.</summary>
    public static string? GetExecutablePath(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        try
        {
            var root = Path.GetFullPath(directory);
            if (!Path.IsPathFullyQualified(directory)) return null;
            var manifestPath = Path.Combine(root, ManifestName);
            var manifestInfo = new FileInfo(manifestPath);
            if (!manifestInfo.Exists || manifestInfo.Length > 8 * 1024 * 1024) return null;
            using var stream = File.OpenRead(manifestPath);
            using var document = JsonDocument.Parse(stream);
            var manifest = document.RootElement;
            if (manifest.GetProperty("Product").GetString() != ProductName ||
                manifest.GetProperty("Architecture").GetString() != "win-x64" ||
                !Version.TryParse(manifest.GetProperty("Version").GetString(), out _)) return null;
            var entries = manifest.GetProperty("Files").EnumerateArray().ToArray();
            if (!MatchesFile(root, "Shunshou.App.dll", entries)) return null;
            // A current entry listed by the manifest must validate. Never fall back to an older
            // binary when a partially updated or modified current executable is present.
            var executableName = entries.Any(entry => string.Equals(entry.GetProperty("Path").GetString(), ExecutableName, StringComparison.OrdinalIgnoreCase))
                ? ExecutableName : LegacyExecutableName;
            return MatchesFile(root, executableName, entries) ? Path.Combine(root, executableName) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                      InvalidOperationException or KeyNotFoundException or ArgumentException or
                                      NotSupportedException or FormatException or System.Security.SecurityException)
        { return null; }
    }

    private static bool MatchesFile(string root, string name, JsonElement[] entries)
    {
        var matches = entries.Where(entry => string.Equals(entry.GetProperty("Path").GetString(), name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) return false;
        var path = Path.Combine(root, name);
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists || (fileInfo.Attributes & FileAttributes.ReparsePoint) != 0 ||
            fileInfo.Length != matches[0].GetProperty("Bytes").GetInt64()) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), matches[0].GetProperty("Sha256").GetString(), StringComparison.OrdinalIgnoreCase);
    }
}
