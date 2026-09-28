using System.Text;
using System.Text.Json;

namespace Shunshou.Core;

/// <summary>One copyable symbol with its Chinese label and Unicode code points.</summary>
public sealed record CharacterEntry(string Value, string Name, string Code, string GroupTitle)
{
    public string Display => Value;
    public string Tooltip => $"{Name} · {Code} · 点击复制";
    public string AutomationName => $"复制{Name}，{Value}，{Code}";
}

public sealed record CharacterGroup(string Title, string Level, IReadOnlyList<CharacterEntry> Entries);

/// <summary>
/// Ordered special-character catalogue for daily office writing: common first, specialised last.
/// The catalogue is plain data shipped with the app, so a damaged file can never break other tools.
/// </summary>
public sealed class CharacterLibrary
{
    public const int SupportedVersion = 1;
    public const string DefaultFileName = "SpecialCharacters.json";

    private readonly List<CharacterGroup> _groups;

    private CharacterLibrary(List<CharacterGroup> groups) => _groups = groups;

    public IReadOnlyList<CharacterGroup> Groups => _groups;
    public int GroupCount => _groups.Count;
    public int CharacterCount => _groups.Sum(group => group.Entries.Count);

    public static string DefaultAssetPath => Path.Combine(AppContext.BaseDirectory, "Assets", DefaultFileName);

    public static CharacterLibrary Load(string? assetPath = null) => Parse(File.ReadAllText(assetPath ?? DefaultAssetPath));

    public static CharacterLibrary Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("字符库文件不是有效的对象。");
        if (!root.TryGetProperty("version", out var version) || !version.TryGetInt32(out int value) || value != SupportedVersion)
            throw new InvalidDataException("字符库版本不受支持。");
        if (!root.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("字符库缺少分组。");
        var parsed = new List<CharacterGroup>();
        foreach (var group in groups.EnumerateArray())
        {
            string title = ReadString(group, "title");
            string level = ReadString(group, "level");
            string items = ReadString(group, "items");
            var entries = new List<CharacterEntry>();
            foreach (string item in items.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int separator = item.IndexOf('=');
                if (separator <= 0) continue;
                string symbol = item[..separator];
                string name = item[(separator + 1)..].Trim();
                if (symbol.Length == 0 || name.Length == 0) continue;
                entries.Add(new CharacterEntry(symbol, name, DescribeCodePoints(symbol), title));
            }
            if (entries.Count > 0) parsed.Add(new CharacterGroup(title, level, entries));
        }
        if (parsed.Count == 0 || parsed.Sum(group => group.Entries.Count) == 0) throw new InvalidDataException("字符库没有任何可用字符。");
        return new CharacterLibrary(parsed);
    }

    /// <summary>Filters by character, Chinese name, Unicode code point, or group name. An empty query keeps everything.</summary>
    public IReadOnlyList<CharacterGroup> Filter(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return _groups;
        string trimmed = query.Trim();
        var filtered = new List<CharacterGroup>();
        foreach (var group in _groups)
        {
            var entries = group.Entries.Where(entry => Matches(entry, trimmed)).ToList();
            if (entries.Count > 0) filtered.Add(new CharacterGroup(group.Title, group.Level, entries));
        }
        return filtered;
    }

    private static bool Matches(CharacterEntry entry, string query) =>
        entry.Value.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        entry.Code.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        entry.GroupTitle.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>Names every code point, so a copied symbol can be identified even when the font shows a different glyph.</summary>
    public static string DescribeCodePoints(string value)
    {
        var builder = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (builder.Length > 0) builder.Append(' ');
            builder.Append("U+").Append(rune.Value.ToString("X4"));
        }
        return builder.ToString();
    }
}
