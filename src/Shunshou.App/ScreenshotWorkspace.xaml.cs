using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Shunshou.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;

namespace Shunshou.App;

/// <summary>Lets the host window hide itself, run the full-screen picker, and hand back the cropped PNG path.</summary>
public sealed class ScreenshotCaptureRequest : EventArgs
{
    internal Func<Task<string?>>? PerformAsync { get; set; }
}

/// <summary>
/// Annotate a freshly captured screen region: rectangles, arrows, mosaics, sequence numbers and text,
/// all rendered locally on top of the snapshot. Nothing is uploaded; the source capture is never modified.
/// </summary>
public sealed partial class ScreenshotWorkspace : UserControl
{
    private static readonly (string Hex, string Name)[] Palette =
    [
        ("#E81123", "红"), ("#FF8C00", "橙"), ("#FFD700", "黄"), ("#107C10", "绿"),
        ("#0078D4", "蓝"), ("#5C2D91", "紫"), ("#000000", "黑"), ("#FFFFFF", "白")
    ];

    private readonly ScreenshotAnnotationService _annotation = new();
    private readonly List<ToggleButton> _colorButtons = [];
    private string? _imagePath;
    private int _imageWidth, _imageHeight;
    private double _scale = 1;
    private bool _busy, _dragging, _textDialogOpen;
    private ScreenshotTool _tool = ScreenshotTool.Rectangle;
    private string _color = Palette[0].Hex;
    private int _nextNumber = 1;
    private double _startX, _startY, _endX, _endY; // image pixels
    private UIElement? _preview;

