using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shunshou.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Shunshou.App;

public sealed partial class DiskCleanupWorkspace : UserControl
{
    private CancellationTokenSource? _cts;
    private readonly ObservableCollection<CategoryVm> _vm = new();

    public DiskCleanupWorkspace()
    {
        InitializeComponent();
        ResultList.ItemsSource = _vm;
    }

    public nint HostWindowHandle { get; set; }

    public event EventHandler<bool>? BusyChanged;

    private sealed class CategoryVm : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public string Path { get; init; } = "";
        public string SizeText { get; init; } = "";
        public Visibility RequiresAdmin { get; init; }
        public CleanupItem Source { get; init; } = null!;
        private bool _isSelected = true;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
        }
    }

    private void SetBusy(bool busy)
    {
        ScanButton.IsEnabled = !busy;
        CleanButton.IsEnabled = !busy && _vm.Any(x => x.IsSelected);
        BusyChanged?.Invoke(this, busy);
    }

    private void ShowInfoBar(string title, string message, InfoBarSeverity severity)
    {
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await RunScanAsync();

    private async Task RunScanAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        Notice.IsOpen = false;
        _vm.Clear();
        EmptyHint.Visibility = Visibility.Visible;
        TotalText.Text = "";
        CleanButton.IsEnabled = false;
        try
        {
            SetBusy(true);
            var progress = new Progress<string>(s => ScanStatus.Text = s);
            var categories = await DiskCleanupService.ScanAsync(progress, _cts.Token);
            long totalBytes = 0;
            foreach (var category in categories)
            {
                foreach (var item in category.Items)
                {
                    _vm.Add(new CategoryVm
                    {
                        Name = category.Name,
                        Description = category.Description,
                        Path = item.Path,
                        SizeText = DiskCleanupService.FormatBytes(item.Bytes) + "（" + item.FileCount + " 个文件）",
                        RequiresAdmin = item.RequiresAdmin ? Visibility.Visible : Visibility.Collapsed,
                        Source = item
                    });
                    totalBytes += item.Bytes;
                }
            }
            EmptyHint.Visibility = _vm.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TotalText.Text = "共 " + DiskCleanupService.FormatBytes(totalBytes);
            ScanStatus.Text = _vm.Count == 0 ? "没有发现可清理的内容。" : "扫描完成。";
            CleanButton.IsEnabled = _vm.Any(x => x.IsSelected);
        }
        catch (OperationCanceledException) { ScanStatus.Text = "已取消。"; }
        catch (Exception ex) { ShowInfoBar("扫描失败", ex.Message, InfoBarSeverity.Warning); }
        finally { SetBusy(false); }
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var selected = _vm.Where(x => x.IsSelected).Select(x => x.Source).ToArray();
        if (selected.Length == 0) return;
        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "确认清理",
            Content = "选中的缓存/临时文件将被直接删除，不可恢复。是否继续？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        Notice.IsOpen = false;
        try
        {
            SetBusy(true);
            var progress = new Progress<string>(s => CleanStatus.Text = s);
            var r = await DiskCleanupService.CleanAsync(selected, progress, _cts.Token);
            // Refresh the list after cleaning; RunScanAsync manages its own busy state.
            await RunScanAsync();
            ShowInfoBar("清理完成",
                $"已删除 {r.DeletedFiles} 个文件，释放 {DiskCleanupService.FormatBytes(r.FreedBytes)}。{r.FailedCount} 项失败。",
                InfoBarSeverity.Informational);
        }
        catch (OperationCanceledException) { CleanStatus.Text = "已取消。"; }
        catch (Exception ex) { ShowInfoBar("清理失败", ex.Message, InfoBarSeverity.Warning); }
        finally { SetBusy(false); }
    }
}
