using System.Runtime.CompilerServices;

namespace Shunshou.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Log("Managed entry point reached.");
        int verifyIndex = Array.IndexOf(args, "--verify-package");
        if (verifyIndex >= 0)
        {
            if (verifyIndex + 2 >= args.Length) { Log("--verify-package requires fixture and output directories."); return 2; }
            try { return Task.Run(() => PackageVerification.RunAsync(args[verifyIndex + 1], args[verifyIndex + 2])).GetAwaiter().GetResult(); }
            catch (Exception ex) { Log("Package verification failed: " + ex); return 1; }
        }
        try { RunApplication(); return 0; }
        catch (Exception exception) { Log("Startup failed: " + exception); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunApplication()
    {
        Log("Initialize COM wrappers.");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Log("Enter WinUI Application.Start.");
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            Log("WinUI application callback reached.");
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            Log("Create application.");
            new App();
            Log("Application created.");
        });
        Log("Application message loop exited.");
    }

    private static void Log(string message)
    {
        var line = $"[{DateTime.Now:O}] {message}{Environment.NewLine}";
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup.log"), line); }
        catch
        {
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShunshouToolbox", "logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "startup.log"), line);
            }
            catch { }
        }
    }
}
