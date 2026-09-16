using Microsoft.UI.Xaml;
using Shunshou.DesktopIntegration;
using Shunshou.Core;
using System.Runtime.InteropServices;

namespace Shunshou.App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    private IDisposable? _applicationLease;
    
    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        UnhandledException += (_, args) => LogException(args.Exception, "ui");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogException(args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()), "runtime");
        TaskScheduler.UnobservedTaskException += (_, args) => LogException(args.Exception, "task");
        try { InitializeComponent(); }
        catch (Exception ex) { LogException(ex, "initialization"); throw; }
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            var verificationLaunch = Environment.GetCommandLineArgs().Skip(1).Any(argument =>
                argument.StartsWith("--verify", StringComparison.Ordinal) || argument.StartsWith("--screenshot", StringComparison.Ordinal));
            var recoveryLaunch = Environment.GetCommandLineArgs().Contains("--recovery", StringComparer.Ordinal);
            if (!verificationLaunch)
            {
                try
                {
                    _applicationLease = recoveryLaunch
                        ? UpdateCoordination.AcquireExistingApplicationLease(AppPaths.InstallationDirectory)
                        : UpdateCoordination.AcquireApplicationLease(AppPaths.InstallationDirectory);
                }
                catch (Exception ex)
                {
                    LogException(ex, "update-coordination");
                    MessageBox(nint.Zero, "无法确认软件是否正在更新。请关闭更新窗口后重试。", "顺手工具箱", 0x10);
                    Exit();
                    return;
                }
                if (_applicationLease is null)
                {
                    MessageBox(nint.Zero, recoveryLaunch
                        ? "请先从其他磁盘正常打开工具箱，再进入误删恢复。若软件正在更新，请等待更新完成。"
                        : "软件正在更新，请等待更新完成后再打开。", "顺手工具箱", 0x40);
                    Exit();
                    return;
                }
                AppDomain.CurrentDomain.ProcessExit += (_, _) => _applicationLease?.Dispose();
            }
            _window = new MainWindow();
            _window.Activate();
            if (!verificationLaunch && !recoveryLaunch)
            {
                try
                {
                    var result = new DesktopShortcutService().InitializeOnNormalLaunch(AppPaths.InstallationDirectory);
                    foreach (var warning in result.Warnings) LogException(new IOException(warning), "desktop-integration");
                }
                catch (Exception ex) { LogException(ex, "desktop-integration"); }
            }
        }
        catch (Exception ex) { LogException(ex, "launch"); throw; }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint window, string text, string title, uint type);

    internal static void LogException(Exception exception, string source)
    {
        if (AppPaths.LoggingDisabled)
        {
            var arguments = Environment.GetCommandLineArgs();
            var verification = Array.IndexOf(arguments, "--verify-shop");
            if (verification < 0) verification = Array.IndexOf(arguments, "--verify-v040");
            if (verification < 0) verification = Array.IndexOf(arguments, "--verify-v100");
            if (verification >= 0 && verification + 1 < arguments.Length)
            {
                try
                {
                    Directory.CreateDirectory(arguments[verification + 1]);
                    File.AppendAllText(Path.Combine(arguments[verification + 1], "diagnostics.log"), $"{source}: {exception}{Environment.NewLine}");
                }
                catch { }
            }
            return;
        }
        try
        {
            var directory = Path.Combine(AppPaths.DataDirectory, "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"error-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(path, $"[{DateTime.Now:O}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* Logging must never replace the original failure. */ }
    }
}

