using System.Text.Json;

namespace Shunshou.Core;

/// <summary>
/// Which tools the user used last. A missing or damaged file simply means an empty list,
/// and nothing here is required for the toolbox to work offline.
/// </summary>
public sealed class ToolUsagePreferences
{
    private const int MaxRecent = 8;
    private readonly List<string> _recent = [];

    public static string FilePath => Path.Combine(AppPaths.DataDirectory, "tool-usage.json");

    public IReadOnlyList<string> Recent => _recent;

    public static ToolUsagePreferences Load(string? filePath = null)
    {
        var preferences = new ToolUsagePreferences();
        try
        {
            using var input = new FileStream(filePath ?? FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length > 32 * 1024) return preferences;
            using var document = JsonDocument.Parse(input);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return preferences;
            if (root.TryGetProperty("version", out var version) && version.TryGetInt32(out int value) && value != 1) return preferences;
            Read(root, "recent", preferences._recent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                      ArgumentException or NotSupportedException or InvalidOperationException)
        { /* An unreadable preference file just means "no history yet". */ }
        return preferences;

        static void Read(JsonElement root, string name, List<string> target)
        {
            if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array) return;
            foreach (var item in element.EnumerateArray())
            {
                string? text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text) && text.Length <= 200 && !target.Contains(text, StringComparer.Ordinal)) target.Add(text);
            }
        }
    }

    public void RecordRecent(string tool)
    {
        if (string.IsNullOrWhiteSpace(tool)) return;
        _recent.Remove(tool);
        _recent.Insert(0, tool);
        while (_recent.Count > MaxRecent) _recent.RemoveAt(_recent.Count - 1);
        TrySave();
    }

    public bool TrySave(string? filePath = null)
    {
        if (AppPaths.LoggingDisabled) return false;
        string? temporary = null;
        try
        {
            string destination = Path.GetFullPath(filePath ?? FilePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".tool-usage-" + Guid.NewGuid().ToString("N") + ".tmp");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1,
                recent = _recent.ToArray()
            });
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
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
