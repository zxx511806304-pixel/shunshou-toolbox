using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Shunshou.Core;
using Windows.Graphics;

namespace Shunshou.App;

public sealed partial class ScreenRecordingBar : Window
{
    public event EventHandler? TogglePauseRequested;
    public event EventHandler? StopRequested;
    private bool _allowClose;
    public bool ExcludedFromCapture { get; }
    internal FrameworkElement CaptureRoot => Root;

    public ScreenRecordingBar(ScreenRecordingBounds display, ElementTheme theme)
    {
        InitializeComponent();
        Root.RequestedTheme = theme;
        Title = "顺手工具箱 · 正在录屏";
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Move(new PointInt32(display.X + display.Width / 2 - 180, display.Y + 20));
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(hwnd) / 96d;
        int width = (int)(360 * scale), height = (int)(72 * scale);
        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(display.X + (display.Width - width) / 2, display.Y + 20));
        ExcludedFromCapture = SetWindowDisplayAffinity(hwnd, 0x11);
        AppWindow.Closing += (_, args) => { if (!_allowClose) { args.Cancel = true; StopRequested?.Invoke(this, EventArgs.Empty); } };
    }

    public void Update(ScreenRecordingState state, TimeSpan elapsed)
    {
        TimeText.Text = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        PauseButton.Content = state == ScreenRecordingState.Paused ? "继续" : "暂停";
        PauseButton.IsEnabled = state is ScreenRecordingState.Recording or ScreenRecordingState.Paused;
        StopButton.IsEnabled = state != ScreenRecordingState.Stopping;
    }
    public void Shutdown() { _allowClose = true; Close(); }
    private void Pause_Click(object sender, RoutedEventArgs args) => TogglePauseRequested?.Invoke(this, EventArgs.Empty);
    private void Stop_Click(object sender, RoutedEventArgs args) => StopRequested?.Invoke(this, EventArgs.Empty);
    private void Drag_Pressed(object sender, PointerRoutedEventArgs args)
    {
        if (!args.GetCurrentPoint(DragArea).Properties.IsLeftButtonPressed) return;
        ReleaseCapture();
        SendMessage(WinRT.Interop.WindowNative.GetWindowHandle(this), 0xA1, 2, 0);
    }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);
}
