using Shunshou.Core;

namespace Shunshou.SmokeTests;

public static class CharacterLibraryTests
{
    public static Task RunAsync(string root)
    {
        var library = CharacterLibrary.Load(ShippedAssetPath());
        CompressionTests.Check(library.GroupCount == 22 && library.CharacterCount == 557,
            $"shipped catalogue keeps all 22 groups and 557 characters, saw {library.GroupCount}/{library.CharacterCount}");
        foreach (var group in library.Groups)
        {
            CompressionTests.Check(group.Title.Length > 0 && group.Level.Length > 0 && group.Entries.Count > 0, "every group carries a title, a usage level and entries");
            var symbols = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in group.Entries)
            {
                CompressionTests.Check(entry.Value.Length > 0 && entry.Name.Length > 0 && entry.Code.StartsWith("U+", StringComparison.Ordinal),
                    $"entry {entry.Value} keeps a symbol, a Chinese name and a code point");
                CompressionTests.Check(entry.Code == CharacterLibrary.DescribeCodePoints(entry.Value), $"code point of {entry.Value} is derived from the symbol, not from the file text");
                CompressionTests.Check(symbols.Add(entry.Value), $"group {group.Title} lists {entry.Value} only once");
            }
        }
        CompressionTests.Check(CharacterLibrary.DescribeCodePoints("🀄") == "U+1F004", "non-BMP symbols name their full code point");
        CompressionTests.Check(CharacterLibrary.DescribeCodePoints("”“") == "U+201D U+201C", "multi-character values name every code point");

        var lookup = library.Filter("勾");
        CompressionTests.Check(lookup.SelectMany(group => group.Entries).Any(entry => entry.Value == "✓"), "search by Chinese name finds the tick");
        CompressionTests.Check(lookup.SelectMany(group => group.Entries).All(entry => entry.Value != "℃"), "search by name drops unrelated symbols");
        CompressionTests.Check(library.Filter("2713").SelectMany(group => group.Entries).Any(entry => entry.Value == "✓"), "search by code point finds the tick");
        CompressionTests.Check(library.Filter("箭头").Count >= 2, "search by group name returns the arrow groups");
        CompressionTests.Check(library.Filter("✿").SelectMany(group => group.Entries).Any(entry => entry.Value == "✿"), "search by the symbol itself works");
        CompressionTests.Check(library.Filter("  ").Count == 22, "a blank query keeps the whole catalogue");
        CompressionTests.Check(library.Filter("zzzz不存在").Count == 0, "an unmatched query returns nothing instead of everything");

        bool rejected = false;
        try { CharacterLibrary.Parse("{\"version\":1,\"groups\":[]}"); } catch (InvalidDataException) { rejected = true; }
        CompressionTests.Check(rejected, "an empty catalogue is rejected instead of showing a blank tool");
        rejected = false;
        try { CharacterLibrary.Parse("{\"version\":9,\"groups\":[{\"title\":\"x\",\"level\":\"y\",\"items\":\"✓=勾号\"}]}"); }
        catch (InvalidDataException) { rejected = true; }
        CompressionTests.Check(rejected, "an unsupported catalogue version is rejected");

        Directory.CreateDirectory(root);
        Console.WriteLine("PASS shipped special-character catalogue: 22 groups, 557 characters, name/code/group search");
        return Task.CompletedTask;
    }

    private static string ShippedAssetPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Shunshou.slnx"))) directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("Repository root with Shunshou.slnx was not found.");
        return Path.Combine(directory.FullName, "src", "Shunshou.App", "Assets", CharacterLibrary.DefaultFileName);
    }
}
