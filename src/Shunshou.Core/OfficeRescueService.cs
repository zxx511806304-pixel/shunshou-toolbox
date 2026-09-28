using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Shunshou.Core;

public sealed record RescuableDocument(string App, string Path, long Bytes, DateTime Modified);

public sealed record ExtractResult(int TextBlocks, int MediaFiles, string OutputFolder);

/// <summary>Finds unsaved Office/WPS auto-recovery files and pulls text and media out of damaged OOXML documents. Everything stays on this computer and originals are never modified.</summary>
public sealed class OfficeRescueService
{
    private static readonly string[] AutoSavePatterns = ["*.asd", "*.xar", "*.tmp"];
    private static readonly Regex TextTag = new(@"<t(?:\s[^>]*)?>(.*?)</t>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CellTag = new(@"<(?:t|v)(?:\s[^>]*)?>(.*?)</(?:t|v)>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public Task<IReadOnlyList<RescuableDocument>> ScanUnsavedAsync(CancellationToken ct) => Task.Run(() =>
    {
        var found = new List<RescuableDocument>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        ScanAutoSaveFolder(found, seen, "Word", Path.Combine(roaming, "Microsoft", "Word"), ct);
        ScanAutoSaveFolder(found, seen, "Excel", Path.Combine(roaming, "Microsoft", "Excel"), ct);
        ScanAutoSaveFolder(found, seen, "PowerPoint", Path.Combine(roaming, "Microsoft", "PowerPoint"), ct);
        // Office keeps files that were never saved even once in a single shared folder.
        ScanUnsavedFilesFolder(found, seen, Path.Combine(local, "Microsoft", "Office", "UnsavedFiles"), ct);
        ScanFolder(found, seen, "WPS", Path.Combine(roaming, "Kingsoft", "office6", "backup"), "*", ct);
        return (IReadOnlyList<RescuableDocument>)found.OrderByDescending(d => d.Modified).ToArray();
    }, ct);

    private static void ScanAutoSaveFolder(List<RescuableDocument> found, HashSet<string> seen, string app, string folder, CancellationToken ct)
    {
        foreach (var pattern in AutoSavePatterns)
        {
            ScanFolder(found, seen, app, folder, pattern, ct);
            // Auto-recovery files may sit one level down, e.g. per-session subfolders.
            foreach (var child in SafeEnumerateDirectories(folder))
                ScanFolder(found, seen, app, child, pattern, ct);
        }
    }

    private static void ScanUnsavedFilesFolder(List<RescuableDocument> found, HashSet<string> seen, string folder, CancellationToken ct)
    {
        foreach (var path in SafeEnumerateFiles(folder, "*"))
        {
            ct.ThrowIfCancellationRequested();
            var app = AppForExtension(path);
            if (app is not null) Add(found, seen, app, path);
        }
    }

    private static void ScanFolder(List<RescuableDocument> found, HashSet<string> seen, string app, string folder, string pattern, CancellationToken ct)
    {
        foreach (var path in SafeEnumerateFiles(folder, pattern))
        {
            ct.ThrowIfCancellationRequested();
            Add(found, seen, app, path);
        }
    }

    private static void Add(List<RescuableDocument> found, HashSet<string> seen, string app, string path)
    {
        if (!seen.Add(path)) return;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { seen.Remove(path); return; }
            found.Add(new(app, info.FullName, info.Length, info.LastWriteTime));
        }
        catch (IOException) { seen.Remove(path); }
        catch (UnauthorizedAccessException) { seen.Remove(path); }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string folder, string pattern)
    {
        if (!Directory.Exists(folder)) return [];
        try { return Directory.EnumerateFiles(folder, pattern).ToArray(); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        try { return Directory.EnumerateDirectories(folder).ToArray(); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static string? AppForExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".docx" or ".doc" or ".docm" => "Word",
        ".xlsx" or ".xls" or ".xlsm" or ".xlsb" => "Excel",
        ".pptx" or ".ppt" or ".pptm" or ".ppsx" => "PowerPoint",
        _ => null
    };

    /// <summary>Copies the auto-save file into the chosen folder without touching the original. Name conflicts get a number suffix.</summary>
    public static Task<string> CopyToSafetyAsync(RescuableDocument doc, string targetFolder) => Task.Run(() =>
    {
        if (!File.Exists(doc.Path)) throw new FileNotFoundException("原文件已不存在，请重新扫描。", doc.Path);
        Directory.CreateDirectory(targetFolder);
        string name = Path.GetFileNameWithoutExtension(doc.Path);
        string extension = Path.GetExtension(doc.Path);
        var candidate = Path.Combine(targetFolder, name + extension);
        for (var index = 2; File.Exists(candidate); index++)
            candidate = Path.Combine(targetFolder, $"{name} ({index}){extension}");
        File.Copy(doc.Path, candidate, false);
        return candidate;
    });

    /// <summary>Opens a damaged .docx/.xlsx/.pptx as a ZIP and pulls out document text and embedded media.</summary>
    public static Task<ExtractResult> ExtractDamagedDocumentAsync(string file, string outputFolder, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(file)) throw new FileNotFoundException("文件不存在。", file);
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(file); }
        catch (InvalidDataException) { throw new InvalidDataException("文件损坏严重，无法读取内容"); }
        catch (IOException) { throw new InvalidDataException("文件损坏严重，无法读取内容"); }
        using (zip)
        {
            var root = NewFolder(outputFolder, Path.GetFileNameWithoutExtension(file) + "_提取");
            var text = new StringBuilder();
            var blocks = 0;
            foreach (var entry in TextEntries(zip))
            {
                ct.ThrowIfCancellationRequested();
                string xml;
                try
                {
                    using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    xml = reader.ReadToEnd();
                }
                catch (InvalidDataException) { continue; }
                catch (IOException) { continue; }
                var pieces = ExtractTextPieces(entry.FullName, xml);
                if (pieces.Count == 0) continue;
                text.AppendLine("===== " + entry.FullName.Replace('\\', '/') + " =====");
                foreach (var piece in pieces)
                {
                    text.AppendLine(piece);
                    blocks++;
                }
                text.AppendLine();
            }
            if (blocks > 0)
                File.WriteAllText(Path.Combine(root, "提取文本.txt"), text.ToString(), new UTF8Encoding(false));
            var media = 0;
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var name = entry.FullName.Replace('\\', '/');
                if (!IsMediaEntry(name)) continue;
                var fileName = Path.GetFileName(name);
                if (fileName.Length == 0) continue;
                try
                {
                    var mediaFolder = Path.Combine(root, "媒体文件");
                    Directory.CreateDirectory(mediaFolder);
                    using var source = entry.Open();
                    using var target = new FileStream(UniquePath(mediaFolder, fileName), FileMode.CreateNew, FileAccess.Write);
                    source.CopyTo(target);
                    media++;
                }
                catch (InvalidDataException) { }
                catch (IOException) { }
            }
            return new ExtractResult(blocks, media, root);
        }
    }, ct);

    private static IEnumerable<ZipArchiveEntry> TextEntries(ZipArchive zip) =>
        zip.Entries.Where(entry =>
        {
            var name = entry.FullName.Replace('\\', '/');
            return name.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase) ||
                (name.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) ||
                (name.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
        }).OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase);

    private static List<string> ExtractTextPieces(string entryName, string xml)
    {
        // Worksheet numbers live in <v> cells while all other text is in <t> runs.
        var isWorksheet = entryName.Replace('\\', '/').StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase);
        var tag = isWorksheet ? CellTag : TextTag;
        var pieces = new List<string>();
        foreach (Match match in tag.Matches(xml))
        {
            var text = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
            if (text.Length > 0) pieces.Add(text);
        }
        return pieces;
    }

    private static bool IsMediaEntry(string name) =>
        name.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("xl/media/", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("ppt/media/", StringComparison.OrdinalIgnoreCase);

    private static string UniquePath(string folder, string fileName)
    {
        var candidate = Path.Combine(folder, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; File.Exists(candidate); index++)
            candidate = Path.Combine(folder, $"{stem} ({index}){extension}");
        return candidate;
    }

    private static string NewFolder(string parent, string stem)
    {
        parent = Path.GetFullPath(parent);
        var candidate = Path.Combine(parent, stem);
        for (var index = 2; Directory.Exists(candidate); index++)
            candidate = Path.Combine(parent, $"{stem} ({index})");
        Directory.CreateDirectory(candidate);
        return candidate;
    }
}
