using System.Text.Json;

namespace Shunshou.Core;

public enum AppearanceTheme { Light, Dark, System }

/// <summary>Portable, optional appearance preference. A missing or damaged file means use the system theme.</summary>
public static class AppearancePreferences
{
    public static string FilePath => Path.Combine(AppPaths.DataDirectory, "appearance.json");

    public static AppearanceTheme? Load(string? filePath = null)
    {
        try
        {
            using var input = new FileStream(filePath ?? FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length > 4096) return null;
            using var document = JsonDocument.Parse(input);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var value) || value != 1 ||
                !root.TryGetProperty("theme", out var theme) || theme.ValueKind != JsonValueKind.String) return null;
            return theme.GetString() switch { "light" => AppearanceTheme.Light, "dark" => AppearanceTheme.Dark, "system" => AppearanceTheme.System, _ => null };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                      ArgumentException or NotSupportedException or InvalidOperationException)
        { return null; }
    }

    /// <summary>Writes beside the destination, then atomically replaces it. Recovery mode never saves preferences.</summary>
    public static bool TrySave(AppearanceTheme theme, string? filePath = null)
    {
        if (AppPaths.LoggingDisabled || !Enum.IsDefined(theme)) return false;
        string? temporary = null;
        try
        {
            var destination = Path.GetFullPath(filePath ?? FilePath);
            var directory = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, ".appearance-" + Guid.NewGuid().ToString("N") + ".tmp");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1,
                theme = theme switch
                {
                    AppearanceTheme.Dark => "dark",
                    AppearanceTheme.System => "system",
                    _ => "light"
                }
            });
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
            temporary = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
