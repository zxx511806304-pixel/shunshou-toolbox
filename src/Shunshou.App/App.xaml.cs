using Microsoft.UI.Xaml;

namespace Shunshou.App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    
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
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex) { LogException(ex, "launch"); throw; }
    }

    internal static void LogException(Exception exception, string source)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShunshouToolbox", "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"error-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(path, $"[{DateTime.Now:O}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* Logging must never replace the original failure. */ }
    }
}

