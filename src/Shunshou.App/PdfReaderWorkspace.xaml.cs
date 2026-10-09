using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Shunshou.Core;
using Windows.Storage.Streams;
using Windows.System;

namespace Shunshou.App;

/// <summary>
/// Lightweight in-place PDF reader: renders only the current page (with the immediate neighbours
/// cached), never writes anything, and hands the document over to other PDF tools on request.
/// </summary>
public sealed partial class PdfReaderWorkspace : UserControl
{
    private static readonly int[] ZoomSteps = [50, 75, 100, 125, 150, 200, 250, 300];

    private readonly Dictionary<uint, PdfRenderedPage> _cache = [];
    private string? _document;
    private int _pageCount;
    private uint _page;
    private int _zoom = 100;
    private double _pageWidthDip;
    private bool _busy;
    private int _renderSerial;
    private bool _syncingJump;

    public PdfReaderWorkspace()
    {
        InitializeComponent();
        var previous = new KeyboardAccelerator { Key = VirtualKey.Left };
        previous.Invoked += (_, args) => { if (TryHandleArrow()) args.Handled = true; };
        void Register(Microsoft.UI.Xaml.Input.KeyboardAccelerator accelerator) => KeyboardAccelerators.Add(accelerator);
        Register(previous);
        var next = new KeyboardAccelerator { Key = VirtualKey.Right };
        next.Invoked += (_, args) => { if (TryHandleArrow(forward: true)) args.Handled = true; };
        Register(next);
        // Ctrl+wheel zooms; plain wheel keeps scrolling the page.
        PageScroll.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(PageScroll_PointerWheelChanged), true);

