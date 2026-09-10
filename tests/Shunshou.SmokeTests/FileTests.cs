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
        await RunMultiRootSearchAsync(root);

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

    private static async Task RunMultiRootSearchAsync(string root)
    {
        var fixture = Path.Combine(root, "search-" + Guid.NewGuid().ToString("N"));
        var first = Path.Combine(fixture, "disk-a");
        var nested = Path.Combine(first, "nested");
        var sibling = Path.Combine(fixture, "disk-ab");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(sibling);
        Directory.CreateDirectory(Path.Combine(first, "Needle folder"));
        await File.WriteAllTextAsync(Path.Combine(first, "Needle.txt"), "first volume");
        await File.WriteAllTextAsync(Path.Combine(nested, "NEEDLE.JPG"), "second level");
        await File.WriteAllTextAsync(Path.Combine(sibling, "needle.png"), "second volume");
        var service = new FileService();
        var updates = new List<FileSearchUpdate>();
        var roots = new[] { nested, first + Path.DirectorySeparatorChar, first.ToUpperInvariant(), sibling };
        var summary = await service.SearchRootsAsync(roots, " needle ", false,
            new InlineProgress<FileSearchUpdate>(updates.Add), default, batchSize: 1);
        Require(summary.Results.Count == 4 && summary.Results.Count(x => x.IsDirectory) == 1,
            "Multi-root search includes files and folders across independent roots.");
        Require(summary.Results.Select(x => x.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 4,
            "Duplicate and nested roots produce each result once, while a common path prefix stays independent.");
        Require(summary.ScannedDirectories == 4 && summary.ScannedEntries == 5 && summary.SkippedEntries == 0,
            "Search statistics count actual directories and entries without duplicate traversal.");
        Require(!summary.IsCancelled && !summary.IsTruncated && updates[^1].IsCompleted,
            "Completed search has an explicit terminal update and completion status.");
        var delivered = updates.SelectMany(x => x.Results).ToArray();
        Require(delivered.Select(x => x.FullPath).SequenceEqual(summary.Results.Select(x => x.FullPath)) &&
                updates.Count(x => x.Results.Count > 0) == 4 && updates.Count(x => !x.IsCompleted && x.Results.Count > 0) >= 3,
            "Incremental batches deliver every result exactly once before completion.");
        Require(summary.Results.Single(x => x.Name == "Needle.txt").Size == 12,
            "Search reports the actual file size.");

        var images = await service.SearchRootsAsync(roots, "needle", true, null, default);
        Require(images.Results.Select(x => x.Name).Order().SequenceEqual(new[] { "NEEDLE.JPG", "needle.png" }.Order()),
            "Multi-root image filtering ignores extension and query case and excludes folders.");
        var partial = await service.SearchRootsAsync([Path.Combine(fixture, "missing"), first], "needle", false, null, default);
        Require(partial.Results.Count == 3 && partial.SkippedEntries == 1 && !partial.IsCancelled,
            "A disconnected or missing root is skipped while another root is searched.");
        var unavailable = await service.SearchRootsAsync([Path.Combine(fixture, "missing")], "needle", false, null, default);
        Require(unavailable.Results.Count == 0 && unavailable.RequestedRoots == 1 && unavailable.SearchedRoots == 0 &&
                unavailable.SkippedEntries == 1,
            "When all selected locations are unavailable the summary distinguishes that from an empty successful search.");
        var exactLimit = await service.SearchRootsAsync([sibling], "needle", false, null, default, maxResults: 1);
        Require(exactLimit.Results.Count == 1 && !exactLimit.IsTruncated,
            "Exactly the result limit is a complete result when there are no further matches.");
        var capped = await service.SearchRootsAsync(roots, "needle", false, null, default, maxResults: 2, batchSize: 1);
        Require(capped.Results.Count == 2 && capped.IsTruncated && !capped.IsCancelled,
            "More than the result limit returns retained results with a truncation flag instead of throwing.");

        using (var cts = new CancellationTokenSource())
        {
            var shown = new List<FileSearchResult>();
            var cancelled = await service.SearchRootsAsync(roots, "needle", false,
                new InlineProgress<FileSearchUpdate>(update =>
                {
                    shown.AddRange(update.Results);
                    if (shown.Count >= 2) cts.Cancel();
                }), cts.Token, batchSize: 1);
            Require(cancelled.IsCancelled && cancelled.Results.Count == 2 && shown.Count == 2 &&
                    shown.Select(x => x.FullPath).SequenceEqual(cancelled.Results.Select(x => x.FullPath)),
                "Cancelling an active search preserves every already-delivered result without reporting success.");
        }
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            var cancelled = await service.SearchRootsAsync(roots, "needle", false, null, cts.Token);
            Require(cancelled.IsCancelled && cancelled.Results.Count == 0 && cancelled.ScannedEntries == 0,
                "An already-cancelled multi-root search returns an empty cancellation summary.");
        }
        await Expect<ArgumentException>(() => service.SearchRootsAsync([], "needle", false, null, default));
        await Expect<ArgumentException>(() => service.SearchRootsAsync(roots, " ", false, null, default));
        await Expect<ArgumentOutOfRangeException>(() => service.SearchRootsAsync(roots, "needle", false, null, default, maxResults: 0));
        await Expect<IOException>(() => service.SearchRootsAsync(roots, "needle", false,
            new InlineProgress<FileSearchUpdate>(update =>
            {
                if (update.Results.Count > 0) throw new IOException("simulated callback failure");
            }), default, batchSize: 1));

        var deep = Path.Combine(fixture, "deep");
        for (var i = 0; i < 90; i++) deep = Path.Combine(deep, "d");
        Directory.CreateDirectory(deep);
        await File.WriteAllTextAsync(Path.Combine(deep, "needle.txt"), "deep result");
        var deepResult = await service.SearchRootsAsync([Path.Combine(fixture, "deep")], "needle", false, null, default);
        Require(deepResult.Results.Count == 1 && deepResult.ScannedDirectories == 91,
            "Deep directory trees are searched iteratively without losing their terminal file.");

        var linkTarget = Path.Combine(fixture, "link-target");
        var link = Path.Combine(first, "needle-link");
        Directory.CreateDirectory(linkTarget);
        await File.WriteAllTextAsync(Path.Combine(linkTarget, "needle-outside.txt"), "do not follow");
        try { Directory.CreateSymbolicLink(link, linkTarget); }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException && (ex.HResult & 0xffff) == 1314)
        {
            // Junctions exercise the same reparse-point exclusion without requiring administrator privileges.
            // Only generated fixture paths are used; quote them as literal PowerShell strings.
            static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
            var script = $"New-Item -Path {Quote(link)} -ItemType Junction -Value {Quote(linkTarget)} -ErrorAction Stop | Out-Null";
            var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
                         Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start) ?? throw new Exception("Could not create junction fixture.");
            var errors = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            Require(process.ExitCode == 0, "Junction fixture creation failed: " + await errors);
            await output;
        }
        var parentSearch = await service.SearchRootsAsync([first], "needle", false, null, default);
        Require(parentSearch.Results.Count == 3 && parentSearch.SkippedEntries == 1 &&
                !parentSearch.Results.Any(x => x.FullPath.StartsWith(link, StringComparison.OrdinalIgnoreCase)),
            "Directory links are not returned or followed during a parent-directory search.");
        var linkSearch = await service.SearchRootsAsync([link], "needle", false, null, default);
        Require(linkSearch.SearchedRoots == 0 && linkSearch.Results.Count == 0 && linkSearch.SkippedEntries == 1,
            "An explicit root that is a directory link is not followed.");

        var drives = service.GetLocalDrives();
        Require(drives.Select(x => x.RootPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == drives.Count &&
                drives.All(x => Path.IsPathRooted(x.RootPath) && !string.IsNullOrWhiteSpace(x.DisplayName)),
            "Local drive discovery returns unique absolute roots and usable labels without scanning their contents.");
        Console.WriteLine("PASS: local-drive discovery, multi-root traversal, incremental batches, duplicates, image filter, partial failure, result cap and cancellation retention.");
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    { public void Report(T value) => action(value); }
    private static void Require(bool result, string message) { if (!result) throw new Exception(message); }
    private static async Task Expect<T>(Func<Task> call) where T : Exception
    { try { await call(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
