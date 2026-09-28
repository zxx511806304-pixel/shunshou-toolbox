using System.Text.Json;

namespace Shunshou.Core;

/// <summary>Separates the portable package root and user data from the private runtime folder.</summary>
public static class AppPaths
{
    private static int _loggingDisabled;
    public static bool LoggingDisabled => Volatile.Read(ref _loggingDisabled) != 0;
    /// <summary>Recovery disables application log writes for the remainder of this process.</summary>
    public static void DisableLogging() => Interlocked.Exchange(ref _loggingDisabled, 1);
    public static string InstallationDirectory { get; } = ResolveInstallationDirectory(AppContext.BaseDirectory);
    public static string DataDirectory => Path.Combine(InstallationDirectory, "data");
    public static string LauncherPath => File.Exists(Path.Combine(InstallationDirectory, "ShunshouToolbox.exe"))
        ? Path.Combine(InstallationDirectory, "ShunshouToolbox.exe")
        : Environment.ProcessPath ?? Path.Combine(InstallationDirectory, "Shunshou.App.exe");

    public static string ResolveInstallationDirectory(string runtimeDirectory)
    {
        var runtime = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeDirectory));
        var folder = new DirectoryInfo(runtime);
        if (!folder.Name.Equals("app", StringComparison.OrdinalIgnoreCase) || folder.Parent is null) return runtime;
        var root = folder.Parent.FullName;
        try
        {
            var path = Path.Combine(root, "package-manifest.json");
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 8 * 1024 * 1024) return runtime;
            using var input = File.OpenRead(path);
            using var document = JsonDocument.Parse(input);
            var manifest = document.RootElement;
            if (manifest.GetProperty("Product").GetString() != "顺手工具箱" ||
                manifest.GetProperty("Architecture").GetString() != "win-x64") return runtime;
            var files = manifest.GetProperty("Files").EnumerateArray().Select(file => file.GetProperty("Path").GetString())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (files.Contains("ShunshouToolbox.exe") && files.Contains("app/Shunshou.App.exe") &&
                files.Contains("app/Shunshou.App.dll") && File.Exists(Path.Combine(root, "ShunshouToolbox.exe"))) return root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                      InvalidOperationException or KeyNotFoundException or ArgumentException or NotSupportedException)
        { /* A build folder or unrelated directory named app is not a portable installation. */ }
        return runtime;
    }
}
