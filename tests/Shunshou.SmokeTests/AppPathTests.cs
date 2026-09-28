using System.Text.Json;
using Shunshou.Core;

namespace Shunshou.SmokeTests;

public static class AppPathTests
{
    public static Task RunAsync(string root)
    {
        var fixture = Path.Combine(root, "app-paths-" + Guid.NewGuid().ToString("N"));
        var runtime = Path.Combine(fixture, "中文 工具箱", "app");
        Directory.CreateDirectory(runtime);
        var package = Path.GetDirectoryName(runtime)!;
        Require(AppPaths.ResolveInstallationDirectory(runtime) == runtime, "An arbitrary app directory does not select its parent.");
        File.WriteAllText(Path.Combine(package, "ShunshouToolbox.exe"), "inert fixture");
        string[] files = ["ShunshouToolbox.exe", "app/Shunshou.App.exe", "app/Shunshou.App.dll"];
        void WriteManifest(string product, string[] paths) => File.WriteAllText(Path.Combine(package, "package-manifest.json"),
            JsonSerializer.Serialize(new { Product = product, Architecture = "win-x64", Version = "0.3.0", Files = paths.Select(path => new { Path = path }).ToArray() }));
        WriteManifest("Other app", files);
        Require(AppPaths.ResolveInstallationDirectory(runtime) == runtime, "Unrelated parent manifest is ignored.");
        WriteManifest("顺手工具箱", ["ShunshouToolbox.exe", "Shunshou.App.exe", "Shunshou.App.dll"]);
        Require(AppPaths.ResolveInstallationDirectory(runtime) == runtime, "A legacy parent manifest does not claim an unrelated nested app folder.");
        WriteManifest("顺手工具箱", files);
        Require(AppPaths.ResolveInstallationDirectory(runtime + Path.DirectorySeparatorChar) == package, "Clean manifest locates the portable root with a trailing separator.");
        Require(AppPaths.ResolveInstallationDirectory(package) == package, "Flat/development layout retains its own directory.");
        File.WriteAllText(Path.Combine(package, "package-manifest.json"), "{broken");
        Require(AppPaths.ResolveInstallationDirectory(runtime) == runtime, "Malformed parent manifest falls back safely.");
        Require(Path.GetFileName(AppPaths.DataDirectory) == "data" && Path.GetDirectoryName(AppPaths.DataDirectory) == AppPaths.InstallationDirectory,
            "Data directory is a sibling of the runtime, under the selected installation root.");
        Console.WriteLine("PASS portable/development app paths and unrelated or corrupt manifest rejection");
        return Task.CompletedTask;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
