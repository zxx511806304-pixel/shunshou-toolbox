using static Shunshou.App.RecordingNative;

namespace Shunshou.App;

/// <summary>App-lifetime global shortcut (Alt+Space, falling back to Ctrl+Alt+Space) on its own message thread.</summary>
public sealed class QuickLauncherHotKeys : IDisposable
{
    public event EventHandler? ToggleRequested;

    private readonly TaskCompletionSource<string?> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private nint _window;
    private int _disposed;
    private const int ToggleId = 0x5181;

    private QuickLauncherHotKeys()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Shunshou quick launcher shortcut" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>The shortcut that was actually registered, for display in the launcher box.</summary>
    public string ShortcutLabel { get; private set; } = "Alt + Space";

    /// <summary>Null when neither shortcut is free; the rest of the app keeps working without one.</summary>
    public static QuickLauncherHotKeys? TryCreate()
    {
        var candidate = new QuickLauncherHotKeys();
        try
        {
            candidate._thread.Start();
            if (candidate._ready.Task.GetAwaiter().GetResult() is null)
            {
                candidate.Dispose();
                return null;
            }
            return candidate;
        }
        catch (Exception exception)
        {
            App.LogException(exception, "quick-launcher-hotkeys");
            candidate.Dispose();
            return null;
        }
    }

    private void Run()
    {
        var className = "ShunshouQuickLauncher_" + Guid.NewGuid().ToString("N");
        WindowProc procedure = HandleMessage;
        ushort atom = 0;
        var registered = false;
        try
        {
            atom = Register(className, procedure);
            _window = CreateWindowEx(0, className, "", 0, 0, 0, 0, 0, -3, 0, GetModuleHandle(null), 0);
            if (_window == 0) throw Error("无法创建快捷键接收窗口");
            // MOD_NOREPEAT | MOD_ALT, VK_SPACE; fall back when another launcher already owns Alt+Space.
            if (RegisterHotKey(_window, ToggleId, 0x4001, 0x20)) ShortcutLabel = "Alt + Space";
            else if (RegisterHotKey(_window, ToggleId, 0x4003, 0x20)) ShortcutLabel = "Ctrl + Alt + Space";
            else throw Error("Alt + Space 与 Ctrl + Alt + Space 均已被占用");
            registered = true;
            _ready.TrySetResult(ShortcutLabel);
            Pump();
        }
        catch (Exception) { _ready.TrySetResult(null); }
        finally
        {
            if (registered) UnregisterHotKey(_window, ToggleId);
            if (IsWindow(_window)) DestroyWindow(_window);
            _window = 0;
            if (atom != 0) UnregisterClass(className, GetModuleHandle(null));
            GC.KeepAlive(procedure);
            _ready.TrySetResult(null);
        }
    }

    private nint HandleMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case 0x0312 when Volatile.Read(ref _disposed) == 0:
                try
                {
                    if (wParam == ToggleId) ToggleRequested?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
                return 0;
            case 0x0010:
                // Unregister while the associated HWND still exists.
                UnregisterHotKey(hwnd, ToggleId);
                DestroyWindow(hwnd);
                return 0;
            case 0x0082:
                PostQuitMessage(0);
                break;
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var window = _window;
        if (window != 0) PostMessage(window, 0x0010, 0, 0);
        if (Thread.CurrentThread != _thread && _thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
        GC.SuppressFinalize(this);
    }
}
