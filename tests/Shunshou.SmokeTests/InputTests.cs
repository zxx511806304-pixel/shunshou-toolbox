using System.Security.Cryptography;
using Shunshou.Core;

namespace Shunshou.SmokeTests;

public static class InputTests
{
    public static async Task RunAsync(string root)
    {
        string directory = Path.Combine(root, "inputs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Dictionary<string, string> files = [];
        foreach (string name in new[] { "截图.PNG", "照片.jpg", "材料.pdf", "附件.PDF", "图片.zip", "记录.json", "声音.WAV", "说明.txt" })
        {
            files[name] = Path.Combine(directory, name);
            await File.WriteAllTextAsync(files[name], "Input selection fixture: " + name);
        }
        Dictionary<string, string> before = files.Values.ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

        var batch = InputSelectionPolicy.Select(InputTool.ImageConvert, [files["截图.PNG"]],
            [files["截图.PNG"].ToLowerInvariant(), Path.Combine(directory, ".", "照片.jpg"), files["说明.txt"], directory, Path.Combine(directory, "gone.png")]);
        Require(batch.Applied && batch.Paths.SequenceEqual([files["截图.PNG"], files["照片.jpg"]]) && batch.AddedCount == 1 && batch.Rejected.Count == 3,
            "Batch drop preserves order, canonicalizes paths, deduplicates Windows case, accepts images and reports incompatible/missing items.");
        var duplicate = InputSelectionPolicy.Select(InputTool.ImageConvert, batch.Paths, [files["照片.jpg"]]);
        Require(duplicate.Applied && duplicate.AddedCount == 0 && duplicate.Paths.SequenceEqual(batch.Paths) && duplicate.Message!.Contains("已经添加"),
            "Repeated drops do not duplicate inputs and explain the unchanged selection.");
        var invalid = InputSelectionPolicy.Select(InputTool.Pdf, [files["材料.pdf"]], [files["说明.txt"], "\0", ""]);
        Require(!invalid.Applied && invalid.Paths.Single() == files["材料.pdf"] && invalid.Rejected.Count == 3,
            "Invalid drops keep the previously selected input.");
        var multiple = InputSelectionPolicy.Select(InputTool.Pdf, [files["材料.pdf"]], [files["材料.pdf"], files["附件.PDF"]]);
        Require(!multiple.Applied && multiple.Paths.Single() == files["材料.pdf"] && multiple.Message!.Contains("每次处理一个"),
            "Single-input tools reject multiple valid files instead of silently losing any.");
        var merged = InputSelectionPolicy.Select(InputTool.MergePdf, [], [files["附件.PDF"], files["材料.pdf"]]);
        Require(merged.Applied && merged.Paths.SequenceEqual([files["附件.PDF"], files["材料.pdf"]]), "Merge order follows incoming file order.");
        var busy = InputSelectionPolicy.Select(InputTool.ImageConvert, [files["截图.PNG"]], [files["照片.jpg"]], busy: true);
        Require(!busy.Applied && busy.Paths.Single() == files["截图.PNG"], "Busy input cannot change files being processed.");
        var folderSearch = InputSelectionPolicy.Select(InputTool.Search, [], [directory + Path.DirectorySeparatorChar]);
        Require(folderSearch.Applied && folderSearch.SearchDirectory == directory && folderSearch.SearchFileName == null && folderSearch.Paths.Single() == directory,
            "Folder drop chooses that folder as search scope.");
        var fileSearch = InputSelectionPolicy.Select(InputTool.Search, [], [files["截图.PNG"]]);
        Require(fileSearch.Applied && fileSearch.SearchDirectory == directory && fileSearch.SearchFileName == "截图.PNG" && fileSearch.Paths.Single() == directory,
            "File drop chooses parent search scope and fills the actual filename.");
        var searchMany = InputSelectionPolicy.Select(InputTool.Search, [directory], [directory, files["材料.pdf"]]);
        Require(!searchMany.Applied && searchMany.Paths.Single() == directory, "Ambiguous search drops cannot change the scope.");

        foreach (InputTool tool in Enum.GetValues<InputTool>())
        {
            string valid = tool switch
            {
                InputTool.TargetZip or InputTool.ExtractZip => files["图片.zip"],
                InputTool.Pdf or InputTool.MergePdf => files["材料.pdf"],
                InputTool.ImageConvert or InputTool.Ocr => files["截图.PNG"],
                InputTool.Media => files["声音.WAV"],
                InputTool.UndoRename => files["记录.json"],
                _ => files["说明.txt"]
            };
            Require(InputSelectionPolicy.Select(tool, [], [valid]).Applied, $"{tool} accepts its valid input.");
            bool folderAllowed = tool is InputTool.TargetZip or InputTool.CreateZip or InputTool.Search;
            Require(InputSelectionPolicy.Select(tool, [], [directory]).Applied == folderAllowed, $"{tool} accepts folders only when its service handles them.");
        }
        Require(!InputSelectionPolicy.Select(InputTool.Media, [], [files["说明.txt"]]).Applied, "Media validation rejects plain documents.");
        Require(!InputSelectionPolicy.Select(InputTool.TargetZip, [], [files["截图.PNG"]]).Applied, "Target ZIP compression expects an archive or folder.");
        Require(!InputSelectionPolicy.Select(InputTool.CreateZip, [], [files["截图.PNG"], files["照片.jpg"]]).Applied, "ZIP packing accepts the one source supported by its engine.");
        var ocrBatch = InputSelectionPolicy.Select(InputTool.Ocr, [], [files["截图.PNG"], files["照片.jpg"]]);
        Require(ocrBatch.Applied && ocrBatch.Paths.Count == 2, "OCR retains a batch; its active image is selected by the workspace.");
        VerifyToolDrafts(files);
        foreach (var item in before)
            Require(File.Exists(item.Key) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(item.Key))) == item.Value, "Input selection never changes originals.");
        Console.WriteLine("PASS: shared picker/drop validation, mixed input, deduplication, single-input protection, busy state, search file/folder mapping and unchanged originals.");
    }

    private static void VerifyToolDrafts(Dictionary<string, string> files)
    {
        string first = files["截图.PNG"], second = files["照片.jpg"], pdf = files["材料.pdf"], otherPdf = files["附件.PDF"];
        var workspace = new InputSelectionWorkspace();
        workspace.SwitchTo(InputTool.ImageConvert, []);
        var ocr = workspace.SwitchTo(InputTool.Ocr, [first, second], second);
        Require(ocr.Paths.SequenceEqual([first, second]) && ocr.SelectedPath == second && ocr.Restored,
            "Image conversion to OCR preserves the complete batch and selected image.");
        var imageAfterRemoval = workspace.SwitchTo(InputTool.ImageConvert, [second], second);
        Require(imageAfterRemoval.Paths.SequenceEqual([second]), "Removing an image in OCR updates the shared image conversion batch without resurrecting it.");
        var emptyOcr = workspace.SwitchTo(InputTool.Ocr, [], null);
        Require(emptyOcr.Paths.Count == 0, "An explicitly cleared shared image batch stays empty across tool changes.");

        workspace.SaveCurrent([first, second], second);
        var media = workspace.SwitchTo(InputTool.Media, [first, second], second);
        Require(media.Paths.Count == 0 && media.KeptInPreviousTool == 2 && media.Message != null,
            "Incompatible inputs remain in their previous draft while the next tool starts empty.");
        var returned = workspace.SwitchTo(InputTool.ImageConvert, [files["声音.WAV"]], files["声音.WAV"]);
        Require(returned.Paths.SequenceEqual([first, second]) && returned.SelectedPath == second,
            "Returning from an unrelated tool restores the previous image batch and selection.");
        var returnedMedia = workspace.SwitchTo(InputTool.Media, [first, second], first);
        Require(returnedMedia.Paths.Single() == files["声音.WAV"], "A previously visited tool restores its own input draft.");

        var mergeWorkspace = new InputSelectionWorkspace();
        mergeWorkspace.SwitchTo(InputTool.MergePdf, []);
        var singlePdf = mergeWorkspace.SwitchTo(InputTool.Pdf, [pdf, otherPdf], otherPdf);
        Require(singlePdf.Paths.SequenceEqual([otherPdf]) && singlePdf.KeptInPreviousTool == 1 && singlePdf.Message != null,
            "A single-PDF tool uses the active selection and reports that other PDFs remain in the merge draft.");
        var mergeRestored = mergeWorkspace.SwitchTo(InputTool.MergePdf, singlePdf.Paths, singlePdf.SelectedPath);
        Require(mergeRestored.Paths.SequenceEqual([pdf, otherPdf]) && mergeRestored.SelectedPath == otherPdf,
            "Returning to merge restores all PDF inputs in their original order.");
        mergeWorkspace.SaveCurrent([otherPdf, pdf], otherPdf);
        mergeWorkspace.SwitchTo(InputTool.Pdf, [otherPdf, pdf], otherPdf);
        var reordered = mergeWorkspace.SwitchTo(InputTool.MergePdf, [otherPdf], otherPdf);
        Require(reordered.Paths.SequenceEqual([otherPdf, pdf]), "Reordered PDF inputs survive tool switches without changing files.");

        var unselectedWorkspace = new InputSelectionWorkspace();
        unselectedWorkspace.SwitchTo(InputTool.MergePdf, []);
        var noChoice = unselectedWorkspace.SwitchTo(InputTool.Pdf, [pdf, otherPdf]);
        Require(noChoice.Paths.Count == 0 && noChoice.KeptInPreviousTool == 2 && noChoice.Message != null,
            "No active choice means a single-input tool never silently takes the first file from a batch.");
        var originals = unselectedWorkspace.SwitchTo(InputTool.MergePdf, []);
        Require(originals.Paths.SequenceEqual([pdf, otherPdf]), "All originals stay in their draft after an unselected single-input transfer.");
        var source = new[] { pdf, otherPdf };
        var compatible = InputSelectionPolicy.CompatiblePaths(InputTool.Pdf, source);
        Require(compatible.SequenceEqual(source) && source.Length == 2, "Compatibility inspection does not apply processing-count truncation.");
        Console.WriteLine("PASS: shared image/OCR batch, selected image, tool draft restore, explicit clear/remove, compatible transfer and retained PDF merge order.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
