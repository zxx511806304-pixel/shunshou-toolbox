using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Shunshou.Core;
using Windows.System;

namespace Shunshou.App;

public sealed partial class MainWindow
{
    private readonly ObservableCollection<SearchResultItem> _searchRows = [];
    private readonly HashSet<string> _shownSearchPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Image, CancellationTokenSource> _thumbnailRequests = [];
    private SearchPreviewService? _previews;
    private CancellationTokenSource _searchViewCancellation = new();
    private CancellationTokenSource? _selectedPreviewCancellation;
    private string? _selectedSearchFolder;
    private long _searchGeneration;

    private sealed record SearchScope(string Label, string? Root = null, bool IsFolder = false)
    {
        public override string ToString() => Label;
    }

    private void InitializeSearch()
    {
        _previews = new SearchPreviewService(DispatcherQueue);
        var scopes = new List<SearchScope> { new("全部本机磁盘") };
        scopes.AddRange(new FileService().GetLocalDrives().Select(d => new SearchScope(d.DisplayName, d.RootPath)));
        scopes.Add(new("指定文件夹", IsFolder: true));
        SearchScopeBox.ItemsSource = scopes;
        SearchScopeBox.SelectedIndex = 0;
        SearchResults.ItemsSource = _searchRows;
    }

    private void SearchScope_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (SearchFolderPanel == null) return;
        SetVisible(SearchFolderPanel, SearchScopeBox.SelectedItem is SearchScope { IsFolder: true });
    }

    private async void ChooseSearchFolder_Click(object sender, RoutedEventArgs args)
    {
        if (_busy) return;
        try
        {
            var folder = await PickFolder();
            if (folder != null && !_busy) SelectSearchFolder(folder);
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void SelectSearchFolder(string folder, string? query = null)
    {
        if (_busy) return;
        _selectedSearchFolder = Path.GetFullPath(folder);
        SearchFolderLabel.Text = _selectedSearchFolder;
        ToolTipService.SetToolTip(SearchFolderLabel, _selectedSearchFolder);
        SearchScopeBox.SelectedItem = SearchScopeBox.Items.Cast<SearchScope>().First(s => s.IsFolder);
        if (query != null) SearchQuery.Text = query;
    }

    private void SearchQuery_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter && !_busy) { args.Handled = true; Run_Click(sender, new RoutedEventArgs()); }
    }

    private void ResetSearchView()
    {
        ++_searchGeneration;
        _searchViewCancellation.Cancel();
        _searchViewCancellation.Dispose();
        _searchViewCancellation = new CancellationTokenSource();
        _selectedPreviewCancellation?.Cancel();
        foreach (var request in _thumbnailRequests.Values) request.Cancel();
        _searchRows.Clear();
        _shownSearchPaths.Clear();
        if (SearchCount == null) return;
        SearchCount.Text = "搜索结果";
        SearchEmpty.Visibility = Visibility.Visible;
        SearchEmptyText.Text = "输入文件或文件夹名称";
        ClearSelectedPreview();
    }

    private void ClearSelectedPreview()
    {
        SelectedPreview.Source = null;
        PreviewIcon.Visibility = Visibility.Visible;
        PreviewName.Text = "选择结果查看预览";
        PreviewDetail.Text = PreviewPath.Text = "";
        PreviewLoading.IsActive = false;
        OpenSearchItemButton.IsEnabled = RevealSearchItemButton.IsEnabled = false;
    }

    private void AppendSearchResults(IEnumerable<FileSearchResult> files)
    {
        foreach (var file in files)
            if (_shownSearchPaths.Add(file.FullPath)) _searchRows.Add(new SearchResultItem(file));
        SearchCount.Text = $"搜索结果 · {_searchRows.Count:N0} 项";
        SetVisible(SearchEmpty, _searchRows.Count == 0);
    }

    private async Task RunSearchAsync()
    {
        string query = SearchQuery.Text.Trim();
        if (query.Length == 0) { ShowError("请输入要查找的名称。"); SearchQuery.Focus(FocusState.Programmatic); return; }
        var scope = SearchScopeBox.SelectedItem as SearchScope;
        string[] roots;
        if (scope?.IsFolder == true)
        {
            if (_selectedSearchFolder == null) { ShowError("请选择要搜索的文件夹，或将文件夹拖入窗口。"); return; }
            roots = [_selectedSearchFolder];
        }
        else if (scope?.Root != null) roots = [scope.Root];
        else roots = new FileService().GetLocalDrives().Select(d => d.RootPath).ToArray();
        if (roots.Length == 0) { ShowError("没有找到可读取的本机磁盘。"); return; }

        ResetSearchView();
        long generation = _searchGeneration;
        bool imagesOnly = ImagesOnly.IsChecked == true;
        _cancellation = new CancellationTokenSource();
        var cancellation = _cancellation;
        SetBusy(true);
        StatusInfo.IsOpen = false;
        TaskProgress.IsIndeterminate = true;
        ProgressText.Text = "正在搜索…";
        SearchEmptyText.Text = "正在查找匹配的名称…";
        IProgress<FileSearchUpdate> progress = new Progress<FileSearchUpdate>(update =>
        {
            if (generation != _searchGeneration || !_busy) return;
            AppendSearchResults(update.Results);
            ProgressText.Text = $"已扫描 {update.ScannedEntries:N0} 项 · 找到 {update.MatchedCount:N0} 项";
        });
        try
        {
            var result = await new FileService().SearchRootsAsync(roots, query, imagesOnly, progress, cancellation.Token);
            AppendSearchResults(result.Results);
            ProgressText.Text = $"{(result.IsCancelled ? "搜索已停止" : "搜索完成")} · 找到 {result.Results.Count:N0} 项 · {result.Elapsed.TotalSeconds:0.0} 秒";
            SearchEmptyText.Text = result.IsCancelled ? "搜索已停止，尚未找到匹配项" : "没有找到匹配项，请换个名称试试";
            if (result.SearchedRoots == 0 && !result.IsCancelled)
                ShowStatus("未能读取搜索位置", "请确认磁盘或文件夹仍可访问。", InfoBarSeverity.Warning);
            else if (result.IsTruncated)
                ShowStatus("已显示前 10,000 项", "输入更完整的名称，或缩小搜索范围后继续查找。", InfoBarSeverity.Informational);
            else if (result.SkippedEntries > 0)
                ProgressText.Text += $" · 跳过 {result.SkippedEntries:N0} 个无法访问的项目";
        }
        catch (OperationCanceledException) { ProgressText.Text = $"搜索已停止 · 保留 {_searchRows.Count:N0} 项结果"; }
        catch (Exception ex) { ProgressText.Text = "搜索未完成"; ShowError(ex.Message); }
        finally
        {
            TaskProgress.IsIndeterminate = false;
            TaskProgress.Value = 100;
            SetBusy(false);
            cancellation.Dispose();
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
        }
    }

    private async void SearchThumbnail_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Image image) return;
        if (image.Tag is SearchResultItem previousItem && !ReferenceEquals(image.DataContext, previousItem)) ReleaseRowPreview(image);
        if (image.DataContext is not SearchResultItem item || _previews == null) return;
        image.Tag = item;
        if (item.Thumbnail != null) return;
        if (_thumbnailRequests.Remove(image, out var previous)) previous.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_searchViewCancellation.Token);
        _thumbnailRequests[image] = cancellation;
        try
        {
            var thumbnail = await _previews.LoadAsync(item.FullPath, 128, cancellation.Token);
            if (!cancellation.IsCancellationRequested && ReferenceEquals(image.DataContext, item)) item.Thumbnail = thumbnail;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.LogException(ex, "search-thumbnail"); }
        finally
        {
            if (_thumbnailRequests.TryGetValue(image, out var current) && ReferenceEquals(current, cancellation)) _thumbnailRequests.Remove(image);
            cancellation.Dispose();
        }
    }

    private void SearchThumbnail_Unloaded(object sender, RoutedEventArgs args)
    {
        if (sender is Image image) ReleaseRowPreview(image);
    }

    private void ReleaseRowPreview(Image image)
    {
        if (_thumbnailRequests.Remove(image, out var cancellation)) cancellation.Cancel();
        // Keep decoded bitmaps only for realized rows. The preview service owns a bounded encoded cache.
        if (image.Tag is SearchResultItem previousItem) previousItem.Thumbnail = null;
        image.Tag = null;
    }

    private void SearchThumbnail_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is not Image image) return;
        ReleaseRowPreview(image);
        if (image.IsLoaded) SearchThumbnail_Loaded(image, new RoutedEventArgs());
    }

    private async void SearchResults_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        _selectedPreviewCancellation?.Cancel();
        if (SearchResults.SelectedItem is not SearchResultItem item || _previews == null) { ClearSelectedPreview(); return; }
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_searchViewCancellation.Token);
        _selectedPreviewCancellation = cancellation;
        PreviewName.Text = item.Name;
        PreviewPath.Text = item.FullPath;
        ToolTipService.SetToolTip(PreviewPath, item.FullPath);
        PreviewDetail.Text = item.Detail;
        PreviewIcon.Glyph = item.Glyph;
        PreviewIcon.Visibility = Visibility.Visible;
        SelectedPreview.Source = null;
        OpenSearchItemButton.IsEnabled = RevealSearchItemButton.IsEnabled = true;
        PreviewLoading.IsActive = true;
        try
        {
            var image = await _previews.LoadAsync(item.FullPath, 480, cancellation.Token);
            if (!cancellation.IsCancellationRequested && ReferenceEquals(SearchResults.SelectedItem, item))
            {
                SelectedPreview.Source = image;
                SetVisible(PreviewIcon, image == null);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.LogException(ex, "selected-preview"); }
        finally
        {
            if (ReferenceEquals(_selectedPreviewCancellation, cancellation)) { PreviewLoading.IsActive = false; _selectedPreviewCancellation = null; }
            cancellation.Dispose();
        }
    }

    private void SearchResults_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        // Do not open the previous selection when the user double-clicks empty list space.
        var element = args.OriginalSource as DependencyObject;
        while (element != null && element is not ListViewItem) element = VisualTreeHelper.GetParent(element);
        if (element is ListViewItem row && row.Content is SearchResultItem item) OpenPath(item.FullPath);
    }

    private void OpenSearchItem_Click(object sender, RoutedEventArgs args)
    {
        if (SearchResults.SelectedItem is SearchResultItem item) OpenPath(item.FullPath);
    }

    private void RevealSearchItem_Click(object sender, RoutedEventArgs args)
    {
        if (SearchResults.SelectedItem is not SearchResultItem item) return;
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add("/select,");
            start.ArgumentList.Add(item.FullPath);
            Process.Start(start);
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void DisposeSearch()
    {
        _searchViewCancellation.Cancel();
        _selectedPreviewCancellation?.Cancel();
        _previews?.Dispose();
    }
}

public sealed class SearchResultItem(FileSearchResult result) : INotifyPropertyChanged
{
    public string Name => result.Name;
    public string FullPath => result.FullPath;
    public bool IsDirectory => result.IsDirectory;
    public string Glyph => IsDirectory ? "\uE8B7" : "\uE8A5";
    public string Detail => IsDirectory ? "文件夹" : (Path.GetExtension(FullPath).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext + " 文件" : "文件")
        + (result.Size < 0 ? "" : $" · {(result.Size >= 1_000_000 ? $"{result.Size / 1_000_000d:0.##} MB" : $"{result.Size / 1000d:0.#} KB")}");
    private ImageSource? _thumbnail;
    public Visibility IconVisibility => _thumbnail == null ? Visibility.Visible : Visibility.Collapsed;
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconVisibility)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
