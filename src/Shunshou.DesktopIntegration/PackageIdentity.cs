using System.Security.Cryptography;
using System.Text.Json;

namespace Shunshou.DesktopIntegration;

/// <summary>Identifies a complete application entry point; the updater validates the full payload separately.</summary>
public static class PackageIdentity
{
    public const string ProductName = "顺手工具箱";
    public const string ExecutableName = "顺手工具箱.exe";
    public const string ManifestName = "package-manifest.json";

    public static bool IsValidDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;
        try
        {
            var root = Path.GetFullPath(directory);
            if (!Path.IsPathFullyQualified(directory)) return false;
            var manifestPath = Path.Combine(root, ManifestName);
            var manifestInfo = new FileInfo(manifestPath);
            if (!manifestInfo.Exists || manifestInfo.Length > 8 * 1024 * 1024) return false;
            using var stream = File.OpenRead(manifestPath);
            using var document = JsonDocument.Parse(stream);
            var manifest = document.RootElement;
            if (manifest.GetProperty("Product").GetString() != ProductName ||
                manifest.GetProperty("Architecture").GetString() != "win-x64" ||
                !Version.TryParse(manifest.GetProperty("Version").GetString(), out _)) return false;
            var entries = manifest.GetProperty("Files").EnumerateArray().ToArray();
            return MatchesFile(root, ExecutableName, entries) && MatchesFile(root, "Shunshou.App.dll", entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                      InvalidOperationException or KeyNotFoundException or ArgumentException or
                                      NotSupportedException or FormatException or System.Security.SecurityException)
        { return false; }
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
