using System.Runtime.InteropServices;

namespace Shunshou.Core;

/// <summary>
/// Win32 system-tray icon with a context menu, running on its own dedicated message thread.
/// Uses a registered window class with a real WndProc (the canonical tray pattern) so the
/// shell's tray callback messages are delivered reliably whether the user left-clicks
/// (restore) or right-clicks (context menu with 恢复窗口 / 退出).
/// </summary>
public sealed class SystemTray : IDisposable
{
    private nint _hwnd;
    private nint _trayIcon;
    private nint _menu;
    private nint _windowClassAtom;
    private readonly string _tooltip;
    private readonly Action _showCallback;
    private readonly Action _exitCallback;

    private Thread? _thread;
    private int _ownerThreadId;
    private bool _disposed;
    private bool _teardownDone;

    private const uint WM_TRAY = 0x8000 + 1; // WM_APP + 1
    private const string ClassName = "ShunshouToolboxTray";

    // The WndProc delegate MUST stay alive for as long as the unmanaged window class
    // references it. A static readonly field roots it for the whole AppDomain lifetime,
    // which is exactly what we want for a singleton tray window.
    private static readonly WndProcDelegate _wndProc = new(WndProc);

    // Per-thread "currently creating" instance pointer: WM_CREATE fires synchronously
    // inside CreateWindowEx on the tray thread, so this lets the static WndProc associate
    // the new HWND with the SystemTray instance that is creating it.
    [ThreadStatic] private static SystemTray? _creating;
    private static readonly Dictionary<nint, SystemTray> _instances = new();
    private static readonly object _instancesLock = new();

    public SystemTray(nint hwndOwner, string tooltip, Action showCallback, Action exitCallback)
    {
        _tooltip = tooltip;
        _showCallback = showCallback;
        _exitCallback = exitCallback;
    }

    /// <summary>Starts the dedicated tray thread (window, icon and menu are created on it).</summary>
    public void Show()
    {
        if (_thread is not null) return;
        _thread = new Thread(MessageThread) { Name = "ShunshouTray", IsBackground = true };
        _thread.Start();
    }

    private void MessageThread()
    {
        _ownerThreadId = Environment.CurrentManagedThreadId;

        // Register a dedicated window class with our own WndProc. Using a registered class
        // (instead of the system "Static" class) is the canonical tray pattern: it lets the
        // shell deliver tray callback messages straight into WndProc via the standard
        // GetMessage/DispatchMessage pump, and lets us handle WM_CREATE/WM_DESTROY cleanly
        // inside WndProc rather than intercepting messages inline in the pump.
        var wc = new Native.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            lpszClassName = ClassName
        };
        _windowClassAtom = Native.RegisterClassExW(ref wc);
        if (_windowClassAtom == 0) return;

        _creating = this;
        try
        {
            // A real (never shown) top-level popup window. Never use HWND_MESSAGE: a
            // message-only window can never become foreground, which breaks TrackPopupMenu.
            _hwnd = Native.CreateWindowEx(0, ClassName, "TrayHidden", unchecked((int)0x80000000),
                0, 0, 0, 0, nint.Zero, nint.Zero, nint.Zero, nint.Zero);
        }
        finally { _creating = null; }
        if (_hwnd == nint.Zero)
        {
            Native.UnregisterClassW(ClassName, nint.Zero);
            _windowClassAtom = 0;
            return;
        }

        // Prefer the executable's own icon so the tray shows the product logo.
        if (Environment.ProcessPath is string exe && Native.ExtractIconEx(exe, 0, out nint large, out nint small, 1) > 0)
            _trayIcon = small != nint.Zero ? small : large;
        if (_trayIcon == nint.Zero) _trayIcon = Native.LoadIcon(nint.Zero, Native.IDI_APPLICATION);

