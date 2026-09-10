using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Shunshou.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Shunshou.App;

/// <summary>A single-image editor; the host owns selection, the run button and cancellation.</summary>
public sealed partial class OcrWorkspace : UserControl, IDisposable, IAsyncDisposable
{
    private readonly SearchPreviewService _previewService;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operationGate = new(1);
    private readonly HashSet<string> _ownedClipboardFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _clipboardDirectory = Path.Combine(Path.GetTempPath(), "ShunshouToolbox", "clipboard", Guid.NewGuid().ToString("N"));
    private readonly Func<string, CancellationToken, Task<string>> _recognize;
    private string? _inputPath;
    private string? _lastExportedText;
    private string _recognizedText = "";
    private string _completionText = "";
    private bool _settingText;
    private bool _disposed;
    private bool _dialogOpen;
    private bool _narrowLayout;
    private ScrollViewer? _hostScroll;
    private int _inputRevision;

    public OcrWorkspace() : this((path, token) => new OcrService().RecognizeAsync(path, token)) { }

    // Injection is limited to the in-process verification harness; shipping construction uses real OCR.
    internal OcrWorkspace(Func<string, CancellationToken, Task<string>> recognize)
    {
        InitializeComponent();
        _recognize = recognize;
        _previewService = new SearchPreviewService(DispatcherQueue);
    }

    public IntPtr HostWindowHandle { get; set; }
    public string? InputPath => _inputPath;
    // WinUI TextBox can normalize line breaks to CR internally. Public/exported
    // text consistently uses Windows CRLF; edit baselines use the actual editor value.
    public string ResultText => ResultEditor.Text.ReplaceLineEndings("\r\n");
    public bool HasUnsavedEdits => ResultEditor.Text != _recognizedText && ResultEditor.Text != _lastExportedText;
    public bool IsBusy { get; private set; }
    public event EventHandler<string?>? InputChanged;
    public event EventHandler<bool>? BusyChanged;
    internal Func<Task<bool>>? ConfirmDiscardOverride { get; set; }
    internal BitmapImage? PreviewImage => SourcePreview.Source as BitmapImage;

