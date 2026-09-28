using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shunshou.Core;

namespace Shunshou.App;

/// <summary>浏览器劫持修复工作区：扫描并修复快捷方式劫持、主页劫持等问题。</summary>
public sealed partial class HijackRepairWorkspace : UserControl
{
    private readonly HijackScanService _service = new();
    private bool _isBusy;

    public HijackRepairWorkspace()
    {
        InitializeComponent();
    }

    public nint HostWindowHandle { get; set; }
    public bool IsBusy { get => _isBusy; private set { _isBusy = value; BusyChanged?.Invoke(this, value); } }
    public event EventHandler<bool>? BusyChanged;

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        try
        {
            IsBusy = true;
            ScanButton.IsEnabled = false;
            FixAllButton.IsEnabled = false;
            ProgressBar.Visibility = Visibility.Visible;
            Notice.IsOpen = false;
            FindingList.ItemsSource = null;
            StatusText.Text = "正在检查…";

            var progress = new Progress<string>(msg => StatusText.Text = msg);
            var result = await _service.ScanAsync(progress, CancellationToken.None);
            var viewModels = result.Findings.Select(f => new HijackFindingViewModel(f)).ToArray();
            FindingList.ItemsSource = viewModels;
            string skipNote = result.SkippedDirectories > 0
                ? $"（已跳过 {result.SkippedDirectories} 个无权限或系统兼容目录）"
                : "";

            if (viewModels.Length == 0)
            {
                StatusText.Text = "未发现问题" + skipNote;
                Notice.Title = "检查完成";
                Notice.Message = "未发现浏览器劫持或弹窗源头问题。" + skipNote;
                Notice.Severity = InfoBarSeverity.Success;
                Notice.IsOpen = true;
            }
            else
            {
                int fixable = viewModels.Count(v => v.CanAutoFix);
                StatusText.Text = $"发现 {viewModels.Length} 项问题，其中 {fixable} 项可自动修复" + skipNote;
                FixAllButton.IsEnabled = fixable > 0;
                Notice.Title = "检查完成";
                Notice.Message = $"发现 {viewModels.Length} 项问题，{fixable} 项可自动修复。" + skipNote;
                Notice.Severity = InfoBarSeverity.Warning;
                Notice.IsOpen = true;
            }
        }
        catch (Exception ex)
        {
            ShowError("检查失败", FriendlyMessage(ex));
            StatusText.Text = "检查未完成，请根据上方提示处理";
        }
        finally
        {
            IsBusy = false;
            ScanButton.IsEnabled = true;
            ProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private async void FixAll_Click(object sender, RoutedEventArgs e)
    {
        if (FindingList.ItemsSource is not IEnumerable<HijackFindingViewModel> items) return;
        var fixable = items.Where(v => v.CanAutoFix).ToArray();
        if (fixable.Length == 0) return;
        try
        {
            IsBusy = true;
            int success = 0, failed = 0;
            foreach (var vm in fixable)
            {
                try { await _service.FixAsync(vm.Id); success++; }
                catch { failed++; }
            }
            Notice.Title = "修复完成";
            Notice.Message = $"已修复 {success} 项" + (failed > 0 ? $"。{failed} 项修复失败，请重试或手动处理。" : "。");
            Notice.Severity = failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            Notice.IsOpen = true;
            await ScanAsync();
        }
        catch (Exception ex)
        {
            ShowError("批量修复失败", FriendlyMessage(ex));
        }
        finally { IsBusy = false; }
    }

    private async void FixItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id) return;
        try
        {
            IsBusy = true;
            button.IsEnabled = false;
            await _service.FixAsync(id);
            // Rescan to refresh state
            await ScanAsync();
        }
        catch (Exception ex)
        {
            ShowError("修复失败", FriendlyMessage(ex));
            button.IsEnabled = true;
        }
        finally { IsBusy = false; }
    }

    private void ShowError(string title, string message)
    {
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = InfoBarSeverity.Warning;
        Notice.IsOpen = true;
    }

    /// <summary>Maps system exceptions to plain Chinese guidance instead of showing raw .NET text.</summary>
    private static string FriendlyMessage(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "没有访问该系统位置的权限；如需检查此处，请以管理员身份运行顺手工具箱。",
        FileNotFoundException => "相关文件已不存在，请重新检查。",
        IOException => "磁盘或文件暂时无法读取，请关闭占用该文件的程序后重试。",
        InvalidOperationException => ex.Message,
        _ => "检查时出现意外问题，请重试；若反复出现请联系客服。"
    };

    private sealed class HijackFindingViewModel
    {
        public HijackFinding Finding { get; }
        public string Id => Finding.Id;
        public string Title => Finding.Title;
        public string Detail => Finding.Detail;
        public bool CanAutoFix => Finding.CanAutoFix;
        public string IconGlyph => Finding.Kind switch
        {
            "快捷方式" => "\uE71B",
            "主页" => "\uE80F",
            "浏览器配置" => "\uE774",
            "启动项" => "\uE8A3",
            _ => "\uE7BA"
        };

        public HijackFindingViewModel(HijackFinding finding) => Finding = finding;
    }
}