        bool TryHandleArrow(bool forward = false)
        {
            if (_busy || _pageCount == 0) return false;
            if (FocusManager.GetFocusedElement(XamlRoot) is NumberBox) return false;
            if (forward) { if (_page + 1 >= _pageCount) return false; GoToPage(_page + 1); }
            else { if (_page == 0) return false; GoToPage(_page - 1); }
            return true;
        }
    }

    public nint HostWindowHandle { get; set; }
    /// <summary>Raised when the user wants to pick a PDF; the window owns the file picker.</summary>
    public event EventHandler? PickRequested;
    /// <summary>Raised when the user asks to continue with another PDF tool; the argument is the operation name.</summary>
    public event EventHandler<string>? ToolRequested;

    internal string? DocumentPath => _document;
    internal int PageCount => _pageCount;
    internal int CurrentPage => _pageCount == 0 ? 0 : (int)_page + 1;
    internal int ZoomPercent => _zoom;

    internal async Task SetDocumentAsync(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            _document = null;
            _pageCount = 0;
            _page = 0;
            _cache.Clear();
            _renderSerial++;
            PageImage.Source = null;
            DocumentTitle.Text = "拖入或添加一份 PDF 开始阅读";
            DocumentDetail.Text = "翻页、缩放与跳转只在本机渲染；底部可跳转到其它 PDF 工具继续处理。";
            Notice.IsOpen = false;
            UpdateState();
            return;
        }
        if (StringComparer.OrdinalIgnoreCase.Equals(_document, path)) return;
        _document = path;
        _cache.Clear();
        _renderSerial++;
        PageImage.Source = null;
        DocumentTitle.Text = Path.GetFileName(path);
        DocumentDetail.Text = "正在读取…";
        try
        {
            _pageCount = await PdfService.CountPagesAsync(path);
            _page = 0;
            DocumentDetail.Text = $"{_pageCount} 页 · Ctrl+滚轮缩放，←/→ 翻页";
        }
        catch (Exception ex)
        {
            _document = null;
            _pageCount = 0;
            DocumentTitle.Text = "无法打开这份 PDF";
            DocumentDetail.Text = "请选择其它 PDF 文件。";
            ShowNotice(ex.Message, InfoBarSeverity.Error);
        }
        UpdateState();
        if (_pageCount > 0) await ShowCurrentPageAsync();
    }

    private void UpdateState()
    {
        bool loaded = _pageCount > 0 && !_busy;
        EmptyHint.Visibility = _pageCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        PrevButton.IsEnabled = loaded && _page > 0;
        NextButton.IsEnabled = loaded && _page + 1 < _pageCount;
        _syncingJump = true;
        try
        {
            JumpBox.IsEnabled = loaded;
            JumpBox.Maximum = Math.Max(1, _pageCount);
            JumpBox.Value = _pageCount == 0 ? 1 : _page + 1;
        }
        finally { _syncingJump = false; }
        ZoomOutButton.IsEnabled = loaded && _zoom > ZoomSteps[0];
        ZoomInButton.IsEnabled = loaded && _zoom < ZoomSteps[^1];
        FitWidthButton.IsEnabled = loaded && _pageWidthDip > 0;
        ZoomLabel.Text = _zoom + "%";
        PageLabel.Text = _pageCount == 0 ? "0 / 0" : $"{_page + 1} / {_pageCount}";
        bool toolReady = _pageCount > 0 && !_busy;
        MergeToolButton.IsEnabled = SplitToolButton.IsEnabled = OrganizeToolButton.IsEnabled
            = WatermarkToolButton.IsEnabled = CompressToolButton.IsEnabled = toolReady;
    }

    private void GoToPage(uint page)
    {
        if (_pageCount == 0 || page >= _pageCount || page == _page) return;
        _page = page;
        UpdateState();
        _ = ShowCurrentPageAsync();
    }

    private async Task ShowCurrentPageAsync()
    {
        string? path = _document;
        if (path is null || _pageCount == 0) return;
        int serial = ++_renderSerial;
        _busy = true;
        UpdateState();
        Rendering.IsActive = true;
        Notice.IsOpen = false;
        try
        {
            if (!_cache.TryGetValue(_page, out var rendered))
            {
                rendered = await PdfService.RenderPagePngAsync(path, _page, Dpi(), CancellationToken.None);
                _cache[_page] = rendered;
            }
            if (serial != _renderSerial) return;
            var bitmap = new BitmapImage();
            using (var stream = new InMemoryRandomAccessStream())
            {
                await stream.WriteAsync(rendered.Png.AsBuffer());
                stream.Seek(0);
                await bitmap.SetSourceAsync(stream);
            }
            _pageWidthDip = rendered.WidthDip;
            PageImage.Width = rendered.WidthDip * _zoom / 100.0;
            PageImage.Height = rendered.HeightDip * _zoom / 100.0;
            PageImage.Source = bitmap;
            _ = PrefetchAsync(path, serial);
        }
        catch (Exception ex)
        {
            if (serial == _renderSerial) ShowNotice(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            Rendering.IsActive = false;
            _busy = false;
            UpdateState();
        }
    }

    private int Dpi() => Math.Clamp(96 * _zoom / 100, 48, 600);

    /// <summary>Warms the cache with the immediate neighbours so a single step never re-renders.</summary>
    private async Task PrefetchAsync(string path, int serial)
    {
        try
        {
            foreach (uint index in new[] { _page > 0 ? _page - 1 : uint.MaxValue, _page + 1 })
            {
                if (index >= _pageCount || _cache.ContainsKey(index)) continue;
                var rendered = await PdfService.RenderPagePngAsync(path, index, Dpi(), CancellationToken.None);
                if (serial != _renderSerial) return;
                _cache[index] = rendered;
            }
            // Keep only the current page and its neighbours; drop anything stale.
            foreach (uint key in _cache.Keys.Where(key => key + 1 < _page || key > _page + 1).ToList())
                _cache.Remove(key);
        }
        catch (Exception) { /* Prefetch failures are silent; the page is rendered on demand. */ }
    }

    private void ApplyZoom(int zoom)
    {
        zoom = Math.Clamp(zoom, ZoomSteps[0], ZoomSteps[^1]);
        if (zoom == _zoom || _pageCount == 0) { UpdateState(); return; }
        _zoom = zoom;
        _cache.Clear();
        UpdateState();
        _ = ShowCurrentPageAsync();
    }

    private void Prev_Click(object sender, RoutedEventArgs e) { if (_page > 0) GoToPage(_page - 1); }
    private void Next_Click(object sender, RoutedEventArgs e) { if (_page + 1 < _pageCount) GoToPage(_page + 1); }
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ApplyZoom(ZoomSteps.Where(step => step < _zoom).DefaultIfEmpty(ZoomSteps[0]).Max());
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ApplyZoom(ZoomSteps.Where(step => step > _zoom).DefaultIfEmpty(ZoomSteps[^1]).Min());

    private void FitWidth_Click(object sender, RoutedEventArgs e)
    {
        if (_pageWidthDip <= 0 || PageScroll.ViewportWidth <= 0) return;
        ApplyZoom((int)Math.Floor((PageScroll.ViewportWidth - 24) / _pageWidthDip * 100));
    }

    private void Jump_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingJump || _pageCount == 0 || !double.IsFinite(args.NewValue)) return;
        int target = Math.Clamp((int)Math.Round(args.NewValue), 1, _pageCount);
        GoToPage((uint)(target - 1));
    }

    private void PageScroll_PointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        if (_pageCount == 0) return;
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        if (!state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        args.Handled = true;
        var point = args.GetCurrentPoint(PageScroll);
        if (point.Properties.MouseWheelDelta > 0) ZoomIn_Click(sender, args);
        else if (point.Properties.MouseWheelDelta < 0) ZoomOut_Click(sender, args);
    }

    private void Pick_Click(object sender, RoutedEventArgs e) => PickRequested?.Invoke(this, EventArgs.Empty);

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string operation } && _document is not null)
            ToolRequested?.Invoke(this, operation);
    }

    private void ShowNotice(string message, InfoBarSeverity severity)
    {
        Notice.Title = "PDF 阅读";
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }
}
