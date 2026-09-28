using System.Text.Json;

namespace Shunshou.Core;

/// <summary>Remembers which one-time usage notices the user has closed. A missing or damaged file shows every notice once.</summary>
public static class NoticePreferences
{
    private static string FilePath => Path.Combine(AppPaths.DataDirectory, "notices.json");

    public static bool IsDismissed(string key) => Load().Contains(key);

    public static void Dismiss(string key)
    {
        if (AppPaths.LoggingDisabled) return;
        var set = Load();
        if (set.Count >= 32 || !set.Add(key)) return;
        string? temporary = null;
        try
        {
            var destination = Path.GetFullPath(FilePath);
            var directory = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, ".notices-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1,
                dismissed = set.OrderBy(k => k, StringComparer.Ordinal).ToArray()
            }));
            File.Move(temporary, destination, overwrite: true);
            temporary = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static HashSet<string> Load()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length > 16384) return [];
            using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("dismissed", out var list) && list.ValueKind == JsonValueKind.Array)
                return new HashSet<string>(list.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(s => s.Length is > 0 and <= 64), StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new HashSet<string>(StringComparer.Ordinal);
    }
}
