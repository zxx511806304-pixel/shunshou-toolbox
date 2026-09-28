using System.Runtime.CompilerServices;
using Shunshou.Core;

namespace Shunshou.App;

internal static class Program
{
    /// <summary>Per-session mutex that owns the singleton slot for a user-facing launch.</summary>
    private const string SingleInstanceMutexName = @"Local\ShunshouToolbox.SingleInstance";
    /// <summary>Auto-reset event the second instance pulses to wake the first one up.</summary>
    private const string ActivateEventName = @"Local\ShunshouToolbox.Activate";

    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _activateEvent;

    /// <summary>
    /// The activate event the running first instance listens on, or null when single-instance
    /// was bypassed for this launch (verification, recovery, elevated sub-windows).
    /// </summary>
    internal static EventWaitHandle? ActivateEvent => _activateEvent;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--recovery", StringComparer.Ordinal) || args.Any(a => a.StartsWith("--verify", StringComparison.Ordinal) || a.StartsWith("--screenshot", StringComparison.Ordinal))) AppPaths.DisableLogging();
        Log("Managed entry point reached.");
        int verifyIndex = Array.IndexOf(args, "--verify-package");
        if (verifyIndex >= 0)
        {
            if (verifyIndex + 2 >= args.Length) { Log("--verify-package requires fixture and output directories."); return 2; }
            try { return Task.Run(() => PackageVerification.RunAsync(args[verifyIndex + 1], args[verifyIndex + 2])).GetAwaiter().GetResult(); }
            catch (Exception ex) { Log("Package verification failed: " + ex); return 1; }
        }
        // Single-instance applies to normal user-facing launches only. Special modes
        // (recovery, package verification, UI screenshots, elevated sub-windows driven by
        // --software/--recovery) must always be allowed through so their dedicated windows work.
        if (!IsSpecialLaunch(args))
        {
            if (TryAcquireSingleInstanceSlot())
            {
                Log("Single-instance slot acquired; this is the first running copy.");
            }
            else
            {
                Log("Another instance is already running; signaling it to activate and exiting.");
                try { _singleInstanceMutex?.Dispose(); } catch { /* Best-effort cleanup. */ }
                _singleInstanceMutex = null;
                return 0;
            }
        }
        try { RunApplication(); return 0; }
        catch (Exception exception) { Log("Startup failed: " + exception); return 1; }
        finally
        {
            try { _activateEvent?.Dispose(); } catch { /* Never fail shutdown on cleanup. */ }
            try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* Ownership may have transferred on abnormal exit. */ }
            try { _singleInstanceMutex?.Dispose(); } catch { /* Same as above. */ }
        }
    }

    /// <summary>
    /// Returns true for launches that must always be allowed through (verification, recovery,
    /// screenshot automation, and elevated sub-windows spawned for uninstall/recovery). The
    /// <c>--startup</c> switch is intentionally NOT special: if the OS starts a copy while the
    /// user is already running one, the second launch should silently hand off to the first.
    /// </summary>
    private static bool IsSpecialLaunch(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg == "--recovery" || arg == "--software" || arg == "--verify-package" ||
                arg.StartsWith("--verify", StringComparison.Ordinal) ||
                arg.StartsWith("--screenshot", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Attempts to become the single running instance. On success, also opens the activation
    /// event so subsequent launches can wake this process. On failure, signals the existing
    /// instance via the activation event and returns false.
    /// </summary>
    private static bool TryAcquireSingleInstanceSlot()
    {
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName, out bool createdNew);
            if (!createdNew)
            {
                // Another process owns the slot; pulse the activation event so it brings its
                // window forward, then hand off. EventWaitHandle is opened, not created: the
                // first instance is responsible for its lifetime.
                try
                {
                    if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var existing))
                    {
                        existing.Set();
                        existing.Dispose();
                    }
                    else
                    {
                        Log("Activation event not found; the running instance may still be initializing.");
                    }
                }
                catch (Exception ex) { Log("Failed to signal existing instance: " + ex); }
                return false;
            }
            // First instance: own the activation event for the lifetime of this process so
            // later launches can find and signal it.
            _activateEvent = new EventWaitHandle(initialState: false, mode: EventResetMode.AutoReset, name: ActivateEventName);
            return true;
        }
        catch (Exception ex)
        {
            // Mutex/EventWaitHandle failures are non-fatal; degrade to allowing the launch.
            Log("Single-instance setup failed: " + ex);
            return true;
        }
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
        if (AppPaths.LoggingDisabled) return;
        var line = $"[{DateTime.Now:O}] {message}{Environment.NewLine}";
        try
        {
            var directory = Path.Combine(AppPaths.DataDirectory, "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "startup.log"), line);
        }
        catch { /* Portable read-only locations can still launch; never write elsewhere implicitly. */ }
    }
}
