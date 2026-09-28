using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Shunshou.Core;
using Windows.Storage.Pickers;

namespace Shunshou.App;

public sealed partial class NetworkDiagnosticsWorkspace : UserControl
{
    private readonly ObservableCollection<NetCheckRow> _rows = [];
    private NetReport? _report;
    private bool _busy;

    public NetworkDiagnosticsWorkspace()
    {
        InitializeComponent();
        ResultList.ItemsSource = _rows;
    }

    public nint HostWindowHandle { get; set; }

    public event EventHandler<bool>? BusyChanged;

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        Notice.IsOpen = false;
        _rows.Clear();
        ExportButton.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(message => ProgressText.Text = message);
            _report = await NetworkDiagnosticsService.RunAsync(progress, CancellationToken.None);
            foreach (var item in _report.Items)
                _rows.Add(new NetCheckRow(item));
            ProgressText.Text = $"诊断完成 · {_report.Items.Count(i => !i.Ok)} 项异常 / {_report.Items.Count} 项检查";
            ExportButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            ShowError("诊断失败", ex.Message);
            ProgressText.Text = "诊断未完成。";
        }
        finally { SetBusy(false); }
    }

    private async void FlushDns_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        Notice.IsOpen = false;
        try
        {
            string result = await NetworkDiagnosticsService.FlushDnsAsync();
            ProgressText.Text = result;
        }
        catch (Exception ex) { ShowError("清理 DNS 缓存失败", ex.Message); }
        finally { SetBusy(false); }
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "重置网络",
            Content = "将重置 Winsock 和 TCP/IP 协议栈，并清理 DNS 缓存。完成后需要重启电脑才能完全生效。\n\n确认继续吗？",
            PrimaryButtonText = "重置网络",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        SetBusy(true);
        Notice.IsOpen = false;
        try
        {
            await NetworkDiagnosticsService.RunNetworkResetAsync();
            ProgressText.Text = "已发起网络重置，请按系统提示完成操作并重启电脑。";
        }
        catch (Exception ex) { ShowError("重置网络失败", ex.Message); }
        finally { SetBusy(false); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        try
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = "网络诊断报告" };
            picker.FileTypeChoices.Add("文本文件", [".txt"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, HostWindowHandle);
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await NetworkDiagnosticsService.SaveReportAsync(_report, file.Path);
            ProgressText.Text = "已保存 · " + file.Name;
        }
        catch (Exception ex)
        {
            ShowError("导出报告失败", ex.Message);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RunButton.IsEnabled = FlushDnsButton.IsEnabled = ResetButton.IsEnabled = !busy;
        BusyChanged?.Invoke(this, busy);
    }

    private void ShowError(string title, string message)
    {
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = InfoBarSeverity.Warning;
        Notice.IsOpen = true;
    }

    private sealed class NetCheckRow(NetCheckItem item)
    {
        public string Name => item.Name;
        public string Detail => item.Detail;
        public string Glyph => item.Ok ? "" : "";
        public Brush Color => item.Ok
            ? new SolidColorBrush(Colors.ForestGreen)
            : new SolidColorBrush(Colors.IndianRed);
    }
}
