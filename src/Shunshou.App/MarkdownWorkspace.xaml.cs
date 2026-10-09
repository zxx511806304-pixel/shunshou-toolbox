using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Shunshou.Core;
using Windows.Storage.Pickers;
using Windows.System;

namespace Shunshou.App;

/// <summary>
/// Lightweight Markdown editor: plain-text editing on the left, a sandboxed WebView2 live preview
/// on the right (300 ms debounce), local open/save and PDF export. Everything renders offline;
/// the preview blocks navigation, scripts dialogs and downloads.
/// </summary>
public sealed partial class MarkdownWorkspace : UserControl, IDisposable
{
    private readonly string _sessionDirectory = Path.Combine(Path.GetTempPath(), "Shunshou", "markdown", Guid.NewGuid().ToString("N"));
    private readonly DispatcherTimer _debounce;
    private WebView2? _browser;
    private CoreWebView2Environment? _environment;
    private string? _path;
    private string _savedText = "";
    private bool _busy, _disposed, _previewReady, _settingText, _dialogOpen;

    public MarkdownWorkspace()
    {
        InitializeComponent();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); RenderPreview(); };
        var save = new KeyboardAccelerator { Key = VirtualKey.S, Modifiers = VirtualKeyModifiers.Control };
        save.Invoked += async (_, args) =>
        {
            if (_busy || !SaveButton.IsEnabled) return;
            args.Handled = true;
            await SaveAsync(saveAs: false);
        };
        KeyboardAccelerators.Add(save);
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (_disposed) return;
            if (Visibility == Visibility.Visible)
            {
                if (_browser?.CoreWebView2 is { } core) core.Resume();
                else _ = EnsurePreviewAsync();
            }
            else if (_browser?.CoreWebView2 is { } core)
            {
                DispatcherQueue.TryEnqueue(async () =>
                {
                    if (_disposed || Visibility == Visibility.Visible || !ReferenceEquals(_browser?.CoreWebView2, core)) return;
                    try { await core.TrySuspendAsync(); } catch (Exception) { }
                });
            }
        });
        UpdateState();
    }

    public nint HostWindowHandle { get; set; }
    public event EventHandler<bool>? BusyChanged;

    public bool HasUnsavedEdits => Editor.Text != _savedText;

    public async Task<bool> ConfirmDiscardEditsAsync()
    {
        if (!HasUnsavedEdits) return true;
        if (_dialogOpen) return false;
        _dialogOpen = true;
        try
        {
            if (XamlRoot is null) return false;
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "放弃未保存的修改？",
                Content = "当前文档有未保存的修改，继续会丢失这些内容。",
                PrimaryButtonText = "放弃修改",
                CloseButtonText = "继续编辑",
                DefaultButton = ContentDialogButton.Close
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally { _dialogOpen = false; }
    }

    private async Task EnsurePreviewAsync()
    {
        if (_disposed || _browser is not null) return;
        var browser = new WebView2 { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        _browser = browser;
        PreviewHost.Children.Add(browser);
        try
        {
            Directory.CreateDirectory(_sessionDirectory);
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null,
                Path.Combine(_sessionDirectory, "profile"), new CoreWebView2EnvironmentOptions());
            _environment = environment;
            await browser.EnsureCoreWebView2Async(environment);
            if (_disposed || !ReferenceEquals(browser, _browser)) return;
            var core = browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsBuiltInErrorPageEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.IsMuted = true;
            // The preview only ever shows NavigateToString content; links and embeds stay inert offline.
            core.NavigationStarting += (_, args) => { if (!string.Equals(args.Uri, "about:blank", StringComparison.OrdinalIgnoreCase)) args.Cancel = true; };
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.PermissionRequested += (_, args) => { args.State = CoreWebView2PermissionState.Deny; args.Handled = true; };
            core.DownloadStarting += (_, args) => { args.Cancel = true; args.Handled = true; };
            core.LaunchingExternalUriScheme += (_, args) => args.Cancel = true;
            _previewReady = true;
            PreviewHint.Visibility = Visibility.Collapsed;
            RenderPreview();
        }
        catch (Exception ex)
        {
            ResetBrowser();
            PreviewHint.Text = ex.HResult == unchecked((int)0x80070002)
                ? "这台电脑尚未安装 WebView2 运行库，无法显示预览；编辑与保存不受影响。"
                : "无法启动预览，请重试。" + ex.Message;
        }
    }

    private void RenderPreview()
    {
        if (_disposed || !_previewReady || _browser?.CoreWebView2 is not { } core) return;
        core.NavigateToString(MarkdownService.RenderHtml(Editor.Text));
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs args)
    {
        if (_settingText) return;
        UpdateState();
        _debounce.Stop();
        _debounce.Start();
    }

    // TextBox has no AcceptsTab in WinUI 3 (and the markup compiler rejects it), so Tab is
    // turned into a literal tab character here.
    private void Editor_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Tab || _busy) return;
        args.Handled = true;
        int start = Editor.SelectionStart;
        Editor.Text = Editor.Text.Insert(start, "\t");
        Editor.SelectionStart = start + 1;
    }

    private void SetEditorText(string text)
    {
        _settingText = true;
        try { Editor.Text = text; }
        finally { _settingText = false; }
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !await ConfirmDiscardEditsAsync()) return;
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".md");
            picker.FileTypeFilter.Add(".markdown");
            picker.FileTypeFilter.Add(".txt");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, HostWindowHandle);
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            string text = await File.ReadAllTextAsync(file.Path);
            _path = file.Path;
            SetEditorText(text);
            _savedText = Editor.Text;
            DocumentTitle.Text = Path.GetFileName(file.Path);
            Notice.IsOpen = false;
            RenderPreview();
            UpdateState();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync(saveAs: false);
    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await SaveAsync(saveAs: true);

    private async Task SaveAsync(bool saveAs)
    {
        if (_busy) return;
        string? target = _path;
        try
        {
            if (saveAs || target is null)
            {
                var picker = new FileSavePicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    SuggestedFileName = _path is null ? "未命名" : Path.GetFileNameWithoutExtension(_path)
                };
                picker.FileTypeChoices.Add("Markdown 文档", [".md"]);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, HostWindowHandle);
                var file = await picker.PickSaveFileAsync();
                if (file is null) return;
                target = file.Path;
            }
            SetBusy(true);
            string text = Editor.Text.ReplaceLineEndings("\r\n");
            string path = Path.GetFullPath(target);
            string temporary = Path.Combine(Path.GetDirectoryName(path)!, ".markdown-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(false));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            _path = path;
            _savedText = Editor.Text;
            DocumentTitle.Text = Path.GetFileName(path);
            ShowNotice("已保存：" + path, InfoBarSeverity.Success);
            UpdateState();
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { SetBusy(false); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = _path is null ? "未命名" : Path.GetFileNameWithoutExtension(_path)
            };
            picker.FileTypeChoices.Add("PDF 文档", [".pdf"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, HostWindowHandle);
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            SetBusy(true);
            string markdown = Editor.Text;
            string output = await MarkdownService.ExportPdfAsync(markdown, file.Path);
            ShowNotice("已导出 PDF：" + output, InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        UpdateState();
        BusyChanged?.Invoke(this, value);
    }

    private void UpdateState()
    {
        bool hasText = Editor.Text.Length > 0;
        SaveButton.IsEnabled = SaveAsButton.IsEnabled = ExportButton.IsEnabled = !_busy && hasText;
        OpenButton.IsEnabled = !_busy;
        Editor.IsReadOnly = _busy;
        DocumentDetail.Text = (_path ?? "尚未保存到文件") + (HasUnsavedEdits ? " · 已修改" : "") + $" · {Editor.Text.Length:N0} 字";
    }

    private void ResetBrowser()
    {
        _previewReady = false;
        if (_browser is { } browser)
        {
            _browser = null;
            try { browser.Close(); } catch (Exception) { }
            PreviewHost.Children.Remove(browser);
        }
        _environment = null;
    }

    private void ShowError(string message) => ShowNotice(message, InfoBarSeverity.Error);

    private void ShowNotice(string message, InfoBarSeverity severity)
    {
        Notice.Title = "Markdown 编辑";
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounce.Stop();
        ResetBrowser();
        var directory = _sessionDirectory;
        _ = Task.Run(async () =>
        {
            // WebView2 releases its profile asynchronously after Close(). Only remove this
            // instance's generated GUID directory, never a shared browser profile.
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Shunshou", "markdown")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(directory).StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) return;
            for (int attempt = 0; attempt < 12; attempt++)
            {
                try { if (Directory.Exists(directory)) Directory.Delete(directory, true); return; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                await Task.Delay(250);
            }
        });
    }
}
