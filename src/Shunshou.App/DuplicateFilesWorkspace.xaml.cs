using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shunshou.Core;
using Windows.Storage.Pickers;

namespace Shunshou.App;

/// <summary>Finds duplicate files locally and sends selected copies to the recycle bin.</summary>
public sealed partial class DuplicateFilesWorkspace : UserControl
{
    private readonly ObservableCollection<string> _folders = [];
    private readonly ObservableCollection<DuplicateGroupRow> _groups = [];
    private CancellationTokenSource? _operation;

    public DuplicateFilesWorkspace()
    {
        InitializeComponent();
        FolderList.ItemsSource = _folders;
        ResultsList.ItemsSource = _groups;
    }

    public nint HostWindowHandle { get; set; }
    public event EventHandler<bool>? BusyChanged;

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, HostWindowHandle);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            var path = Path.TrimEndingDirectorySeparator(folder.Path);
            if (!_folders.Contains(path, StringComparer.OrdinalIgnoreCase)) _folders.Add(path);
        }
        catch (Exception ex) { ShowError("无法添加文件夹", ex.Message); }
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is string path) _folders.Remove(path);
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) return;
        if (_folders.Count == 0) { ShowError("无法扫描", "请先添加要检查的文件夹。"); return; }
        Notice.IsOpen = false;
        _groups.Clear();
        AutoCheckButton.IsEnabled = false;
        DeleteButton.IsEnabled = false;
        EmptyText.Visibility = Visibility.Collapsed;
        SummaryText.Text = "";
        var roots = _folders.ToArray();
        var minBytes = (long)Math.Max(1, MinSizeBox.Value) * 1024 * 1024;
        _operation = new CancellationTokenSource();
        SetBusy(true, scanning: true);
        var progress = new Progress<string>(text => ScanProgress.Text = text);
        try
        {
            var result = await DuplicateFileService.ScanAsync(roots, minBytes, progress, _operation.Token);
            foreach (var group in result.Groups) _groups.Add(new DuplicateGroupRow(group));
            UpdateSummary(result.FilesScanned);
            AutoCheckButton.IsEnabled = _groups.Count > 0;
            DeleteButton.IsEnabled = _groups.Count > 0;
            EmptyText.Visibility = _groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ScanProgress.Text = _groups.Count == 0 ? "没有发现重复文件" : "扫描完成";
        }
        catch (OperationCanceledException)
        {
            ScanProgress.Text = "已取消";
        }
        catch (Exception ex) { ShowError("扫描失败", ex.Message); ScanProgress.Text = ""; }
        finally
        {
            _operation.Dispose();
            _operation = null;
            SetBusy(false, scanning: true);
        }
    }

    private void CancelScan_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();

    private void AutoCheck_Click(object sender, RoutedEventArgs e)
    {
        foreach (var group in _groups)
        {
            var ordered = group.Files.OrderBy(file => file.ModifiedTicks).ToArray();
            foreach (var file in ordered) file.Selected = !ReferenceEquals(file, ordered[0]);
        }
        ResultsList.ItemsSource = null;
        ResultsList.ItemsSource = _groups;
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) return;
        var selected = _groups.SelectMany(group => group.Files.Where(file => file.Selected)
            .Select(file => (Group: group, File: file))).ToArray();
        if (selected.Length == 0) { ShowError("没有选中文件", "请先勾选要删除的重复文件。"); return; }
        long bytes = selected.Sum(item => item.Group.Size);
        var dialog = new ContentDialog
        {
            Title = "删除选中文件",
            Content = $"将把 {selected.Length} 个文件移到回收站，可释放 {DuplicateFileService.FormatBytes(bytes)}。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _operation = new CancellationTokenSource();
        SetBusy(true, scanning: false);
        var removed = 0;
        var failed = 0;
        try
        {
            foreach (var (group, file) in selected)
            {
                try
                {
                    DuplicateFileService.SendToRecycleBin(file.Path);
                    group.Files.Remove(file);
                    removed++;
                }
                catch (Exception) { failed++; }
            }
            for (var index = _groups.Count - 1; index >= 0; index--)
                if (_groups[index].Files.Count < 2) _groups.RemoveAt(index);
            UpdateSummary(null);
            AutoCheckButton.IsEnabled = _groups.Count > 0;
            DeleteButton.IsEnabled = _groups.Count > 0;
            EmptyText.Visibility = _groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Notice.Title = "删除完成";
            Notice.Message = failed == 0
                ? $"已将 {removed} 个文件移到回收站。"
                : $"已将 {removed} 个文件移到回收站，{failed} 个失败。";
            Notice.Severity = failed == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            Notice.IsOpen = true;
        }
        catch (Exception ex) { ShowError("删除失败", ex.Message); }
        finally
        {
            _operation.Dispose();
            _operation = null;
            SetBusy(false, scanning: false);
        }
    }

    private void SetBusy(bool busy, bool scanning)
    {
        ScanButton.IsEnabled = !busy;
        CancelScanButton.IsEnabled = busy && scanning;
        AddFolderButton.IsEnabled = !busy;
        RemoveFolderButton.IsEnabled = !busy;
        MinSizeBox.IsEnabled = !busy;
        AutoCheckButton.IsEnabled = !busy && _groups.Count > 0;
        DeleteButton.IsEnabled = !busy && _groups.Count > 0;
        BusyChanged?.Invoke(this, busy);
    }

    private void UpdateSummary(int? filesScanned)
    {
        long reclaimable = _groups.Sum(group => group.Size * (group.Files.Count - 1));
        var scanned = filesScanned is { } count ? $"，扫描 {count} 个文件" : "";
        SummaryText.Text = $"共 {_groups.Count} 组 · 可释放 {DuplicateFileService.FormatBytes(reclaimable)}{scanned}";
    }

    private void ShowError(string title, string message)
    {
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = InfoBarSeverity.Warning;
        Notice.IsOpen = true;
    }

    private sealed class DuplicateFileRow
    {
        public required string Path { get; init; }
        public required string Name { get; init; }
        public required long ModifiedTicks { get; init; }
        public required string ModifiedText { get; init; }
        public bool Selected { get; set; }
    }

    private sealed class DuplicateGroupRow
    {
        public DuplicateGroupRow(DuplicateGroup group)
        {
            Size = group.Size;
            Header = $"{DuplicateFileService.FormatBytes(group.Size)} × {group.Paths.Count} 个文件";
            Files = new(group.Paths.Select(path =>
            {
                DateTime modified;
                try { modified = File.GetLastWriteTime(path); }
                catch (Exception) { modified = DateTime.MinValue; }
                return new DuplicateFileRow
                {
                    Path = path,
                    Name = System.IO.Path.GetFileName(path),
                    ModifiedTicks = modified.Ticks,
                    ModifiedText = modified == DateTime.MinValue ? "时间未知" : modified.ToString("yyyy-MM-dd HH:mm"),
                    Selected = false
                };
            }));
        }

        public long Size { get; }
        public string Header { get; }
        public ObservableCollection<DuplicateFileRow> Files { get; }
    }
}
