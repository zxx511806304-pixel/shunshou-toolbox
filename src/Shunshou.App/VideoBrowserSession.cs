using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Shunshou.Core;

namespace Shunshou.App;

/// <summary>Only the isolated page explicitly opened by the user supplies a session.</summary>
internal sealed class VideoBrowserSession : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Shunshou", "video-browser", Guid.NewGuid().ToString("N"));
    public VideoDownloadSession? Session { get; private set; }

    internal static async Task<VideoBrowserSession?> OpenAsync(XamlRoot root, string url)
    {
        var result = new VideoBrowserSession();
        var browser = new WebView2 { Height = Math.Max(230, Math.Min(480, root.Size.Height - 250)), MinWidth = 550 };
        var hint = new TextBlock { Text = "在下方正常打开视频，必要时登录，然后点“使用当前网页”。会话仅用于这次下载。", TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 10, Children = { hint, browser } };
        var dialog = new ContentDialog { XamlRoot = root, Title = "网页辅助", Content = panel, PrimaryButtonText = "使用当前网页", CloseButtonText = "取消", IsPrimaryButtonEnabled = false };
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Opened += async (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(result._directory);
                var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(result._directory, "profile"), new());
                if (!string.Equals(Path.GetFullPath(environment.UserDataFolder), Path.Combine(result._directory, "profile"), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("无法创建独立网页会话。");
                var options = environment.CreateCoreWebView2ControllerOptions(); options.IsInPrivateModeEnabled = true;
                await browser.EnsureCoreWebView2Async(environment, options);
                var core = browser.CoreWebView2;
                core.Settings.IsPasswordAutosaveEnabled = false; core.Settings.IsGeneralAutofillEnabled = false;
                core.Settings.AreHostObjectsAllowed = false; core.Settings.IsWebMessageEnabled = false;
                core.Settings.AreDefaultScriptDialogsEnabled = false;
                core.PermissionRequested += (_, args) => { args.State = CoreWebView2PermissionState.Deny; args.Handled = true; };
                core.DownloadStarting += (_, args) => { args.Cancel = true; args.Handled = true; };
                core.NewWindowRequested += (_, args) => args.Handled = true;
                core.LaunchingExternalUriScheme += (_, args) => args.Cancel = true;
                core.NavigationStarting += (_, args) =>
                {
                    dialog.IsPrimaryButtonEnabled = false;
                    if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) args.Cancel = true;
                };
                core.NavigationCompleted += (_, args) =>
                {
                    dialog.IsPrimaryButtonEnabled = args.IsSuccess && args.HttpStatusCode < 400;
                    if (!dialog.IsPrimaryButtonEnabled) hint.Text = "网页加载失败，网站可能限制当前网络。可以取消后换完整分享链接重试。";
                };
                core.Navigate(url); ready.TrySetResult();
            }
            catch (Exception ex) { hint.Text = "无法启动网页辅助：" + ex.Message; ready.TrySetException(ex); }
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                await ready.Task;
                var core = browser.CoreWebView2;
                string source = VideoDownloadService.ValidateUrl(core.Source);
                string userAgent = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("navigator.userAgent")) ?? "Mozilla/5.0";
                string? mediaUrl = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(MediaScript));
                if (!Uri.TryCreate(mediaUrl, UriKind.Absolute, out var media) || media.Scheme is not ("http" or "https")) mediaUrl = null;
                var cookies = await core.CookieManager.GetCookiesAsync(source);
                var lines = new List<string> { "# Netscape HTTP Cookie File" };
                foreach (var cookie in cookies)
                {
                    if (new[] { cookie.Domain, cookie.Path, cookie.Name, cookie.Value }.Any(s => s.Any(c => c is '\t' or '\r' or '\n'))) continue;
                    long expires = cookie.IsSession ? 0 : (long)cookie.Expires;
                    lines.Add(string.Join('\t', cookie.Domain, cookie.Domain.StartsWith('.') ? "TRUE" : "FALSE", cookie.Path,
                        cookie.IsSecure ? "TRUE" : "FALSE", expires.ToString(CultureInfo.InvariantCulture), cookie.Name, cookie.Value));
                }
                string cookieFile = Path.Combine(result._directory, "session.txt");
                await File.WriteAllLinesAsync(cookieFile, lines, new UTF8Encoding(false));
                result.Session = new(cookieFile, userAgent, source, mediaUrl);
            }
            catch (Exception ex) { args.Cancel = true; hint.Text = "无法使用当前网页：" + ex.Message; }
            finally { deferral.Complete(); }
        };
        try
        {
            bool accepted = await dialog.ShowAsync() == ContentDialogResult.Primary && result.Session is not null;
            if (!accepted) { result.Dispose(); return null; }
            return result;
        }
        catch { result.Dispose(); throw; }
        finally { browser.Close(); }
    }

    private const string MediaScript = """
        (() => {
          const video = [...document.querySelectorAll('video')].find(v => v.offsetWidth > 0 && v.offsetHeight > 0);
          if (video && /^https?:/.test(video.currentSrc || video.src)) return video.currentSrc || video.src;
          if (location.hostname.endsWith('.xiaohongshu.com')) {
            const id = location.pathname.split('/').filter(Boolean).pop();
            const note = window.__INITIAL_STATE__?.note?.noteDetailMap?.[id]?.note;
            const streams = Object.values(note?.video?.media?.stream || {}).flat().filter(s => /^https?:/.test(s.masterUrl || ''));
            streams.sort((a,b) => (b.height || 0) - (a.height || 0));
            if (streams.length) return streams[0].masterUrl;
          }
          return null;
        })()
        """;

    public void Dispose()
    {
        // Delete the cookie export immediately; browser profile release may lag behind Close().
        try { File.Delete(Path.Combine(_directory, "session.txt")); } catch (IOException) { }
        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 12; attempt++)
            {
                try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); return; }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
                await Task.Delay(250);
            }
        });
    }
}
