using System.Text.Json;

namespace Shunshou.Core;

/// <summary>
/// Pinned clipboard text snippets, persisted as JSON in the data directory.
/// A missing or damaged file simply means nothing is pinned.
/// </summary>
public sealed class ClipboardPinStore
{
    private const int MaxPins = 100;
    private const int MaxTextLength = 100_000;
    private const int MaxFileBytes = 4 * 1024 * 1024;
    private readonly List<string> _pins = [];

    public static string FilePath => Path.Combine(AppPaths.DataDirectory, "clipboard-pins.json");

    /// <summary>Newest pin first.</summary>
    public IReadOnlyList<string> Pins => _pins;

    public bool Contains(string text) => _pins.Contains(text, StringComparer.Ordinal);

    public static ClipboardPinStore Load(string? filePath = null)
    {
        var store = new ClipboardPinStore();
        try
        {
            using var input = new FileStream(filePath ?? FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length > MaxFileBytes) return store;
            using var document = JsonDocument.Parse(input);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return store;
            if (root.TryGetProperty("version", out var version) && version.TryGetInt32(out int value) && value != 1) return store;
            if (!root.TryGetProperty("pins", out var pins) || pins.ValueKind != JsonValueKind.Array) return store;
            foreach (var item in pins.EnumerateArray())
            {
                string? text = item.GetString();
                if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength) continue;
                if (store._pins.Contains(text, StringComparer.Ordinal)) continue;
                store._pins.Add(text);
                if (store._pins.Count >= MaxPins) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                      ArgumentException or NotSupportedException or InvalidOperationException)
        { /* An unreadable pin file just means "nothing pinned yet". */ }
        return store;
    }

    /// <summary>Pins the text (or promotes it to the front when already pinned) and saves.</summary>
    public bool Add(string text, string? filePath = null)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength) return false;
        _pins.Remove(text);
        _pins.Insert(0, text);
        while (_pins.Count > MaxPins) _pins.RemoveAt(_pins.Count - 1);
        return TrySave(filePath);
    }

    /// <summary>Unpins the text and saves. Unknown text counts as success.</summary>
    public bool Remove(string text, string? filePath = null)
    {
        if (!_pins.Remove(text)) return true;
        return TrySave(filePath);
    }

    public bool TrySave(string? filePath = null)
    {
        if (AppPaths.LoggingDisabled) return false;
        string? temporary = null;
        try
        {
            string destination = Path.GetFullPath(filePath ?? FilePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".clip-pins-" + Guid.NewGuid().ToString("N") + ".tmp");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1,
                pins = _pins.ToArray()
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
