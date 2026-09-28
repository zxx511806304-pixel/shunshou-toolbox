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
        await RunIndexedAsync(root);

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

    public static async Task RunIndexedAsync(string root, string? runtimeDirectory = null)
    {
        var fixture = Path.Combine(root, "indexed-" + Guid.NewGuid().ToString("N"));
        var mockRuntime = Path.Combine(fixture, "mock-runtime");
        Directory.CreateDirectory(mockRuntime);
        await File.WriteAllTextAsync(Path.Combine(mockRuntime, "es.exe"), "not executed; injectable command runner");
        await File.WriteAllTextAsync(Path.Combine(mockRuntime, "Everything.exe"), "not executed");
        var scope = Path.Combine(fixture, "资料 [a]+!");
        var output = "";
        int exitCode = 0;
        bool loading = false;
        IReadOnlyList<string>? submitted = null;
        static string Row(string path, string size, uint attributes) => $"\"{path.Replace("\"", "\"\"")}\",{size},{attributes}\r\n";
        Task<EverythingCommandResult> Fake(IReadOnlyList<string> args, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (args.Contains("-get-everything-version")) return Task.FromResult(new EverythingCommandResult(0, "1.4.1.1032"));
            if (args.Contains("-get-result-count")) return Task.FromResult(new EverythingCommandResult(loading ? 8 : 0, "1000"));
            submitted = args;
            return Task.FromResult(new EverythingCommandResult(exitCode, "Filename,Size,Attributes\r\n" + output));
        }
        var service = new EverythingSearchService(mockRuntime, Path.Combine(fixture, "data"), Fake);
        Require((await service.GetStatusAsync()).State == EverythingIndexState.Ready, "A loaded external index is available without starting any process.");
        loading = true;
        Require((await service.GetStatusAsync()).State == EverythingIndexState.Loading, "A version reply alone cannot mark an unloaded index as ready.");
        Require((await service.EnableAsync(null, default)).State == EverythingIndexState.Loading, "Enabling an already loading index waits without launching another elevated process.");
        loading = false;
        output = Row(Path.Combine(scope, "资料,a.jpg"), "1234", 32) + Row(Path.Combine(scope, "资料 folder"), "", 16) +
            Row(Path.Combine(scope, "资料 folder", "unrelated.txt"), "12", 32) +
            Row(Path.Combine(scope + "-sibling", "资料.jpg"), "12", 32) + Row(Path.Combine(scope, "资料-link.jpg"), "2", 1024);
        var updates = new List<FileSearchUpdate>();
        var found = await service.SearchAsync([scope], "资料", false, new InlineProgress<FileSearchUpdate>(updates.Add), default, batchSize: 1);
        Require(found.IsIndexed && found.Results.Count == 2 && found.Results[0].Name == "资料,a.jpg" && found.Results[0].Size == 1234 && found.Results[1].IsDirectory,
            "Indexed results retain Chinese, commas, file sizes and folders while rejecting out-of-scope/parent-name/reparse matches.");
        Require(found.ScannedEntries == 0 && updates.All(x => x.IsIndexed) && updates[^1].IsCompleted && updates.Sum(x => x.Results.Count) == 2,
            "Index queries report no directory traversal, with exactly-once result batches and a terminal update.");
        var images = await service.SearchAsync([scope], "资料", true, null, default);
        Require(images.Results.Count == 1 && images.Results[0].Name == "资料,a.jpg", "Image-only indexing excludes folders and non-images.");
        output = Row(Path.Combine(scope, "[a]+! folder"), "0", 16);
        var literal = await service.SearchAsync([scope], "[a]+!", false, null, default);
        Require(literal.Results.Count == 1 && submitted is not null && !submitted[^1].Contains("[a]+!", StringComparison.Ordinal),
            "Literal regex and Everything operators are encoded before reaching the native index.");
        output = Row(Path.Combine(scope, "one.txt"), "1", 32);
        var exact = await service.SearchAsync([scope], "one", false, null, default, maxResults: 1);
        Require(exact.Results.Count == 1 && !exact.IsTruncated, "An exact native result limit is not falsely marked truncated.");
        output += Row(Path.Combine(scope, "one-more.txt"), "2", 32);
        var capped = await service.SearchAsync([scope], "one", false, null, default, maxResults: 1);
        Require(capped.Results.Count == 1 && capped.IsTruncated, "The extra indexed match identifies truncation.");
        using (var cancelled = new CancellationTokenSource())
        {
            var stopped = await service.SearchAsync([scope], "one", false, new InlineProgress<FileSearchUpdate>(update =>
            { if (update.Results.Count > 0) cancelled.Cancel(); }), cancelled.Token, batchSize: 1);
            Require(stopped.IsCancelled && stopped.Results.Count == 1, "Indexed cancellation retains only already delivered results.");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var stopped = await service.SearchAsync([scope], "one", false, null, cancelled.Token);
            Require(stopped.IsCancelled && stopped.Results.Count == 0, "Pre-cancelled index searches return a cancellation summary.");
        }
        output = "\"unfinished";
        await Expect<InvalidDataException>(() => service.SearchAsync([scope], "one", false, null, default));
        exitCode = 8;
        await Expect<IOException>(() => service.SearchAsync([scope], "one", false, null, default));
        await Expect<ArgumentException>(() => service.SearchAsync([], "one", false, null, default));
        await Expect<ArgumentException>(() => service.SearchAsync([scope], " ", false, null, default));
        await Expect<ArgumentOutOfRangeException>(() => service.SearchAsync([scope], "one", false, null, default, maxResults: 0));
        var unavailable = new EverythingSearchService(mockRuntime, Path.Combine(fixture, "absent"), (_, _) => Task.FromResult(new EverythingCommandResult(8, "")));
        Require((await unavailable.GetStatusAsync()).State == EverythingIndexState.NeedsEnable, "Unavailable IPC offers explicit enablement and never starts a disk scan.");
        Console.WriteLine("PASS: indexed search state, unloaded database, literal Unicode names/operators, scoped filtering, CSV, images, limits and cancellation.");
        if (runtimeDirectory is not null) await RunLiveIndexAsync(root, fixture, runtimeDirectory);
    }

    private static async Task RunLiveIndexAsync(string root, string fixture, string runtimeDirectory)
    {
        var service = new EverythingSearchService(runtimeDirectory, Path.Combine(fixture, "live-data"));
        var status = await service.GetStatusAsync();
        Require(status.State == EverythingIndexState.Ready, "Live integration requires an already running, loaded Everything index; it never installs or enables one.");
        var folder = Path.Combine(fixture, "实际搜索 [a]+! space");
        Directory.CreateDirectory(Path.Combine(folder, "parent-only-needle"));
        await File.WriteAllTextAsync(Path.Combine(folder, "资料,[a]+!.JPG"), "fixture");
        await File.WriteAllTextAsync(Path.Combine(folder, "资料,[a]+!.txt"), "fixture");
        await File.WriteAllTextAsync(Path.Combine(folder, "parent-only-needle", "unrelated.txt"), "fixture");
        // USN updates are asynchronous. This bounded fixture readiness poll is separate from timed queries.
        FileSearchSummary? result = null;
        var first = System.Diagnostics.Stopwatch.StartNew();
        while (first.Elapsed < TimeSpan.FromSeconds(10))
        {
            result = await service.SearchAsync([folder], "[a]+!", false, null, default);
            if (result.Results.Count == 2) break;
            await Task.Delay(100);
        }
        Require(result?.Results.Count == 2 && result.Results.All(x => x.Name.Contains("资料,[a]+!", StringComparison.Ordinal)), "Actual ES preserves Chinese/comma/operator names and does not interpret regex operators.");
        first.Stop();
        var images = await service.SearchAsync([folder], "[a]+!", true, null, default);
        Require(images.Results.Count == 1 && images.Results[0].Name.EndsWith(".JPG", StringComparison.Ordinal), "Actual index applies case-insensitive image extension filtering.");
        var parent = await service.SearchAsync([folder], "parent-only-needle", false, null, default);
        Require(parent.Results.Count == 1 && parent.Results[0].IsDirectory, "Actual index excludes a child matching only its parent folder name.");
        var times = new List<double>();
        var roots = new FileService().GetLocalDrives().Select(x => x.RootPath).ToArray();
        int count = 0;
        for (var i = 0; i < 5; i++)
        {
            var timed = await service.SearchAsync(roots, "Shunshou", false, null, default);
            times.Add(timed.Elapsed.TotalMilliseconds); count = timed.Results.Count;
            Require(timed.IsIndexed && !timed.IsCancelled && timed.ScannedEntries == 0, "Measured searches use the live index without any directory enumeration.");
        }
        var report = new { ExistingIndex = true, FirstFixtureVisibilityMs = first.Elapsed.TotalMilliseconds, Query = "Shunshou", ResultCount = count,
            QueryMilliseconds = times, IncludesCliProcessStartup = true, FreshIndexBuildMeasured = false, FixtureChecksPassed = true };
        await File.WriteAllTextAsync(Path.Combine(root, "indexed-search-results.json"), System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS: live Everything index ({count} results); five searches: {string.Join(", ", times.Select(x => x.ToString("0.0")))} ms, including ES startup.");
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    { public void Report(T value) => action(value); }
    private static void Require(bool result, string message) { if (!result) throw new Exception(message); }
    private static async Task Expect<T>(Func<Task> call) where T : Exception
    { try { await call(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
