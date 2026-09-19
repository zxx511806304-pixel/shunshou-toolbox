using static Shunshou.App.RecordingNative;

namespace Shunshou.App;

/// <summary>Live, screenshot-free selection on one physical display.</summary>
public static class ScreenRecordingPicker
{
    private static int _active;

    /// <summary>Returns physical pixels relative to the supplied display origin, or null when cancelled.</summary>
    public static Task<(int X, int Y, int Width, int Height)?> SelectAsync(
        nint owner, int left, int top, int width, int height)
    {
        if (width < 64 || height < 64 || width > short.MaxValue || height > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width), "显示器尺寸不支持区域选择。");
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            throw new InvalidOperationException("请先完成当前的录制区域选择。");

        var session = new Selection(owner, left, top, width, height);
        var thread = new Thread(session.Run) { IsBackground = true, Name = "Shunshou recording region" };
        thread.SetApartmentState(ApartmentState.STA);
        try { thread.Start(); }
        catch { Interlocked.Exchange(ref _active, 0); throw; }
        return session.Completion.Task;
    }

    private sealed class Selection(nint owner, int left, int top, int width, int height)
    {
        internal readonly TaskCompletionSource<(int X, int Y, int Width, int Height)?> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _className = "ShunshouRecordingRegion_" + Guid.NewGuid().ToString("N");
        private WindowProc? _procedure;
        private nint _window, _cursor, _font, _background, _transparentBrush, _pen;
        private nint _buffer, _bitmap, _oldBitmap;
        private bool _dragging;
        private int _startX, _startY;
        private Rect _selection;
        private double _scale = 1;
        private string _hint = "拖动选择录制区域 · Esc 或右键取消";
        private (int X, int Y, int Width, int Height)? _result;
        private Exception? _failure;
        private const uint ColorKey = 0x00ff00ff;

        internal void Run()
        {
            ushort atom = 0;
            var oldDpi = SetThreadDpiAwarenessContext(-4); // PER_MONITOR_AWARE_V2: all coordinates stay physical.
            try
            {
                if (oldDpi == 0) throw Error("无法使用显示器的实际像素选择录制区域");
                _cursor = LoadCursor(0, 32515); // IDC_CROSS, shared cursor: do not destroy.
                _procedure = HandleMessage;
                atom = Register(_className, _procedure, _cursor);
                _window = CreateWindowEx(0x00080088, _className, "顺手工具箱 · 选择录制区域", 0x80000000,
                    left, top, width, height, owner, 0, GetModuleHandle(null), 0);
                if (_window == 0) throw Error("无法打开录制区域选择器");
                _scale = Math.Max(1, GetDpiForWindow(_window) / 96.0);
                _font = CreateFont(-Dip(18), 0, 0, 0, 500, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
                _background = CreateSolidBrush(0x00181818);
                _transparentBrush = CreateSolidBrush(ColorKey);
                _pen = CreatePen(0, Dip(2), GetSysColor(13)); // System highlight, including contrast themes.
                if (_font == 0 || _background == 0 || _transparentBrush == 0 || _pen == 0)
                    throw Error("无法绘制录制区域选择器");
                if (!SetLayeredWindowAttributes(_window, ColorKey, 175, 3))
                    throw Error("无法打开透明区域选择器");
                ShowWindow(_window, 5);
                SetForegroundWindow(_window);
                SetFocus(_window);
                UpdateWindow(_window);
                Pump();
            }
            catch (Exception error) { _failure ??= error; }
            finally
            {
                if (IsWindow(_window)) DestroyWindow(_window);
                if (_buffer != 0 && _oldBitmap != 0) SelectObject(_buffer, _oldBitmap);
                if (_bitmap != 0) DeleteObject(_bitmap);
                if (_buffer != 0) DeleteDC(_buffer);
                foreach (var value in new[] { _font, _background, _transparentBrush, _pen })
                    if (value != 0) DeleteObject(value);
                if (atom != 0) UnregisterClass(_className, GetModuleHandle(null));
                if (oldDpi != 0) SetThreadDpiAwarenessContext(oldDpi);
                if (owner != 0 && IsWindow(owner)) SetForegroundWindow(owner);
                GC.KeepAlive(_procedure);
                Interlocked.Exchange(ref _active, 0);
                if (_failure is null) Completion.TrySetResult(_result);
                else Completion.TrySetException(_failure);
            }
        }

        private nint HandleMessage(nint hwnd, uint message, nuint wParam, nint lParam)
        {
            try
            {
                switch (message)
                {
                    case 0x000f: // WM_PAINT
                        PaintWindow(hwnd);
                        return 0;
                    case 0x0014: return 1; // WM_ERASEBKGND: paint the complete back buffer.
                    case 0x0020: SetCursor(_cursor); return 1;
                    case 0x0201: // WM_LBUTTONDOWN
                        (_startX, _startY) = Coordinates(lParam);
                        _dragging = true;
                        _selection = new Rect(_startX, _startY, _startX, _startY);
                        SetCapture(hwnd);
                        InvalidateRect(hwnd, 0, false);
                        return 0;
                    case 0x0200 when _dragging: // WM_MOUSEMOVE
                        UpdateSelection(lParam);
                        InvalidateRect(hwnd, 0, false);
                        return 0;
                    case 0x0202 when _dragging: // WM_LBUTTONUP
                        UpdateSelection(lParam);
                        _dragging = false;
                        ReleaseCapture();
                        if (_selection.Width >= 64 && _selection.Height >= 64)
                        {
                            _result = (_selection.Left, _selection.Top, _selection.Width, _selection.Height);
                            DestroyWindow(hwnd);
                        }
                        else
                        {
                            _selection = default;
                            _hint = "区域至少为 64 × 64 像素，请重新拖动 · Esc 取消";
                            InvalidateRect(hwnd, 0, false);
                        }
                        return 0;
                    case 0x0215 when _dragging: // WM_CAPTURECHANGED: a cancelled drag must not be accepted.
                        _dragging = false;
                        _selection = default;
                        InvalidateRect(hwnd, 0, false);
                        return 0;
                    case 0x0100 when wParam == 0x1b: // Escape
                    case 0x0204: // Right button
                    case 0x0010: // Close / Alt+F4
                        DestroyWindow(hwnd);
                        return 0;
                    case 0x0006 when (wParam & 0xffff) == 0: // Don't strand a topmost overlay after Alt+Tab.
                        DestroyWindow(hwnd);
                        return 0;
                    case 0x0082: // WM_NCDESTROY
                        PostQuitMessage(0);
                        break;
                }
                return DefWindowProc(hwnd, message, wParam, lParam);
            }
            catch (Exception error)
            {
                // Never allow a managed exception to cross the native callback boundary.
                _failure ??= error;
                DestroyWindow(hwnd);
                return 0;
            }
        }

        private (int X, int Y) Coordinates(nint packed) =>
            (Math.Clamp(unchecked((short)(long)packed), 0, width),
             Math.Clamp(unchecked((short)((long)packed >> 16)), 0, height));

        private void UpdateSelection(nint packed)
        {
            var (x, y) = Coordinates(packed);
            _selection = new Rect(Math.Min(_startX, x), Math.Min(_startY, y), Math.Max(_startX, x), Math.Max(_startY, y));
        }

        private int Dip(double value) => (int)Math.Round(value * _scale);

        private void PaintWindow(nint hwnd)
        {
            var dc = BeginPaint(hwnd, out var paint);
            try
            {
                if (dc == 0 || _background == 0) return;
                if (_buffer == 0)
                {
                    _buffer = CreateCompatibleDC(dc);
                    _bitmap = CreateCompatibleBitmap(dc, width, height);
                    if (_buffer == 0 || _bitmap == 0) throw Error("无法绘制录制区域选择器");
                    _oldBitmap = SelectObject(_buffer, _bitmap);
                    if (_oldBitmap == 0 || _oldBitmap == -1) throw Error("无法绘制录制区域选择器");
                }
                var screen = new Rect(0, 0, width, height);
                FillRect(_buffer, ref screen, _background);
                var oldFont = SelectObject(_buffer, _font);
                var oldPen = SelectObject(_buffer, _pen);
                var oldBrush = SelectObject(_buffer, GetStockObject(5)); // NULL_BRUSH is shared.
                try
                {
                    SetBkMode(_buffer, 1);
                    SetTextColor(_buffer, 0x00ffffff);
                    var hint = new Rect(Dip(16), Dip(24), width - Dip(16), Dip(72));
                    DrawText(_buffer, _hint, -1, ref hint, 0x00000001 | 0x00000004 | 0x00000020);
                    if (_selection.Width > 0 && _selection.Height > 0)
                    {
                        var area = _selection;
                        FillRect(_buffer, ref area, _transparentBrush);
                        Rectangle(_buffer, area.Left, area.Top, area.Right, area.Bottom);
                        var labelTop = area.Bottom + Dip(6);
                        if (labelTop + Dip(30) > height) labelTop = Math.Max(0, area.Top - Dip(36));
                        var label = new Rect(Math.Max(Dip(8), Math.Min(area.Left, width - Dip(240))), labelTop,
                            width - Dip(8), Math.Min(height, labelTop + Dip(30)));
                        DrawText(_buffer, $"{area.Width} × {area.Height} 像素", -1, ref label, 0x00000020);
                    }
                    BitBlt(dc, 0, 0, width, height, _buffer, 0, 0, 0x00cc0020);
                }
                finally
                {
                    SelectObject(_buffer, oldFont);
                    SelectObject(_buffer, oldPen);
                    SelectObject(_buffer, oldBrush);
                }
            }
            finally { EndPaint(hwnd, ref paint); }
        }
    }
}
