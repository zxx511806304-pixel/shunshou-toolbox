using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shunshou.Core;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Windows.Storage.Pickers;

namespace Shunshou.App;

public sealed partial class LargeFilesWorkspace : UserControl
{
    private CancellationTokenSource? _cts;
    private readonly ObservableCollection<LargeFileVm> _vm = new();
    private string _currentFolder = "C:\\";

    private sealed class LargeFileVm
    {
        public string Path { get; init; } = "";
        public string Name => System.IO.Path.GetFileName(Path);
        public long Bytes { get; init; }
        public string SizeText { get; init; } = "";
        public string ModifiedText { get; init; } = "";
    }

    public LargeFilesWorkspace()
    {
        InitializeComponent();
        ResultList.ItemsSource = _vm;
        MinSizeBox.Value = 100;
        EmptyHint.Visibility = Visibility.Visible;
    }

    public nint HostWindowHandle { get; set; }

    public event EventHandler<bool>? BusyChanged;

    private void SetBusy(bool busy)
    {
        ScanButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        BusyChanged?.Invoke(this, busy);
    }

    private void ShowInfoBar(string title, string message, InfoBarSeverity severity)
    {
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

    private async void PickFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, HostWindowHandle);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            _currentFolder = folder.Path;
            FolderBox.Text = _currentFolder;
        }
        catch (Exception ex) { ShowInfoBar("选择目录失败", ex.Message, InfoBarSeverity.Warning); }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentFolder) || !Directory.Exists(_currentFolder))
        {
            ShowInfoBar("目录不存在", "请选择有效的扫描目录。", InfoBarSeverity.Warning);
            return;
        }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        Notice.IsOpen = false;
        _vm.Clear();
        EmptyHint.Visibility = Visibility.Visible;
        TotalText.Text = "";
        try
        {
            SetBusy(true);
            var progress = new Progress<string>(s => ScanStatus.Text = s);
            var minBytes = (long)MinSizeBox.Value * 1024 * 1024;
            var results = await DiskCleanupService.ScanLargeFilesAsync(_currentFolder, minBytes, 200, progress, _cts.Token);
            long totalBytes = 0;
            foreach (var item in results)
            {
                _vm.Add(new LargeFileVm
                {
                    Path = item.Path,
                    Bytes = item.Bytes,
                    SizeText = DiskCleanupService.FormatBytes(item.Bytes),
                    ModifiedText = item.Modified.ToString("yyyy-MM-dd HH:mm")
                });
                totalBytes += item.Bytes;
            }
            EmptyHint.Visibility = _vm.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TotalText.Text = "共 " + DiskCleanupService.FormatBytes(totalBytes);
            ScanStatus.Text = "扫描完成。";
        }
        catch (OperationCanceledException) { ScanStatus.Text = "已取消。"; }
        catch (Exception ex) { ShowInfoBar("扫描失败", ex.Message, InfoBarSeverity.Warning); }
        finally { SetBusy(false); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path }) return;
        try { Process.Start("explorer.exe", "/select,\"" + path + "\""); }
        catch (Exception ex) { ShowInfoBar("打开失败", ex.Message, InfoBarSeverity.Warning); }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path }) return;
        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "确认删除",
            Content = "此文件将被移动到回收站，可以恢复。是否继续？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            var item = _vm.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
            if (item is not null) _vm.Remove(item);
            EmptyHint.Visibility = _vm.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TotalText.Text = "共 " + DiskCleanupService.FormatBytes(_vm.Sum(x => x.Bytes));
        }
        catch (Exception ex) { ShowInfoBar("删除失败", ex.Message, InfoBarSeverity.Warning); }
    }
}