    public ScreenshotWorkspace()
    {
        InitializeComponent();
        bool first = true;
        foreach (var (hex, name) in Palette)
        {
            var swatch = new ToggleButton
            {
                Width = 30, Height = 30, Padding = new Thickness(3), Tag = hex, IsChecked = first,
                Content = new Border
                {
                    Background = BrushOf(hex),
                    BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 128, 128, 128)),
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4)
                }
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(swatch, name + "色");
            swatch.Click += Color_Click;
            ColorPanel.Children.Add(swatch);
            _colorButtons.Add(swatch);
            first = false;
        }
        RegisterAccelerator(VirtualKey.Z, UndoButton, () => Undo());
        RegisterAccelerator(VirtualKey.Y, RedoButton, () => Redo());

        void RegisterAccelerator(VirtualKey key, Button button, Action action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
            accelerator.Invoked += (_, args) =>
            {
                if (_busy || !button.IsEnabled || FocusManager.GetFocusedElement(XamlRoot) is TextBox or NumberBox) return;
                action();
                args.Handled = true;
            };
            KeyboardAccelerators.Add(accelerator);
        }
        UpdateState();
    }

    public nint HostWindowHandle { get; set; }
    public event EventHandler<bool>? BusyChanged;
    /// <summary>The host hides its window, runs the overlay selection, and resolves the cropped PNG path.</summary>
    public event EventHandler<ScreenshotCaptureRequest>? CaptureRequested;

    internal bool IsBusy => _busy;
    internal string? ImagePath => _imagePath;
    internal int ShapeCount => _annotation.Shapes.Count;

    private double Stroke => double.IsNaN(StrokeBox.Value) ? 3 : Math.Clamp(StrokeBox.Value, 1, 16);
    private double MarkerRadius() => Math.Clamp(Math.Min(_imageWidth, _imageHeight) / 55.0, 12, 48);
    private double DefaultFontSize() => Math.Clamp(Math.Min(_imageWidth, _imageHeight) / 24.0, 16, 120);
    private int MosaicBlockSize() => (int)Math.Clamp(Math.Min(_imageWidth, _imageHeight) / 80.0, 8, 40);

    internal async Task SetImageAsync(string path)
    {
        var bitmap = await ImagePreview.FromFileAsync(path);
        if (bitmap is null || bitmap.PixelWidth == 0) throw new InvalidDataException("无法读取这张截图。");
        _imagePath = path;
        _imageWidth = bitmap.PixelWidth;
        _imageHeight = bitmap.PixelHeight;
        _annotation.Clear();
        _nextNumber = 1;
        RemovePreview();
        SnapshotImage.Source = bitmap;
        EmptyHint.Visibility = Visibility.Collapsed;
        Notice.IsOpen = false;
        _scale = 0;
        FitScale();
        ApplyCanvasSize();
        RedrawShapes();
        StatusText.Text = $"{_imageWidth} × {_imageHeight} · 拖拽或点击画面添加标注";
        UpdateState();
    }

    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var request = new ScreenshotCaptureRequest();
        CaptureRequested?.Invoke(this, request);
        if (request.PerformAsync is null) return;
        Notice.IsOpen = false;
        SetBusy(true);
        try
        {
            string? path = await request.PerformAsync();
            if (path is null)
            {
                if (_imagePath is null) StatusText.Text = "已取消截图，点击“截图”重新开始。";
                return;
            }
            await SetImageAsync(path);
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { SetBusy(false); }
    }

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag }) return;
        _tool = Enum.Parse<ScreenshotTool>(tag);
        foreach (var button in new[] { RectTool, ArrowTool, MosaicTool, SequenceTool, TextTool })
            button.IsChecked = ReferenceEquals(button, sender);
    }

    private void Color_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string hex }) return;
        _color = hex;
        foreach (var button in _colorButtons) button.IsChecked = ReferenceEquals(button, sender);
    }

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_busy || _textDialogOpen || _imagePath is null) return;
        var point = e.GetCurrentPoint(AnnotCanvas);
        if (!point.Properties.IsLeftButtonPressed) return;
        double x = Math.Clamp(point.Position.X / _scale, 0, _imageWidth);
        double y = Math.Clamp(point.Position.Y / _scale, 0, _imageHeight);
        e.Handled = true;
        switch (_tool)
        {
            case ScreenshotTool.Sequence:
                if (Commit(new ScreenshotSequenceShape(x, y, _nextNumber, MarkerRadius(), _color)) && _nextNumber < 999) _nextNumber++;
                break;
            case ScreenshotTool.Text:
                _ = PlaceTextAsync(x, y);
                break;
            default:
                _dragging = true;
                _startX = _endX = x;
                _startY = _endY = y;
                AnnotCanvas.CapturePointer(e.Pointer);
                break;
        }
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var position = e.GetCurrentPoint(AnnotCanvas).Position;
        _endX = Math.Clamp(position.X / _scale, 0, _imageWidth);
        _endY = Math.Clamp(position.Y / _scale, 0, _imageHeight);
        ShowPreview();
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        EndDrag();
        ScreenshotShape shape = _tool switch
        {
            ScreenshotTool.Arrow => new ScreenshotArrowShape(_startX, _startY, _endX, _endY, _color, Stroke),
            ScreenshotTool.Mosaic => new ScreenshotMosaicShape(RectX(), RectY(), RectWidth(), RectHeight(), MosaicBlockSize()),
            _ => new ScreenshotRectangleShape(RectX(), RectY(), RectWidth(), RectHeight(), _color, Stroke)
        };
        Commit(shape);
    }

    private void Canvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        RemovePreview();
    }

    private double RectX() => Math.Min(_startX, _endX);
    private double RectY() => Math.Min(_startY, _endY);
    private double RectWidth() => Math.Abs(_endX - _startX);
    private double RectHeight() => Math.Abs(_endY - _startY);

    private async Task PlaceTextAsync(double x, double y)
    {
        var box = new TextBox { PlaceholderText = "输入要添加的文字", MaxLength = 500, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "添加文字", Content = box,
            PrimaryButtonText = "添加", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary
        };
        _textDialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(box.Text)) return;
        }
        finally { _textDialogOpen = false; }
        Commit(new ScreenshotTextShape(x, y, box.Text.Trim(), DefaultFontSize(), _color));
    }

    private bool Commit(ScreenshotShape shape)
    {
        try { _annotation.Add(shape); }
        catch (Exception ex) { ShowError(ex.Message); return false; }
        RedrawShapes();
        UpdateState();
        return true;
    }

    private void Undo() => Undo_Click(this, new RoutedEventArgs());
    private void Redo() => Redo_Click(this, new RoutedEventArgs());

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_annotation.Undo() is ScreenshotSequenceShape sequence && sequence.Number == _nextNumber - 1) _nextNumber--;
        RedrawShapes();
        UpdateState();
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_annotation.Redo() is ScreenshotSequenceShape sequence && sequence.Number == _nextNumber) _nextNumber++;
        RedrawShapes();
        UpdateState();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _annotation.Clear();
        _nextNumber = 1;
        RedrawShapes();
        UpdateState();
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_imagePath is null || _busy) return;
        SetBusy(true);
        try
        {
            string temp = Path.Combine(Path.GetTempPath(), "Shunshou", "screenshot-copy-" + Guid.NewGuid().ToString("N") + ".png");
            string rendered = await Task.Run(() => ScreenshotAnnotationService.RenderAndSave(_imagePath, _annotation.Shapes, temp));
            byte[] bytes = await File.ReadAllBytesAsync(rendered);
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var package = new DataPackage();
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            Clipboard.SetContent(package);
            Clipboard.Flush();
            StatusText.Text = "已复制到剪贴板。";
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { SetBusy(false); }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_imagePath is null || _busy) return;
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, SuggestedFileName = "截图 " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss") };
        picker.FileTypeChoices.Add("PNG 图片", [".png"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, HostWindowHandle);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        SetBusy(true);
        try
        {
            string output = await Task.Run(() => ScreenshotAnnotationService.RenderAndSave(_imagePath, _annotation.Shapes, file.Path));
            StatusText.Text = "已保存 · " + Path.GetFileName(output);
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { SetBusy(false); }
    }

    private void CanvasScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_imagePath is null) return;
        if (FitScale()) { ApplyCanvasSize(); RedrawShapes(); }
    }

    /// <summary>Fits the whole snapshot into the viewport without upscaling. Returns true when the scale changed.</summary>
    private bool FitScale()
    {
        if (_imageWidth == 0) return false;
        double width = CanvasScroll.ViewportWidth > 0 ? CanvasScroll.ViewportWidth : CanvasScroll.ActualWidth;
        double height = CanvasScroll.ViewportHeight > 0 ? CanvasScroll.ViewportHeight : CanvasScroll.ActualHeight;
        if (width <= 0 || height <= 0) return false;
        double scale = Math.Clamp(Math.Min(width / _imageWidth, height / _imageHeight), 0.02, 1);
        if (Math.Abs(scale - _scale) < 0.0005) return false;
        _scale = scale;
        return true;
    }

    private void ApplyCanvasSize()
    {
        AnnotCanvas.Width = SnapshotImage.Width = _imageWidth * _scale;
        AnnotCanvas.Height = SnapshotImage.Height = _imageHeight * _scale;
    }

    private void ShowPreview()
    {
        RemovePreview();
        _preview = _tool switch
        {
            ScreenshotTool.Arrow => ArrowElement(new ScreenshotArrowShape(_startX, _startY, _endX, _endY, _color, Stroke)),
            ScreenshotTool.Mosaic => MosaicElement(new ScreenshotMosaicShape(RectX(), RectY(), RectWidth(), RectHeight(), MosaicBlockSize())),
            _ => RectElement(RectX(), RectY(), RectWidth(), RectHeight(), _color, Stroke)
        };
        AnnotCanvas.Children.Add(_preview);
    }

    private void RemovePreview()
    {
        if (_preview is not null) AnnotCanvas.Children.Remove(_preview);
        _preview = null;
    }

    private void RedrawShapes()
    {
        var preview = _preview;
        AnnotCanvas.Children.Clear();
        AnnotCanvas.Children.Add(SnapshotImage);
        foreach (var shape in _annotation.Shapes)
        {
            AnnotCanvas.Children.Add(shape switch
            {
                ScreenshotRectangleShape rectangle => RectElement(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height, rectangle.Color, rectangle.StrokeWidth),
                ScreenshotArrowShape arrow => ArrowElement(arrow),
                ScreenshotMosaicShape mosaic => MosaicElement(mosaic),
                ScreenshotSequenceShape sequence => SequenceElement(sequence),
                ScreenshotTextShape text => TextElement(text),
                _ => new Canvas { IsHitTestVisible = false }
            });
        }
        if (preview is not null) { _preview = preview; AnnotCanvas.Children.Add(preview); }
    }

    private Microsoft.UI.Xaml.Shapes.Rectangle RectElement(double x, double y, double width, double height, string color, double strokeWidth)
    {
        var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = Math.Max(1, width * _scale), Height = Math.Max(1, height * _scale),
            Stroke = BrushOf(color), StrokeThickness = Math.Max(1, strokeWidth * _scale), IsHitTestVisible = false
        };
        Canvas.SetLeft(rect, x * _scale);
        Canvas.SetTop(rect, y * _scale);
        return rect;
    }

    private UIElement MosaicElement(ScreenshotMosaicShape mosaic)
    {
        var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = Math.Max(1, mosaic.Width * _scale), Height = Math.Max(1, mosaic.Height * _scale),
            Fill = new SolidColorBrush(ColorHelper.FromArgb(160, 0, 0, 0)),
            Stroke = new SolidColorBrush(Colors.White), StrokeThickness = 1,
            StrokeDashArray = [4, 2], IsHitTestVisible = false
        };
        Canvas.SetLeft(rect, mosaic.X * _scale);
        Canvas.SetTop(rect, mosaic.Y * _scale);
        return rect;
    }

    private UIElement ArrowElement(ScreenshotArrowShape arrow)
    {
        var container = new Canvas { IsHitTestVisible = false };
        var brush = BrushOf(arrow.Color);
        double thickness = Math.Max(1, arrow.StrokeWidth * _scale);
        container.Children.Add(Line(arrow.StartX, arrow.StartY, arrow.EndX, arrow.EndY, brush, thickness));
        var (left, right) = ScreenshotAnnotationService.ArrowHead(arrow.StartX, arrow.StartY, arrow.EndX, arrow.EndY, arrow.StrokeWidth);
        container.Children.Add(Line(arrow.EndX, arrow.EndY, left.X, left.Y, brush, thickness));
        container.Children.Add(Line(arrow.EndX, arrow.EndY, right.X, right.Y, brush, thickness));
        return container;
    }

    private Microsoft.UI.Xaml.Shapes.Line Line(double x1, double y1, double x2, double y2, Brush brush, double thickness) =>
        new()
        {
            X1 = x1 * _scale, Y1 = y1 * _scale, X2 = x2 * _scale, Y2 = y2 * _scale,
            Stroke = brush, StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false
        };

    private UIElement SequenceElement(ScreenshotSequenceShape sequence)
    {
        double diameter = sequence.Radius * 2 * _scale;
        var container = new Canvas { IsHitTestVisible = false };
        var ellipse = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = diameter, Height = diameter, Fill = BrushOf(sequence.Color) };
        Canvas.SetLeft(ellipse, (sequence.CenterX - sequence.Radius) * _scale);
        Canvas.SetTop(ellipse, (sequence.CenterY - sequence.Radius) * _scale);
        var label = new Border
        {
            Width = diameter, Height = diameter, IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = sequence.Number.ToString(), Foreground = new SolidColorBrush(Colors.White),
                FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = Math.Max(8, sequence.Radius * 1.05 * _scale),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        Canvas.SetLeft(label, (sequence.CenterX - sequence.Radius) * _scale);
        Canvas.SetTop(label, (sequence.CenterY - sequence.Radius) * _scale);
        container.Children.Add(ellipse);
        container.Children.Add(label);
        return container;
    }

    private UIElement TextElement(ScreenshotTextShape text)
    {
        var block = new TextBlock
        {
            Text = text.Text, Foreground = BrushOf(text.Color),
            FontSize = Math.Max(8, text.FontSize * _scale), IsHitTestVisible = false
        };
        Canvas.SetLeft(block, text.X * _scale);
        Canvas.SetTop(block, text.Y * _scale);
        return block;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Working.IsActive = busy;
        UpdateState();
        BusyChanged?.Invoke(this, busy);
    }

    private void UpdateState()
    {
        bool hasImage = _imagePath is not null;
        bool idle = !_busy;
        CaptureButton.IsEnabled = idle;
        foreach (var button in new ToggleButton[] { RectTool, ArrowTool, MosaicTool, SequenceTool, TextTool }.Concat(_colorButtons))
            button.IsEnabled = idle && hasImage;
        StrokeBox.IsEnabled = idle && hasImage;
        UndoButton.IsEnabled = idle && _annotation.CanUndo;
        RedoButton.IsEnabled = idle && _annotation.CanRedo;
        ClearButton.IsEnabled = idle && _annotation.Shapes.Count > 0;
        CopyButton.IsEnabled = SaveButton.IsEnabled = idle && hasImage;
    }

    private static SolidColorBrush BrushOf(string hex)
    {
        var color = ColorHelper.FromArgb(255,
            Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16));
        return new SolidColorBrush(color);
    }

    private void ShowError(string message)
    {
        Notice.Title = "截图标注";
        Notice.Message = message;
        Notice.Severity = InfoBarSeverity.Warning;
        Notice.IsOpen = true;
    }
}
