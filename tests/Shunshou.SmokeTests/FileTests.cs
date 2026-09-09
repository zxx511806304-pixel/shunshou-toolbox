using Shunshou.Core;

namespace Shunshou.SmokeTests;

public static class FileTests
{
    public static async Task RunAsync(string root)
    {
        var dir = Path.Combine(root, "files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "资料图片文件夹"));
        await File.WriteAllTextAsync(Path.Combine(dir, "资料.txt"), "original document");
        await File.WriteAllTextAsync(Path.Combine(dir, "资料图片文件夹", "资料.jpg"), "image fixture");
        var service = new FileService(Path.Combine(dir, "journals"));
        var hits = await service.SearchAsync(dir, "资料", false, null, default);
        Require(hits.Count == 3 && hits.Count(x => x.IsDirectory) == 1, "Search finds both files and folders, including nested items.");
        var images = await service.SearchAsync(dir, "资料", true, null, default);
        Require(images.Count == 1 && images[0].Name == "资料.jpg", "Image filter excludes folders and other extensions.");
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            await Expect<OperationCanceledException>(() => service.SearchAsync(dir, "资料", false, null, cts.Token));
        }
        await Expect<ArgumentException>(() => service.SearchAsync(dir, "", false, null, default));
        await Expect<DirectoryNotFoundException>(() => service.SearchAsync(Path.Combine(dir, "missing"), "a", false, null, default));

        var a = Path.Combine(dir, "a.txt"); var b = Path.Combine(dir, "b.txt");
        await File.WriteAllTextAsync(a, "Alpha"); await File.WriteAllTextAsync(b, "Beta");
        var preview = service.PreviewRename([a, b], "论文", 9);
        Require(File.Exists(a) && File.Exists(b) && Path.GetFileName(preview[0].TargetPath) == "论文_009.txt", "Preview changes no file and keeps extensions.");
        var journal = await service.ApplyRenameAsync(preview, default);
        Require(!File.Exists(a) && await File.ReadAllTextAsync(preview[0].TargetPath) == "Alpha", "Rename preserves contents.");
        await service.UndoRenameAsync(journal, default);
        Require(await File.ReadAllTextAsync(a) == "Alpha" && await File.ReadAllTextAsync(b) == "Beta", "Undo restores names and contents.");
        await Expect<InvalidOperationException>(() => service.UndoRenameAsync(journal, default));

        var swapped = await service.ApplyRenameAsync([new(a,b),new(b,a)], default);
        Require(await File.ReadAllTextAsync(a) == "Beta" && await File.ReadAllTextAsync(b) == "Alpha", "Two-phase rename handles name swaps without overwrite.");
        await service.UndoRenameAsync(swapped, default);
        Require(await File.ReadAllTextAsync(a) == "Alpha", "Swaps can be undone.");
        await Expect<IOException>(() => service.ApplyRenameAsync([new(a,b)], default));
        Require(await File.ReadAllTextAsync(a) == "Alpha" && await File.ReadAllTextAsync(b) == "Beta", "Collision leaves all source files intact.");

        journal = await service.ApplyRenameAsync(service.PreviewRename([a], "edited", 1), default);
        var edited = Path.Combine(dir, "edited_001.txt");
        await File.WriteAllTextAsync(edited, "Changed after renaming");
        await Expect<InvalidOperationException>(() => service.UndoRenameAsync(journal, default));
        Require(!File.Exists(a) && await File.ReadAllTextAsync(edited) == "Changed after renaming", "Undo detects modified contents and leaves user edits alone.");
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            await Expect<OperationCanceledException>(() => service.ApplyRenameAsync(service.PreviewRename([b], "cancelled", 1), cts.Token));
            Require(File.Exists(b), "Cancelled rename preserves source.");
        }
        Console.WriteLine("PASS: filename search, image filter, reversible renaming, swaps, collisions, modified-file protection and cancellation.");
    }
    private static void Require(bool result, string message) { if (!result) throw new Exception(message); }
    private static async Task Expect<T>(Func<Task> call) where T : Exception
    { try { await call(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