        var notify = new Native.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = 0x01 | 0x02 | 0x04, // NIF_MESSAGE | NIF_ICON | NIF_TIP
            uCallbackMessage = WM_TRAY,
            hIcon = _trayIcon,
            szTip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip
        };
        if (!Native.Shell_NotifyIconW(0x00, ref notify)) // NIM_ADD
        {
            Teardown();
            return;
        }

        _menu = Native.CreatePopupMenu();
        Native.AppendMenuW(_menu, 0 /*MF_STRING*/, 1, "恢复窗口");
        Native.AppendMenuW(_menu, 0, 2, "退出");

        // Standard GetMessage/TranslateMessage/DispatchMessage pump. WndProc handles
        // WM_TRAY inline; WM_CLOSE triggers teardown; WM_DESTROY posts WM_QUIT to exit.
        while (Native.GetMessageW(out var msg, nint.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }

        Teardown();
    }

    /// <summary>Instance-level message handler invoked by the static WndProc.</summary>
    private nint HandleMessage(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_TRAY)
        {
            if (lParam == 0x0202 || lParam == 0x0203) // WM_LBUTTONUP / WM_LBUTTONDBLCLK: restore
            {
                _showCallback();
            }
            else if (lParam == 0x0205) // WM_RBUTTONUP: context menu
            {
                ShowContextMenu();
            }
            return nint.Zero;
        }
        if (msg == 0x0010) // WM_CLOSE: thread-safe teardown request
        {
            Teardown();
            Native.DestroyWindow(hwnd); // triggers WM_DESTROY, which posts WM_QUIT
            return nint.Zero;
        }
        if (msg == 0x0002) // WM_DESTROY
        {
            lock (_instancesLock) _instances.Remove(hwnd);
            Native.PostQuitMessage(0);
            return nint.Zero;
        }
        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        Native.GetCursorPos(out var pt);
        // Standard tray-menu pattern: the tray window itself must be foreground while
        // the menu is open (the click on the icon grants foreground rights), and the
        // WM_NULL afterwards lets the menu dismiss cleanly when focus is lost.
        Native.SetForegroundWindow(_hwnd);
        int cmd = Native.TrackPopupMenu(_menu, 0x0182, pt.x, pt.y, 0, _hwnd, nint.Zero); // TPM_RIGHTBUTTON|TPM_RETURNCMD|TPM_NONOTIFY
        Native.PostMessageW(_hwnd, 0x0000, nint.Zero, nint.Zero); // WM_NULL
        if (cmd == 1) _showCallback();
        else if (cmd == 2) _exitCallback();
    }

    private void Teardown()
    {
        if (_teardownDone) return;
        _teardownDone = true;
        if (_hwnd != nint.Zero)
        {
            var notify = new Native.NOTIFYICONDATAW { cbSize = Marshal.SizeOf<Native.NOTIFYICONDATAW>(), hWnd = _hwnd, uID = 1 };
            Native.Shell_NotifyIconW(0x02, ref notify); // NIM_DELETE
        }
        if (_menu != nint.Zero) { Native.DestroyMenu(_menu); _menu = nint.Zero; }
        if (_trayIcon != nint.Zero) { Native.DestroyIcon(_trayIcon); _trayIcon = nint.Zero; }
        if (_windowClassAtom != 0) { Native.UnregisterClassW(ClassName, nint.Zero); _windowClassAtom = 0; }
    }

    /// <summary>
    /// Removes the tray icon. Safe to call from any thread: when invoked off the tray
    /// thread it posts WM_CLOSE and lets the owning thread destroy its window.
    /// </summary>
    public void Hide()
    {
        var thread = _thread;
        if (thread is null || _hwnd == nint.Zero) return;
        if (Environment.CurrentManagedThreadId == _ownerThreadId)
        {
            Native.PostMessageW(_hwnd, 0x0010, nint.Zero, nint.Zero); // WM_CLOSE
        }
        else
        {
            Native.PostMessageW(_hwnd, 0x0010, nint.Zero, nint.Zero);
            thread.Join(2000);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Hide();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Static WndProc: routes to the right SystemTray instance via the HWND→instance map
    /// populated during WM_CREATE. DispatchMessageW calls this on the tray thread.
    /// </summary>
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == 0x0001) // WM_CREATE
        {
            var tray = _creating;
            if (tray is not null)
            {
                lock (_instancesLock) _instances[hwnd] = tray;
                tray._hwnd = hwnd;
            }
            return nint.Zero; // 0 = success
        }
        SystemTray? instance = null;
        lock (_instancesLock) _instances.TryGetValue(hwnd, out instance);
        if (instance is null) return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
        return instance.HandleMessage(hwnd, msg, wParam, lParam);
    }

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    private static class Native
    {
        internal const int IDI_APPLICATION = 32512;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct NOTIFYICONDATAW
        {
            public int cbSize;
            public nint hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public nint hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WNDCLASSEX
        {
            public int cbSize;
            public uint style;
            public nint lpfnWndProc; // function pointer (Marshal.GetFunctionPointerForDelegate)
            public int cbClsExtra;
            public int cbWndExtra;
            public nint hInstance;
            public nint hIcon;
            public nint hCursor;
            public nint hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public nint hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MSG
        {
            public nint hwnd;
            public uint message;
            public nint wParam;
            public nint lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT { public int x; public int y; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint RegisterClassExW(ref WNDCLASSEX lpwcx);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool UnregisterClassW(string className, nint hInstance);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
        [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint hwnd);
        [DllImport("user32.dll")] internal static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);
        [DllImport("user32.dll")] internal static extern void PostQuitMessage(int nExitCode);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] internal static extern bool Shell_NotifyIconW(int message, ref NOTIFYICONDATAW data);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] internal static extern uint ExtractIconEx(string file, int index, out nint large, out nint small, uint count);
        [DllImport("user32.dll")] internal static extern nint LoadIcon(nint instance, int name);
        [DllImport("user32.dll")] internal static extern bool DestroyIcon(nint handle);
        [DllImport("user32.dll")] internal static extern nint CreatePopupMenu();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool AppendMenuW(nint menu, uint flags, nint id, string text);
        [DllImport("user32.dll")] internal static extern bool DestroyMenu(nint menu);
        [DllImport("user32.dll")] internal static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
        [DllImport("user32.dll")] internal static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hwnd);
        [DllImport("user32.dll")] internal static extern int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);
        [DllImport("user32.dll")] internal static extern bool PostMessageW(nint hwnd, uint msg, nint w, nint l);
        [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint DispatchMessageW(ref MSG msg);
    }
}
