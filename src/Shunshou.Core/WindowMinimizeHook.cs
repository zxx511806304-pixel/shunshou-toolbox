using System.Runtime.InteropServices;

namespace Shunshou.Core;

/// <summary>
/// Subclasses a top-level window and raises a callback when it becomes minimized
/// (WM_SIZE with SIZE_MINIMIZED), regardless of how minimization was triggered.
/// </summary>
public sealed class WindowMinimizeHook : IDisposable
{
    private const int GWLP_WNDPROC = -4;
    private const uint WM_SIZE = 0x0005;
    private const nuint SIZE_MINIMIZED = 1;

    private nint _hwnd;
    private nint _originalProc;
    private WinProc? _newProc;
    private readonly Action _minimized;
    private bool _disposed;

    /// <summary>
    /// When true, WM_SIZE/SIZE_MINIMIZED is swallowed at the window procedure instead of
    /// invoking the callback. Set during programmatic window-state changes (tray restore).
    /// </summary>
    public bool SuppressCallbacks { get; set; }

    private WindowMinimizeHook(nint hwnd, Action minimized)
    {
        _hwnd = hwnd;
        _minimized = minimized;
    }

    /// <summary>Installs the hook. Returns null if the window could not be subclassed.</summary>
    public static WindowMinimizeHook? Install(nint hwnd, Action minimized)
    {
        if (hwnd == nint.Zero) return null;
        var hook = new WindowMinimizeHook(hwnd, minimized);
        hook._newProc = hook.WindowProcedure;
        nint newProc = Marshal.GetFunctionPointerForDelegate(hook._newProc);
        nint original = Native.SetWindowLongPtr(hwnd, GWLP_WNDPROC, newProc);
        if (original == nint.Zero) return null;
        hook._originalProc = original;
        GC.KeepAlive(hook);
        return hook;
    }

    private nint WindowProcedure(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        if (msg == WM_SIZE && wParam == SIZE_MINIMIZED && !SuppressCallbacks)
        {
            try { _minimized(); }
            catch { /* A hook failure must never break the window procedure. */ }
        }
        return Native.CallWindowProc(_originalProc, hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_originalProc != nint.Zero && _hwnd != nint.Zero)
            Native.SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _originalProc);
        _newProc = null;
        GC.SuppressFinalize(this);
    }

    private delegate nint WinProc(nint hwnd, uint msg, nuint wParam, nint lParam);

    private static class Native
    {
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        internal static extern nint SetWindowLongPtr(nint hwnd, int index, nint newLong);

        [DllImport("user32.dll")]
        internal static extern nint CallWindowProc(nint previousProc, nint hwnd, uint msg, nuint wParam, nint lParam);
    }
}

/// <summary>Win32 top-level window placement helpers.</summary>
public static class WindowPlacement
{
    private const int SW_RESTORE = 9;

    /// <summary>Restores a minimized/maximized window to its normal placement (SW_RESTORE).</summary>
    public static void Restore(nint hwnd)
    {
        if (hwnd != nint.Zero) ShowWindow(hwnd, SW_RESTORE);
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int command);
}

/// <summary>Win32 top-level window foreground helpers used by tray and single-instance activation.</summary>
public static class NativeWindowHelpers
{
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly nint HWND_TOPMOST = new(-1);
    private static readonly nint HWND_NOTOPMOST = new(-2);

    /// <summary>
    /// Brings a window to the foreground. SetForegroundWindow refuses calls from
    /// non-foreground processes, so the standard workaround is to briefly attach the calling
    /// thread to the target window's input, then undo it. Failing that, the window is raised
    /// via SetWindowPos which still puts it above other non-topmost windows.
    /// </summary>
    public static void SetForeground(nint hwnd)
    {
        if (hwnd == nint.Zero) return;
        uint threadId = GetWindowThreadProcessId(hwnd, out _);
        uint currentThread = GetCurrentThreadId();
        bool attached = false;
        if (threadId != currentThread)
            attached = AttachThreadInput(currentThread, threadId, attach: true);
        try
        {
            SetForegroundWindow(hwnd);
            // Ensure the window is visible and on top of the Z-order even if the foreground
            // call was silently rejected by the OS foreground-lock rules.
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        finally
        {
            if (attached) AttachThreadInput(currentThread, threadId, attach: false);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int w, int h, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attachFrom, uint attachTo, bool attach);
}
