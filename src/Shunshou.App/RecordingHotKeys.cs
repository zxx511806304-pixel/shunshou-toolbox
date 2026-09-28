using static Shunshou.App.RecordingNative;

namespace Shunshou.App;

/// <summary>Session-scoped Ctrl+Alt+F9/F10 registration. Events run on this helper's message thread.</summary>
public sealed class RecordingHotKeys : IDisposable
{
    public event EventHandler? TogglePauseRequested;
    public event EventHandler? StopRequested;

    private readonly TaskCompletionSource<string?> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private nint _window;
    private int _disposed;
    private const int PauseId = 0x5179, StopId = 0x517a;

    private RecordingHotKeys()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Shunshou recording shortcuts" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>If either key is already in use, neither key remains registered.</summary>
    public static bool TryCreate(out RecordingHotKeys? hotKeys, out string? error)
    {
        var candidate = new RecordingHotKeys();
        try
        {
            candidate._thread.Start();
            error = candidate._ready.Task.GetAwaiter().GetResult();
        }
        catch (Exception exception) { error = "无法启用录屏快捷键：" + exception.Message; }
        if (error is not null)
        {
            candidate.Dispose();
            hotKeys = null;
            return false;
        }
        hotKeys = candidate;
        return true;
    }

    private void Run()
    {
        var className = "ShunshouRecordingHotKeys_" + Guid.NewGuid().ToString("N");
        WindowProc procedure = HandleMessage;
        ushort atom = 0;
        var pauseRegistered = false;
        var stopRegistered = false;
        try
        {
            atom = Register(className, procedure);
            _window = CreateWindowEx(0, className, "", 0, 0, 0, 0, 0, -3, 0, GetModuleHandle(null), 0);
            if (_window == 0) throw Error("无法创建快捷键接收窗口");
            pauseRegistered = RegisterHotKey(_window, PauseId, 0x4003, 0x78);
            if (!pauseRegistered) throw Error("Ctrl + Alt + F9 已被占用，录屏期间请使用窗口中的暂停和停止按钮。");
            stopRegistered = RegisterHotKey(_window, StopId, 0x4003, 0x79);
            if (!stopRegistered) throw Error("Ctrl + Alt + F10 已被占用，录屏期间请使用窗口中的暂停和停止按钮。");
            _ready.TrySetResult(null);
            Pump();
        }
        catch (Exception error) { _ready.TrySetResult(error.Message); }
        finally
        {
            if (pauseRegistered) UnregisterHotKey(_window, PauseId);
            if (stopRegistered) UnregisterHotKey(_window, StopId);
            if (IsWindow(_window)) DestroyWindow(_window);
            _window = 0;
            if (atom != 0) UnregisterClass(className, GetModuleHandle(null));
            GC.KeepAlive(procedure);
            _ready.TrySetResult("无法启用录屏快捷键，请使用窗口中的暂停和停止按钮。");
        }
    }

    private nint HandleMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case 0x0312 when Volatile.Read(ref _disposed) == 0:
                try
                {
                    if (wParam == PauseId) TogglePauseRequested?.Invoke(this, EventArgs.Empty);
                    else if (wParam == StopId) StopRequested?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
                return 0;
            case 0x0010:
                // Unregister while the associated HWND still exists.
                UnregisterHotKey(hwnd, PauseId);
                UnregisterHotKey(hwnd, StopId);
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
