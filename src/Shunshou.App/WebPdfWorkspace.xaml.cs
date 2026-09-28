using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Shunshou.Core;
using Windows.Storage.Pickers;
using Windows.System;

namespace Shunshou.App;

/// <summary>Loads a page in an isolated, in-private WebView and prints locally.</summary>
public sealed partial class WebPdfWorkspace : UserControl, IDisposable
{
    public nint HostWindowHandle { get; set; }
    public event EventHandler<bool>? BusyChanged;
    private readonly string _sessionDirectory = Path.Combine(Path.GetTempPath(), "Shunshou", "web-pdf", Guid.NewGuid().ToString("N"));
    private WebView2? _browser;
    private CoreWebView2Environment? _environment;
    private CancellationTokenSource? _job;
    private TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>? _navigation;
    private string? _output;
    private ulong _navigationId;
    private bool _pageReady, _disposed, _printing, _syncingUrl;

    public WebPdfWorkspace()
    {
        InitializeComponent();
        OutputBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ShunshouToolbox", "Output");
        if (!NoticePreferences.IsDismissed("webpdf")) NoticeBar.IsOpen = true;
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (_disposed || _browser?.CoreWebView2 is not { } core) return;
            if (Visibility == Visibility.Visible) core.Resume();
            else PausePreview();
        });
    }

    private void Notice_Closed(InfoBar sender, InfoBarClosedEventArgs args) => NoticePreferences.Dismiss("webpdf");

    internal static Uri ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("请输入完整的 http:// 或 https:// 网页链接。");
        return uri;
    }

    private async Task EnsureBrowserAsync(CancellationToken ct)
    {
        if (_browser?.CoreWebView2 is not null) { _browser.CoreWebView2.Resume(); return; }
        RuntimeLink.Visibility = Visibility.Collapsed;
        var browser = new WebView2 { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        _browser = browser;
        BrowserHost.Children.Insert(0, browser);
        try
        {
            Directory.CreateDirectory(_sessionDirectory);
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null,
                Path.Combine(_sessionDirectory, "profile"), new CoreWebView2EnvironmentOptions()).AsTask().WaitAsync(ct);
            TraceBrowser("environment", new { environment.BrowserVersionString, environment.UserDataFolder });
            ct.ThrowIfCancellationRequested();
            if (!string.Equals(Path.GetFullPath(environment.UserDataFolder).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(Path.Combine(_sessionDirectory, "profile")), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("系统的 WebView2 配置覆盖了独立预览目录，无法创建安全的网页预览。");
            _environment = environment;
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = true;
            await browser.EnsureCoreWebView2Async(environment, options).AsTask().WaitAsync(ct);
            TraceBrowser("initialized", new { browser.CoreWebView2.BrowserProcessId, Profile = browser.CoreWebView2.Profile.IsInPrivateModeEnabled });
            ct.ThrowIfCancellationRequested();
            if (_disposed || !ReferenceEquals(browser, _browser)) throw new OperationCanceledException(ct);
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
            core.NavigationStarting += Navigation_Starting;
            core.NavigationCompleted += Navigation_Completed;
            core.FrameNavigationStarting += (_, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var frame) ||
                    frame.Scheme is not ("http" or "https" or "about" or "data" or "blob")) args.Cancel = true;
            };
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.PermissionRequested += (_, args) => { args.State = CoreWebView2PermissionState.Deny; args.Handled = true; };
            core.DownloadStarting += (_, args) => { args.Cancel = true; args.Handled = true; };
            core.LaunchingExternalUriScheme += (_, args) => args.Cancel = true;
            core.ProcessFailed += (_, args) =>
            {
                if (_disposed) return;
                TraceBrowser("process-failed", new { Kind = args.ProcessFailedKind.ToString(), Reason = args.Reason.ToString(), args.ExitCode, args.ProcessDescription, args.FailureSourceModulePath });
                // WebView2 automatically recreates GPU and utility processes. Closing
                // the control for those recoverable events interrupts a healthy page.
                if (args.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited)) return;
                _pageReady = false;
                _navigation?.TrySetException(new InvalidOperationException("网页预览已中断，请重新加载网页。"));
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_disposed || !ReferenceEquals(_browser, browser)) return;
                    ResetBrowser();
                    UpdateEnabled();
                });
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ResetBrowser();
            // File-not-found is the documented missing Evergreen runtime result. Other
            // initialization errors retain their original context instead of blaming a missing runtime.
            if (ex.HResult == unchecked((int)0x80070002))
            {
                RuntimeLink.Visibility = Visibility.Visible;
                throw new InvalidOperationException("这台电脑尚未安装 WebView2 运行库。安装后重新加载即可。", ex);
            }
            throw new InvalidOperationException("无法启动网页预览，请重试。" + ex.Message, ex);
        }
    }

    private void Navigation_Starting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        TraceBrowser("navigation-starting", new { args.NavigationId, args.Uri, args.IsRedirected, _printing, _disposed });
        if (_disposed || _printing) { args.Cancel = true; return; }
        try { ValidateUrl(args.Uri); }
        catch (ArgumentException) { args.Cancel = true; _navigation?.TrySetException(new InvalidOperationException("该链接不是可预览的网页。")); return; }
        _navigationId = args.NavigationId;
        _pageReady = false;
        UpdateEnabled();
    }

    private void Navigation_Completed(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        TraceBrowser("navigation-completed", new { args.NavigationId, args.IsSuccess, args.HttpStatusCode, Error = args.WebErrorStatus.ToString(), sender.Source });
        if (_disposed || args.NavigationId != _navigationId) return;
        bool success = args.IsSuccess && args.HttpStatusCode < 400;
        _pageReady = false;
        if (success)
        {
            _syncingUrl = true;
            try { UrlBox.Text = sender.Source; }
            finally { _syncingUrl = false; }
            _pageReady = true;
            EmptyPreview.Visibility = Visibility.Collapsed;
            StatusText.Text = string.IsNullOrWhiteSpace(sender.DocumentTitle) ? "网页已加载" : sender.DocumentTitle;
        }
        else if (_job is null) ShowError("网页加载失败，请检查链接与网络后重试。");
        UpdateEnabled();
        _navigation?.TrySetResult(args);
    }

    private async Task LoadPageAsync(string url, CancellationToken ct)
    {
        var uri = ValidateUrl(url);
        _pageReady = false;
        StatusText.Text = "正在加载网页…";
        await EnsureBrowserAsync(ct);
        var navigation = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        _navigation = navigation;
        try
        {
            _browser!.CoreWebView2.Navigate(uri.AbsoluteUri);
            var result = await navigation.Task.WaitAsync(ct);
            if (!result.IsSuccess || result.HttpStatusCode >= 400)
                throw new InvalidOperationException(result.HttpStatusCode >= 400
                    ? $"网页返回 HTTP {result.HttpStatusCode}，请检查链接后重试。"
                    : $"网页加载失败（{result.WebErrorStatus}），请检查网络后重试。");
            ct.ThrowIfCancellationRequested();
        }
        finally { if (ReferenceEquals(_navigation, navigation)) _navigation = null; }
    }

    private async Task<string> SavePdfAsync(string directory, CancellationToken ct)
    {
        var core = _browser?.CoreWebView2;
        if (!_pageReady || core is null || _environment is null) throw new InvalidOperationException("请先加载并确认网页内容。");
        directory = Path.GetFullPath(directory.Trim());
        Directory.CreateDirectory(directory);
        var stage = Path.Combine(_sessionDirectory, Guid.NewGuid().ToString("N") + ".pdf");
        var settings = _environment.CreatePrintSettings();
        settings.Orientation = OrientationBox.SelectedIndex == 1 ? CoreWebView2PrintOrientation.Landscape : CoreWebView2PrintOrientation.Portrait;
        settings.PageWidth = 8.2677165354; // A4 dimensions in inches, as required by WebView2.
        settings.PageHeight = 11.6929133858;
        settings.MarginTop = settings.MarginBottom = settings.MarginLeft = settings.MarginRight = 0.3937007874;
        settings.ShouldPrintBackgrounds = true;
        settings.ShouldPrintHeaderAndFooter = false;
        settings.ScaleFactor = 1;
        string title = CleanFileName(core.DocumentTitle);
        _printing = true;
        StatusText.Text = "正在保存 PDF…";
        try
        {
            var result = await core.PrintToPdfAsync(stage, settings).AsTask().WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (!result || !File.Exists(stage) || new FileInfo(stage).Length < 8)
                throw new InvalidOperationException("网页未能生成 PDF，请重新加载后重试。");
            using (var stream = File.OpenRead(stage))
            {
                var header = new byte[5];
                if (stream.Read(header, 0, header.Length) != 5 || System.Text.Encoding.ASCII.GetString(header) != "%PDF-")
                    throw new InvalidOperationException("网页未能生成有效的 PDF。");
            }
            var destination = Path.Combine(directory, title + ".pdf");
            for (int suffix = 2; File.Exists(destination); suffix++) destination = Path.Combine(directory, $"{title} ({suffix}).pdf");
            File.Move(stage, destination, false);
            return destination;
        }
        finally
        {
            _printing = false;
            if (File.Exists(stage)) try { File.Delete(stage); } catch (IOException) { }
        }
    }

    private static string CleanFileName(string title)
    {
        var bad = Path.GetInvalidFileNameChars();
        var name = string.Concat(title.Select(c => bad.Contains(c) || char.IsControl(c) ? '_' : c)).Trim(' ', '.');
        if (name.Length > 90) name = name[..90].TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(name)) name = "WebPage";
        string first = name.Split('.')[0].ToUpperInvariant();
        if (first is "CON" or "PRN" or "AUX" or "NUL" || (first.Length == 4 && (first.StartsWith("COM") || first.StartsWith("LPT")) && char.IsAsciiDigit(first[3]))) name = "WebPage_" + name;
        return name;
    }

    private async Task RunJobAsync(Func<CancellationToken, Task> action, int timeoutSeconds = 60)
    {
        if (_disposed || _job is not null) return;
        using var job = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        _job = job;
        MessageBar.IsOpen = false;
        Progress.IsIndeterminate = true;
        BusyChanged?.Invoke(this, true);
        UpdateEnabled();
        try { await action(job.Token); }
        catch (OperationCanceledException)
        {
            if (!_disposed) { ResetBrowser(); StatusText.Text = "已停止，请重新加载网页"; }
        }
        catch (Exception ex) { if (!_disposed) { StatusText.Text = "处理未完成"; ShowError(ex.Message); } }
        finally
        {
            _job = null;
            if (!_disposed) { Progress.IsIndeterminate = false; UpdateEnabled(); BusyChanged?.Invoke(this, false); }
        }
    }

    private void UpdateEnabled()
    {
        bool idle = _job is null && !_disposed;
        UrlBox.IsEnabled = LoadButton.IsEnabled = OutputBox.IsEnabled = BrowseButton.IsEnabled = OrientationBox.IsEnabled = idle;
        SaveButton.IsEnabled = idle && _pageReady;
        if (_textMode) SaveButton.IsEnabled = idle && !string.IsNullOrWhiteSpace(TextEditor.Text);
        ExtractTextButton.IsEnabled = OcrPageButton.IsEnabled = idle && _pageReady;
        ShowPageButton.IsEnabled = idle;
        CopyTextButton.IsEnabled = idle && !string.IsNullOrWhiteSpace(TextEditor.Text);
        TextEditor.IsReadOnly = !idle;
        CancelButton.IsEnabled = !idle && !_disposed;
        OpenButton.IsEnabled = idle && _output is not null && File.Exists(_output);
        if (_browser is not null) _browser.IsHitTestVisible = idle;
    }

    private void Url_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncingUrl) return;
        // WinUI can deliver TextChanged after the handler that wrote the URL has returned, so a late
        // event for the page that is already open must not unlock the loaded preview mid-extraction.
        if (_pageReady && _browser?.CoreWebView2 is { } core &&
            string.Equals(UrlBox.Text.Trim(), core.Source, StringComparison.OrdinalIgnoreCase)) return;
        _pageReady = false;
        if (SaveButton is not null) UpdateEnabled();
    }
    private async void Url_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Enter && _job is null) { e.Handled = true; await RunJobAsync(ct => LoadPageAsync(UrlBox.Text, ct)); } }
    private async void Load_Click(object sender, RoutedEventArgs e) => await RunJobAsync(ct => LoadPageAsync(UrlBox.Text, ct));
    private async void Save_Click(object sender, RoutedEventArgs e) => await RunJobAsync(async ct =>
    {
        _output = _textMode ? await SaveTextAsync(OutputBox.Text, ct) : await SavePdfAsync(OutputBox.Text, ct);
        StatusText.Text = "已保存 · " + Path.GetFileName(_output);
    }, 120);
    private void Cancel_Click(object sender, RoutedEventArgs e) { _job?.Cancel(); _browser?.CoreWebView2?.Stop(); }
    private void ShowError(string message) { MessageBar.Title = _textMode ? "链接提取文字" : "网页转 PDF"; MessageBar.Message = message; MessageBar.Severity = InfoBarSeverity.Error; MessageBar.IsOpen = true; }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_output is null) return;
        try { Process.Start(new ProcessStartInfo(_output) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, HostWindowHandle);
            var folder = await picker.PickSingleFolderAsync();
            if (!_disposed && folder is not null) OutputBox.Text = folder.Path;
        }
        catch (Exception ex) { if (!_disposed) ShowError(ex.Message); }
    }

    public void PausePreview()
    {
        if (_disposed || _browser?.CoreWebView2 is not { } core) return;
        core.IsMuted = true;
        core.Stop();
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_disposed || Visibility == Visibility.Visible || _job is not null || !ReferenceEquals(_browser?.CoreWebView2, core)) return;
            try { await core.TrySuspendAsync(); } catch (Exception) { }
        });
    }

    private void ResetBrowser()
    {
        _pageReady = false;
        if (_browser is { } browser)
        {
            _browser = null;
            try { browser.Close(); } catch (Exception) { }
            BrowserHost.Children.Remove(browser);
        }
        _environment = null;
        if (!_disposed) EmptyPreview.Visibility = Visibility.Visible;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _job?.Cancel();
        _navigation?.TrySetCanceled();
        ResetBrowser();
        var directory = _sessionDirectory;
        _ = Task.Run(async () =>
        {
            // WebView2 releases its private profile asynchronously after Close(). Only
            // remove this instance's generated GUID directory, never a browser profile.
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Shunshou", "web-pdf")) + Path.DirectorySeparatorChar;
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