    /// <summary>Returns false when the user keeps their edited text or another operation is active.</summary>
    public async Task<bool> SetInputAsync(string? path, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _operationGate.WaitAsync(0, cancellationToken)) return false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        SetBusy(true);
        try { return await SetInputCoreAsync(path, linked.Token); }
        finally { FinishOperation(); }
    }

    private async Task<bool> SetInputCoreAsync(string? path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        path = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        if (string.Equals(path, _inputPath, StringComparison.OrdinalIgnoreCase)) return true;
        (int Width, int Height)? dimensions = path is null ? null : await Task.Run(() => ReadDimensions(path), ct);
        if (!await ConfirmDiscardEditsAsync()) return false;
        ct.ThrowIfCancellationRequested();
        BitmapImage? preview = path is null ? null : await _previewService.LoadAsync(path, 640, ct);
        ct.ThrowIfCancellationRequested();
        _inputPath = path;
        _inputRevision++;
        SourcePreview.Source = preview;
        PreviewPlaceholder.Visibility = preview is null ? Visibility.Visible : Visibility.Collapsed;
        PreviewPlaceholderText.Text = path is null ? "添加图片，或粘贴截图" : "图片已添加，可开始识别";
        string firstPage = path is not null && Path.GetExtension(path).ToLowerInvariant() is ".tif" or ".tiff" or ".gif" ? " · 识别第 1 页/帧" : "";
        ImageCaption.Text = path is null ? "" : $"{Path.GetFileName(path)} · {dimensions!.Value.Width} × {dimensions.Value.Height}{firstPage}";
        ToolTipService.SetToolTip(ImageCaption, path ?? "");
        SetResultText("");
        _recognizedText = "";
        _lastExportedText = null;
        _completionText = "";
        ClearNotice();
        // A pasted image can remain in the host's shared image batch after selection changes.
        // Keep accepted temporary files for this session; disposal removes only our own files.
        InputChanged?.Invoke(this, path);
        return true;
    }

    public async Task<string> RunRecognitionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(_inputPath)) throw new InvalidOperationException("请先添加需要识别的图片。");
        if (!await _operationGate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("图片正在处理中，请稍候。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        SetBusy(true);
        try
        {
            if (!await ConfirmDiscardEditsAsync()) throw new OperationCanceledException("已保留编辑的文字。", cancellationToken);
            linked.Token.ThrowIfCancellationRequested();
            int revision = _inputRevision;
            string path = _inputPath;
            _completionText = "";
            ClearNotice();
            var text = await _recognize(path, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (_disposed || revision != _inputRevision) throw new OperationCanceledException(linked.Token);
            SetResultText(text);
            _recognizedText = ResultEditor.Text;
            _lastExportedText = null;
            ShowCompletion("已识别");
            return ResultText;
        }
        finally { FinishOperation(); }
    }

    /// <summary>Explicit paste button or a host Ctrl+V handler calls this only for the visible OCR tool.</summary>
    public async Task<bool> PasteImageAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await PasteDataAsync(Clipboard.GetContent(), cancellationToken);
    }

    internal async Task<bool> PasteDataAsync(DataPackageView content, CancellationToken cancellationToken = default)
    {
        if (!await _operationGate.WaitAsync(0, cancellationToken)) return false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        SetBusy(true);
        string? createdPath = null;
        try
        {
            if (content.Contains(StandardDataFormats.StorageItems))
            {
                var items = await content.GetStorageItemsAsync();
                var files = items.OfType<StorageFile>().ToArray();
                if (files.Length != 1 || items.Count != 1) throw new InvalidOperationException("请一次复制一张需要识别的图片。");
                return await SetInputCoreAsync(files[0].Path, linked.Token);
            }
            if (!content.Contains(StandardDataFormats.Bitmap)) throw new InvalidOperationException("剪贴板里没有图片，请先截图或复制一张图片。");
            var reference = await content.GetBitmapAsync();
            using var input = await reference.OpenReadAsync();
            linked.Token.ThrowIfCancellationRequested();
            if (input.Size > 200_000_000) throw new InvalidOperationException("剪贴板图片过大，请裁剪需要识别的区域。");
            var decoder = await BitmapDecoder.CreateAsync(input);
            ValidateDimensions(decoder.PixelWidth, decoder.PixelHeight);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            linked.Token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_clipboardDirectory);
            createdPath = Path.Combine(_clipboardDirectory, "Screenshot-" + Guid.NewGuid().ToString("N") + ".png");
            _ownedClipboardFiles.Add(createdPath);
            using (var fileStream = new FileStream(createdPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var output = fileStream.AsRandomAccessStream())
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync();
            }
            linked.Token.ThrowIfCancellationRequested();
            bool accepted = await SetInputCoreAsync(createdPath, linked.Token);
            if (accepted) createdPath = null;
            return accepted;
        }
        finally
        {
            DeleteOwnedClipboardFile(createdPath);
            FinishOperation();
        }
    }

    public bool CanHandlePasteShortcut()
    {
        if (_disposed || IsBusy || Visibility != Visibility.Visible || XamlRoot is null || _dialogOpen) return false;
        for (DependencyObject? focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
             focused is not null; focused = VisualTreeHelper.GetParent(focused))
            if (focused is TextBox { IsReadOnly: false } or RichEditBox { IsReadOnly: false } or PasswordBox) return false;
        return true;
    }

    public void CopyText()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(ResultEditor.Text)) return;
        var content = CreateTextPackage();
        Clipboard.SetContent(content);
        Clipboard.Flush();
        _lastExportedText = ResultEditor.Text;
        UpdateCommands();
        ShowCompletion("已复制");
    }

    internal DataPackage CreateTextPackage()
    {
        var content = new DataPackage();
        content.SetText(ResultText);
        return content;
    }

    public async Task<string?> SaveTextAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(ResultEditor.Text)) return null;
        if (HostWindowHandle == IntPtr.Zero) throw new InvalidOperationException("保存窗口尚未准备好，请稍后重试。");
        if (!await _operationGate.WaitAsync(0)) return null;
        SetBusy(true);
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = string.IsNullOrWhiteSpace(_inputPath) ? "RecognizedText" : Path.GetFileNameWithoutExtension(_inputPath) + "_text"
            };
            picker.FileTypeChoices.Add("文本文件", [".txt"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, HostWindowHandle);
            var file = await picker.PickSaveFileAsync();
            if (file is null) return null;
            await SaveTextToPathAsync(file.Path);
            ShowCompletion("已保存");
            return file.Path;
        }
        finally { FinishOperation(); }
    }

    internal async Task SaveTextToPathAsync(string path)
    {
        var editorText = ResultEditor.Text;
        var text = editorText.ReplaceLineEndings("\r\n");
        path = Path.GetFullPath(path);
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, ".ocr-text-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(text.AsMemory(), _lifetime.Token);
                await writer.FlushAsync(_lifetime.Token);
            }
            _lifetime.Token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            _lastExportedText = editorText;
            UpdateCommands();
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public async Task<bool> ConfirmDiscardEditsAsync()
    {
        if (!HasUnsavedEdits) return true;
        if (_dialogOpen) return false;
        _dialogOpen = true;
        try
        {
            if (ConfirmDiscardOverride is not null) return await ConfirmDiscardOverride();
            if (XamlRoot is null) return false;
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "保留编辑的文字？",
                Content = "当前文字有未复制或保存的修改。继续会清除这些修改。",
                PrimaryButtonText = "放弃修改并继续",
                CloseButtonText = "返回编辑",
                DefaultButton = ContentDialogButton.Close
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally { _dialogOpen = false; }
    }

    private static (int Width, int Height) ReadDimensions(string path)
    {
        return OcrService.ReadImageDimensions(path);
    }

    private static void ValidateDimensions(uint width, uint height)
    {
        if (width == 0 || height == 0 || (ulong)width * height > 100_000_000)
            throw new InvalidOperationException("图片超过一亿像素或尺寸无效，请裁剪需要识别的区域。");
    }

    private void SetResultText(string text)
    {
        _settingText = true;
        try { ResultEditor.Text = text; }
        finally { _settingText = false; }
        UpdateCommands();
    }

    internal void EditTextForVerification(string text) => ResultEditor.Text = text;

    private void ResultEditor_TextChanged(object sender, TextChangedEventArgs args)
    {
        if (!_settingText && CharacterCount is not null)
        {
            _completionText = "";
            UpdateCommands();
        }
    }

    private void SetBusy(bool value)
    {
        IsBusy = value;
        if (_disposed) return;
        WorkingRing.IsActive = value;
        WorkingRing.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        ResultEditor.IsReadOnly = value;
        PasteButton.IsEnabled = !value;
        UpdateCommands();
        BusyChanged?.Invoke(this, value);
    }

    private void FinishOperation()
    {
        SetBusy(false);
        if (_disposed) CleanupOwnedClipboardFiles();
        _operationGate.Release();
    }

    private void UpdateCommands()
    {
        if (_disposed) return;
        bool hasText = !string.IsNullOrWhiteSpace(ResultEditor.Text);
        CopyButton.IsEnabled = SaveButton.IsEnabled = hasText && !IsBusy;
        string state = HasUnsavedEdits ? "已修改" : _completionText;
        CharacterCount.Text = $"{ResultEditor.Text.Length:N0} 字" + (state.Length == 0 ? "" : " · " + state);
    }

    private void EditorLayout_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool narrow = args.NewSize.Width < 620;
        _narrowLayout = narrow;
        PreviewColumn.Width = new GridLength(narrow ? 1 : 0.9, GridUnitType.Star);
        TextColumn.Width = new GridLength(narrow ? 0 : 1.1, GridUnitType.Star);
        Grid.SetColumn(TextPane, narrow ? 0 : 1);
        Grid.SetRow(TextPane, narrow ? 1 : 0);
        UpdateBodyHeight();
    }

    private void Workspace_Loaded(object sender, RoutedEventArgs args)
    {
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ScrollViewer scroll) continue;
            _hostScroll = scroll;
            _hostScroll.SizeChanged += HostScroll_SizeChanged;
            break;
        }
        UpdateBodyHeight();
    }

    private void Workspace_Unloaded(object sender, RoutedEventArgs args)
    {
        if (_hostScroll is not null) _hostScroll.SizeChanged -= HostScroll_SizeChanged;
        _hostScroll = null;
    }

    private void HostScroll_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateBodyHeight();
    private void WorkspaceStack_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateBodyHeight();

    private void UpdateBodyHeight()
    {
        if (PreviewBodyRow is null || TextBodyRow is null || _disposed) return;
        double body = 300;
        if (!_narrowLayout && _hostScroll is { ViewportHeight: > 0 } && PreviewBodyRow.ActualHeight > 0)
        {
            // The measured remainder includes the paste toolbar, headings, copy/save
            // row and the optional InfoBar. Both panes shrink together, never the text.
            double remainder = WorkspaceStack.ActualHeight - PreviewBodyRow.ActualHeight;
            body = Math.Clamp(_hostScroll.ViewportHeight - remainder - 4, WorkspaceNotice.IsOpen ? 128 : 160, 300);
        }
        if (Math.Abs(PreviewBodyRow.Height.Value - body) < 1) return;
        PreviewBodyRow.Height = TextBodyRow.Height = new GridLength(body);
    }

    private void ShowNotice(string message, InfoBarSeverity severity)
    {
        if (_disposed) return;
        _completionText = "";
        UpdateCommands();
        WorkspaceNotice.Message = message;
        WorkspaceNotice.Severity = severity;
        WorkspaceNotice.Visibility = Visibility.Visible;
        WorkspaceNotice.IsOpen = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;
            UpdateLayout();
            UpdateBodyHeight();
            _hostScroll?.ChangeView(null, 0, null, true);
        });
    }

    private void ShowCompletion(string text)
    {
        _completionText = text;
        ClearNotice();
        UpdateCommands();
    }

    private void ClearNotice()
    {
        WorkspaceNotice.IsOpen = false;
        WorkspaceNotice.Visibility = Visibility.Collapsed;
    }

    private void WorkspaceNotice_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        WorkspaceNotice.Visibility = Visibility.Collapsed;
        UpdateBodyHeight();
    }

    private async void Paste_Click(object sender, RoutedEventArgs args)
    {
        try { await PasteImageAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowNotice(ex.Message, InfoBarSeverity.Warning); }
    }

    private void Copy_Click(object sender, RoutedEventArgs args)
    {
        try { CopyText(); }
        catch (Exception) { ShowNotice("暂时无法访问剪贴板，请稍后重试。", InfoBarSeverity.Warning); }
    }

    private async void Save_Click(object sender, RoutedEventArgs args)
    {
        try { await SaveTextAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowNotice(ex.Message, InfoBarSeverity.Warning); }
    }

    private void DeleteOwnedClipboardFile(string? path)
    {
        if (path is null || !_ownedClipboardFiles.Contains(path)) return;
        try
        {
            File.Delete(path);
            _ownedClipboardFiles.Remove(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _inputRevision++;
        _lifetime.Cancel();
        _previewService.Dispose();
        if (_hostScroll is not null) _hostScroll.SizeChanged -= HostScroll_SizeChanged;
        _hostScroll = null;
        SourcePreview.Source = null;
        CleanupOwnedClipboardFiles();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        // A decoder may still own its input handle while observing cancellation.
        // A graceful host close can await this before ending the process.
        await _operationGate.WaitAsync();
        try { CleanupOwnedClipboardFiles(); }
        finally { _operationGate.Release(); }
    }

    private void CleanupOwnedClipboardFiles()
    {
        foreach (var path in _ownedClipboardFiles.ToArray()) DeleteOwnedClipboardFile(path);
        // Never recurse or enumerate other sessions; only the exact owned, now-empty directory is removed.
        try { if (Directory.Exists(_clipboardDirectory)) Directory.Delete(_clipboardDirectory, recursive: false); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
