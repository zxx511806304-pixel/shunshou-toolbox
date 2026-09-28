using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shunshou.Core;
using Shunshou.DesktopIntegration;

namespace Shunshou.App;

public sealed partial class MainWindow
{
    private StartupRegistration? _startup;

    private StartupRegistration Startup => _startup ??= new StartupRegistration();

    /// <summary>
    /// Reflects the current start-up entry in the "···" menu and keeps an already enabled entry
    /// pointing at this copy after the folder was moved. A disabled entry is never created here.
    /// </summary>
    private void InitializeStartupRegistration()
    {
        StartupRegistrationState state;
        try
        {
            state = Startup.SyncOnLaunch(AppPaths.LauncherPath, out bool repaired);
            if (repaired) ShowStatus("开机自启动", "已把开机自启动更新为当前所在位置的顺手工具箱。", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            state = StartupRegistrationState.Unavailable;
            ShowStatus("开机自启动", "无法读取系统启动项：" + ex.Message, InfoBarSeverity.Warning);
        }
        RefreshStartupMenu(state);
    }

    private void RefreshStartupMenu(StartupRegistrationState? state = null)
    {
        var current = state ?? Startup.Read(AppPaths.LauncherPath);
        StartupMenuItem.IsChecked = current == StartupRegistrationState.Enabled;
        StartupMenuItem.IsEnabled = current != StartupRegistrationState.Unavailable;
        string detail = current switch
        {
            StartupRegistrationState.Enabled => "已开启：开机后用最小化窗口启动，点击任务栏图标即可使用。",
            StartupRegistrationState.PointsElsewhere => "系统里已有一个同名的启动项，不属于本软件；开启会改为使用当前这份顺手工具箱。",
            StartupRegistrationState.Unavailable => "无法读取系统启动项，当前不能修改。",
            _ => "未开启：需要时再从这里打开。"
        };
        ToolTipService.SetToolTip(StartupMenuItem, detail);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StartupMenuItem, "开机自启动。" + detail);
    }

    private void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        bool enable = StartupMenuItem.IsChecked;
        if (!SetStartupEnabled(enable))
            ShowStatus("开机自启动", enable ? "没有写入成功，系统可能限制了启动项写入。" : "没有关闭成功，请稍后重试。", InfoBarSeverity.Warning);
    }

    /// <summary>Shared by the menu and by verification so both drive the same code path.</summary>
    internal bool SetStartupEnabled(bool enable)
    {
        bool changed = enable ? Startup.Enable(AppPaths.LauncherPath) : Startup.Disable();
        RefreshStartupMenu();
        if (!changed) return false;
        ShowStatus("开机自启动", enable
            ? "已开启：下次开机后用最小化窗口启动顺手工具箱。"
            : "已关闭：软件不再随开机启动。", InfoBarSeverity.Informational);
        return true;
    }

    /// <summary>Start-up launches stay out of the way: the window opens minimised on the taskbar.</summary>
    internal void MinimizeForStartup()
    {
        try
        {
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                // This taskbar minimization must not be redirected to the tray.
                _suppressNextMinimizeTray = true;
                presenter.Minimize();
            }
        }
        catch (Exception ex) { App.LogException(ex, "startup-minimize"); }
    }
}
