namespace Shunshou.Core;

public enum InputTool
{
    TargetZip, CreateZip, ExtractZip, Archive, Pdf, MergePdf, WordToPdf, ImageConvert, CropImage, Ocr, Media, TrimMedia, ExtractAudio, Search, Rename, UndoRename
}

public sealed record InputSelectionResult(
    IReadOnlyList<string> Paths,
    bool Applied,
    int AddedCount,
    IReadOnlyList<string> Rejected,
    string? Message = null,
    string? SearchDirectory = null,
    string? SearchFileName = null);

/// <summary>Shared input rules for the file picker and desktop file drops. Never changes files.</summary>
public static class InputSelectionPolicy
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff", ".gif" };

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".mp4", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".wmv",
        ".avi", ".mkv", ".mov", ".webm", ".m4v", ".mpg", ".mpeg", ".m2ts", ".mts", ".ts",
        ".3gp", ".3g2", ".amr", ".ac3", ".ape", ".aif", ".aiff", ".aifc", ".alac", ".au",
        ".caf", ".mka", ".oga", ".ogv", ".vob", ".flv", ".f4v", ".mp2", ".m2v", ".mxf"
    };

    // OCR keeps a batch of images in the workspace and processes its selected image.
    public static bool AllowsMultiple(InputTool tool) => tool is InputTool.MergePdf or InputTool.ImageConvert or InputTool.CropImage or InputTool.Ocr or InputTool.Rename;

    /// <summary>Finds compatible existing inputs without imposing a processing-count limit or changing the source collection.</summary>
    public static IReadOnlyList<string> CompatiblePaths(InputTool tool, IEnumerable<string> paths)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in paths)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
                if (!seen.Add(path)) continue;
                bool directory = Directory.Exists(path);
                if (IsValid(tool, path, !directory && File.Exists(path), directory)) result.Add(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return result;
    }

    public static string[] FileFilters(InputTool tool) => tool switch
    {
        InputTool.TargetZip or InputTool.ExtractZip => [".zip"],
        InputTool.Archive => SevenZipService.SupportedExtensions,
        InputTool.Pdf or InputTool.MergePdf => [".pdf"],
        InputTool.WordToPdf => [".docx"],
        InputTool.ImageConvert or InputTool.CropImage or InputTool.Ocr => ImageExtensions.ToArray(),
        InputTool.Media or InputTool.TrimMedia or InputTool.ExtractAudio => MediaExtensions.ToArray(),
        InputTool.UndoRename => [".json"],
        _ => ["*"]
    };

    public static InputSelectionResult Select(InputTool tool, IEnumerable<string> current, IEnumerable<string> incoming, bool busy = false)
    {
        string[] previous = current.ToArray();
        if (busy) return new(previous, false, 0, [], "正在处理文件，请完成或取消后再添加。");

        List<string> valid = [];
        List<string> rejected = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in incoming)
        {
            string path;
            try
            {
                if (string.IsNullOrWhiteSpace(candidate)) { rejected.Add("空路径"); continue; }
                path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                rejected.Add(candidate);
                continue;
            }
            if (!seen.Add(path)) continue;
            bool directory = Directory.Exists(path);
            bool file = !directory && File.Exists(path);
            if (!IsValid(tool, path, file, directory)) { rejected.Add(path); continue; }
            valid.Add(path);
            if (valid.Count > 10_000)
                return new(previous, false, 0, rejected, "一次最多添加 10,000 个文件，请分批添加。");
        }

        if (valid.Count == 0)
            return new(previous, false, 0, rejected, rejected.Count == 0 ? null : ExpectedInput(tool));
        if (!AllowsMultiple(tool) && valid.Count > 1)
            return new(previous, false, 0, rejected, "这个工具每次处理一个输入，请只添加一个文件或文件夹。");

        if (tool == InputTool.Search)
        {
            string path = valid[0];
            string? fileName = File.Exists(path) ? Path.GetFileName(path) : null;
            string directory = fileName == null ? path : Path.GetDirectoryName(path)!;
            return new([directory], true, 1, rejected, RejectionMessage(rejected), directory, fileName);
        }

        string[] selected = AllowsMultiple(tool)
            ? previous.Concat(valid).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : valid.ToArray();
        if (selected.Length > 10_000)
            return new(previous, false, 0, rejected, "一次最多添加 10,000 个文件，请分批添加。");
        int added = AllowsMultiple(tool) ? selected.Length - previous.Length : 1;
        string? message = RejectionMessage(rejected);
        if (added == 0 && message == null) message = "这些文件已经添加过了。";
        return new(selected, true, added, rejected, message);
    }

    private static bool IsValid(InputTool tool, string path, bool file, bool directory)
    {
        string extension = Path.GetExtension(path);
        return tool switch
        {
            InputTool.TargetZip => directory || file && extension.Equals(".zip", StringComparison.OrdinalIgnoreCase),
            InputTool.CreateZip or InputTool.Search => file || directory,
            InputTool.ExtractZip => file && extension.Equals(".zip", StringComparison.OrdinalIgnoreCase),
            InputTool.Archive => file && SevenZipService.SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase),
            InputTool.Pdf or InputTool.MergePdf => file && extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase),
            InputTool.WordToPdf => file && extension.Equals(".docx", StringComparison.OrdinalIgnoreCase),
            InputTool.ImageConvert or InputTool.CropImage or InputTool.Ocr => file && ImageExtensions.Contains(extension),
            InputTool.Media or InputTool.TrimMedia or InputTool.ExtractAudio => file && MediaExtensions.Contains(extension),
            InputTool.Rename => file,
            InputTool.UndoRename => file && extension.Equals(".json", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static string? RejectionMessage(IReadOnlyList<string> rejected) => rejected.Count == 0 ? null
        : $"已跳过 {rejected.Count} 项格式不符或无法访问的输入：" + string.Join("、", rejected.Take(3).Select(Path.GetFileName)) + (rejected.Count > 3 ? "…" : "");

    private static string ExpectedInput(InputTool tool) => tool switch
    {
        InputTool.TargetZip => "请添加一个 ZIP 压缩包或图片文件夹。",
        InputTool.CreateZip => "请添加一个可访问的文件或文件夹。",
        InputTool.ExtractZip => "请添加一个 ZIP 压缩包。",
        InputTool.Archive => "请添加一个 .7z、.rar、.zip、.tar 或 .cab 压缩包。",
        InputTool.Pdf or InputTool.MergePdf => "请添加 PDF 文件。",
        InputTool.WordToPdf => "请添加 .docx 格式的 Word 文档。",
        InputTool.ImageConvert or InputTool.CropImage or InputTool.Ocr => "请添加 PNG、JPG、WebP、BMP、TIFF 或 GIF 图片。",
        InputTool.Media or InputTool.TrimMedia or InputTool.ExtractAudio => "请添加音频或视频文件，例如 WAV、MP3、FLAC、MP4、MOV 或 MKV。",
        InputTool.Search => "请添加一个可访问的文件或文件夹作为搜索范围。",
        InputTool.UndoRename => "请选择重命名记录 JSON 文件。",
        _ => "请添加可访问的文件。"
    };
}
