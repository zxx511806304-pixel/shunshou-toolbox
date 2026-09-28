using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Shunshou.Core;

namespace Shunshou.App;

public sealed partial class MainWindow
{
    private async Task WaitForIndexedFixtureAsync(string source, string query, int expected)
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            var result = await _indexedSearch.SearchAsync([source], query, false, null, default);
            if (result.Results.Count == expected) return;
            await Task.Delay(200);
        }
        throw new InvalidOperationException("Generated UI fixtures did not become visible in the existing index.");
    }

    private async Task<int> VerifySearchOneButtonAsync(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var original = _indexedSearch;
        var checks = new List<string>();
        double? realQueryMs = null;
        try
        {
            Navigation.SelectedItem = Navigation.MenuItems.OfType<Microsoft.UI.Xaml.Controls.NavigationViewItem>()
                .Single(x => Equals(x.Tag, "files"));
            SelectCategory("files");
            RequireUi(RunButton.Content?.ToString() == "搜索", "The sole primary action must say 搜索.");
            RequireUi(RootLayout.FindName("SearchEngineBox") == null && RootLayout.FindName("EnableSearchIndexButton") == null,
                "Old engine selector/enable action must not be exposed.");
            var source = Path.Combine(output, "generated-search");
            Directory.CreateDirectory(source);
            var path = Path.Combine(source, "Shunshou102-搜索.txt");
            await File.WriteAllTextAsync(path, "Generated single-action search fixture only.");
            var rows = new[] { new FileSearchResult(Path.GetFileName(path), path, false, new FileInfo(path).Length) };
            var fake = new SearchFlowFixture(rows);
            _indexedSearch = fake;
            await RefreshSearchIndexAsync();
            RequireUi(fake.EnableCount == 0 && SearchIndexStatus.Text.Contains("首次使用"),
                "Entering the search page must explain initial indexing without enabling it.");
            SelectSearchFolder(source, "Shunshou102-");
            ImagesOnly.IsChecked = false;
            RootLayout.RequestedTheme = ElementTheme.Light;
            await Task.Delay(650);
            await SaveScreenshot(Path.Combine(output, "search-first-use-light.png"));

            var enableGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fake.EnableAction = async ct =>
            {
                await enableGate.Task.WaitAsync(ct);
                fake.Status = new(EverythingIndexState.Loading, "正在建立文件索引…");
                fake.LoadingChecks = 2;
                return fake.Status;
            };
            Run_Click(RunButton, new RoutedEventArgs());
            await WaitForUiAsync(() => fake.EnableCount == 1, "One search click did not start index preparation.");
            RequireUi(_busy && CancelButton.IsEnabled && !RunButton.IsEnabled && fake.SearchCount == 0,
                "Index preparation must share the cancellable search progress and wait before querying.");
            enableGate.SetResult();
            await WaitForUiAsync(() => !_busy, "The initial search action did not complete after the index became ready.");
            RequireUi(_searchRows.Count == 1 && !_busy && fake.EnableCount == 1 && fake.SearchCount == 1 &&
                SearchIndexStatus.Text == "已就绪" && ProgressText.Text.Contains("搜索完成"),
                "The initial click must proceed through loading to results without another click.");
            checks.Add("Page status is read-only; one search action prepares an absent index, waits and shows results.");

            await RunSearchAsync();
            RequireUi(fake.EnableCount == 1 && fake.SearchCount == 2, "A ready index must be queried directly.");
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                RootLayout.RequestedTheme = theme;
                await Task.Delay(650);
                await SaveScreenshot(Path.Combine(output, $"search-single-action-{theme}.png"));
            }
            checks.Add("Ready searches have no enable step; Light and Dark layouts captured.");

            fake.Status = new(EverythingIndexState.NeedsEnable, "首次使用需建立文件索引。");
            fake.EnableAction = _ => Task.FromResult(new EverythingIndexStatus(EverythingIndexState.NeedsEnable,
                "未获得 Windows 授权，搜索未开始。再次点击搜索可重试。"));
            await RunSearchAsync();
            RequireUi(!_busy && _searchRows.Count == 0 && fake.SearchCount == 2 && StatusInfo.IsOpen &&
                StatusInfo.Message.Contains("未获得 Windows 授权"), "Declined preparation must stop without a slow fallback or false success.");
            checks.Add("Declined consent reports the reason, restores the action and never falls back to traversal.");

            fake.EnableAction = async ct => { await Task.Delay(Timeout.Infinite, ct); return fake.Status; };
            var cancelled = RunSearchAsync();
            await WaitForUiAsync(() => _busy && fake.EnableCount == 3, "Cancellable preparation did not start.");
            Cancel_Click(CancelButton, new RoutedEventArgs());
            await cancelled;
            RequireUi(!_busy && !TaskProgress.IsIndeterminate && fake.SearchCount == 2 &&
                SearchEmptyText.Text == "搜索已停止", "Cancellation must stop preparation and restore idle controls.");
            checks.Add("Cancelling preparation leaves no query in flight and permits a later retry.");

            fake.Status = new(EverythingIndexState.Missing, "搜索组件缺失，请重新解压完整软件包。");
            await RunSearchAsync();
            RequireUi(fake.EnableCount == 3 && fake.SearchCount == 2 && StatusInfo.Message.Contains("组件缺失"),
                "Missing components must show an actionable error without starting another workflow.");
            SearchQuery.Text = " ";
            await RunSearchAsync();
            RequireUi(!_busy && fake.EnableCount == 3, "Empty names must not initialize an index.");
            checks.Add("Missing components and empty input remain explicit errors without indexing side effects.");

            _indexedSearch = original;
            var actual = await original.GetStatusAsync();
            if (actual.State == EverythingIndexState.Ready)
            {
                await WaitForIndexedFixtureAsync(source, "Shunshou102-", 1);
                SelectSearchFolder(source, "Shunshou102-");
                var clock = Stopwatch.StartNew();
                await RunSearchAsync();
                realQueryMs = clock.Elapsed.TotalMilliseconds;
                RequireUi(_searchRows.Count == 1 && string.Equals(_searchRows[0].FullPath, path, StringComparison.OrdinalIgnoreCase),
                    "The actual existing Everything index did not return the generated fixture through the single action.");
                checks.Add("Actual existing Everything IPC queried through the same UI action with a generated Chinese filename.");
            }
            else checks.Add("Live existing-index check unavailable; no real enablement or UAC was requested by this test.");

            await File.WriteAllTextAsync(Path.Combine(output, "search-one-button-results.json"), JsonSerializer.Serialize(new
            { Passed = true, Checks = checks, ActualExistingIndexQueryMs = realQueryMs,
                NotCovered = "Real first-time UAC, clean-machine index creation and unrelated tools." }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "search-one-button-results.json"), JsonSerializer.Serialize(new
            { Passed = false, Checks = checks, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            try { await SaveScreenshot(Path.Combine(output, "search-failure.png")); } catch { }
            return 1;
        }
        finally { _indexedSearch = original; }
    }

    private sealed class SearchFlowFixture(IReadOnlyList<FileSearchResult> rows) : IIndexedFileSearchService
    {
        public EverythingIndexStatus Status { get; set; } = new(EverythingIndexState.NeedsEnable,
            "首次使用需建立文件索引，可能出现 Windows 授权提示；准备时间稍长，完成后搜索会快很多。");
        public int EnableCount { get; private set; }
        public int SearchCount { get; private set; }
        public int LoadingChecks { get; set; }
        public Func<CancellationToken, Task<EverythingIndexStatus>>? EnableAction { get; set; }
        public Task<EverythingIndexStatus> GetStatusAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Status.State == EverythingIndexState.Loading && --LoadingChecks <= 0) Status = new(EverythingIndexState.Ready, "已就绪");
            return Task.FromResult(Status);
        }
        public Task<EverythingIndexStatus> EnableAsync(IProgress<ToolProgress>? progress, CancellationToken ct)
        {
            EnableCount++;
            progress?.Report(new(0, Status.Message));
            return EnableAction?.Invoke(ct) ?? Task.FromResult(Status);
        }
        public Task<FileSearchSummary> SearchAsync(IEnumerable<string> roots, string query, bool imagesOnly,
            IProgress<FileSearchUpdate>? progress, CancellationToken ct, int maxResults = 10000, int batchSize = 100)
        {
            ct.ThrowIfCancellationRequested();
            if (Status.State != EverythingIndexState.Ready) throw new InvalidOperationException("Fixture queried before readiness.");
            SearchCount++;
            return Task.FromResult(new FileSearchSummary(rows, 0, 0, 0, false, false, TimeSpan.FromMilliseconds(1), 1, 1) { IsIndexed = true });
        }
    }
}
