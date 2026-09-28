using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Shunshou.Core;

namespace Shunshou.App;

/// <summary>启动项管理工作区：枚举、禁用、恢复启动项。</summary>
public sealed partial class StartupManagerWorkspace : UserControl
{
    private readonly StartupManagerService _service = new();
    private bool _isBusy;

    public StartupManagerWorkspace()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
        EntryList.SelectionChanged += (_, _) => UpdateButtonState();
    }

    public nint HostWindowHandle { get; set; }
    public bool IsBusy { get => _isBusy; private set { _isBusy = value; BusyChanged?.Invoke(this, value); } }
    public event EventHandler<bool>? BusyChanged;

    private void UpdateButtonState()
    {
        var item = EntryList.SelectedItem as StartupEntryViewModel;
        DisableButton.IsEnabled = item is not null && item.Entry.Enabled;
        EnableButton.IsEnabled = item is not null && !item.Entry.Enabled;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        try
        {
            IsBusy = true;
            Notice.IsOpen = false;
            StatusText.Text = "正在加载…";
            var entries = await _service.ListAsync();
            var viewModels = entries.Select(e => new StartupEntryViewModel(e)).ToArray();
            EntryList.ItemsSource = viewModels;
            int suspiciousCount = viewModels.Count(v => v.Entry.Suspicious);
            StatusText.Text = $"共 {viewModels.Length} 项启动项";
            if (suspiciousCount > 0)
            {
                Notice.Title = "发现可疑启动项";
                Notice.Message = $"检测到 {suspiciousCount} 项可疑启动项（来自临时目录或命令异常），建议优先禁用并进一步核实。";
                Notice.Severity = InfoBarSeverity.Warning;
                Notice.IsOpen = true;
            }
        }
        catch (Exception ex)
        {
            ShowError("加载失败", ex.Message);
            StatusText.Text = "加载失败";
        }
        finally { IsBusy = false; }
    }

    private async void Disable_Click(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is not StartupEntryViewModel vm) return;
        try
        {
            IsBusy = true;
            await _service.DisableAsync(vm.Entry.Id);
            await RefreshAsync();
        }
        catch (UnauthorizedAccessException ex)
        {
            ShowError("需要管理员权限", ex.Message);
        }
        catch (Exception ex)
        {
            ShowError("禁用失败", ex.Message);
        }
        finally { IsBusy = false; }
    }

    private async void Enable_Click(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is not StartupEntryViewModel vm) return;
        try
        {
            IsBusy = true;
            await _service.EnableAsync(vm.Entry.Id);
            await RefreshAsync();
        }
        catch (UnauthorizedAccessException ex)
        {
            ShowError("需要管理员权限", ex.Message);
        }
        catch (Exception ex)
        {
            ShowError("启用失败", ex.Message);
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

    private sealed class StartupEntryViewModel
    {
        public StartupEntry Entry { get; }
        public string Name => Entry.Name;
        public string Command => Entry.Command;
        public string Source => Entry.Source;
        public string Company => Entry.Company ?? "—";
        public string StatusText => Entry.Enabled ? "已启用" : "已禁用";
        public Brush StatusBrush => Entry.Enabled
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 153, 153, 153));
        public string SuspiciousText => Entry.Suspicious ? "可疑" : "";
        public Brush SuspiciousBrush => Entry.Suspicious
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

        public StartupEntryViewModel(StartupEntry entry) => Entry = entry;
    }
}
