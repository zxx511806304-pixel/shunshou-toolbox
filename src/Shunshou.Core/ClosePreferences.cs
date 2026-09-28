using System.Text.Json;

namespace Shunshou.Core;

/// <summary>Remembers whether the user chose to minimize to tray or exit on close.</summary>
public static class ClosePreferences
{
    private static string FilePath => Path.Combine(AppPaths.DataDirectory, "close-pref.json");

    /// <summary>null = ask every time; true = minimize to tray; false = exit directly.</summary>
    public static bool? Preference { get; private set; }

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length > 1024) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("minimize", out var val))
            {
                if (val.ValueKind == JsonValueKind.False) Preference = false;
                else if (val.ValueKind == JsonValueKind.True) Preference = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public static void Save(bool? minimize)
    {
        Preference = minimize;
        if (AppPaths.LoggingDisabled) return;
        string? temporary = null;
        try
        {
            var destination = Path.GetFullPath(FilePath);
            var directory = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, ".close-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1,
                minimize = minimize
            }));
            File.Move(temporary, destination, overwrite: true);
            temporary = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
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
