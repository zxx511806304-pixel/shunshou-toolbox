using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Shunshou.Core;
using Windows.ApplicationModel.DataTransfer;

namespace Shunshou.App;

public sealed partial class WebPdfWorkspace
{
    private bool _textMode;
    private string? _textSource;
    private sealed record ExtractedPage(string title, string text, string url, string method, string warnings, int paragraphs);

    public void SelectTextMode(bool textMode)
    {
        _textMode = textMode;
        ConnectionHint.Text = textMode ? "需联网加载网页 · 提取单篇文章或单章正文，可编辑后保存" : "需联网加载网页 · PDF 保存在本机";
        OrientationBox.Visibility = textMode ? Visibility.Collapsed : Visibility.Visible;
        TextActions.Visibility = textMode ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.Content = textMode ? "保存 TXT" : "保存 PDF";
        OpenButton.Content = "打开文件";
        if (!textMode) { TextEditor.Visibility = Visibility.Collapsed; if (_browser is not null) _browser.Visibility = Visibility.Visible; }
        UpdateEnabled();
    }

    private async Task<ExtractedPage> ExtractTextAsync(CancellationToken ct)
    {
        if (!_pageReady || _browser?.CoreWebView2 is not { } core)
            throw new InvalidOperationException("网页还没有加载完成，请等到页面显示后再提取。");
        ulong navigation = _navigationId;
        string root = Path.Combine(AppContext.BaseDirectory, "Assets", "Readability");
        string library = await File.ReadAllTextAsync(Path.Combine(root, "Readability.js"), ct);
        string extraction = await File.ReadAllTextAsync(Path.Combine(root, "ExtractVisibleText.js"), ct);
        string script = "(() => { try {\n" + library + "\n" + extraction + "\n} catch(e) { return {error:String(e.message || e)}; } })()";
        string json = await core.ExecuteScriptAsync(script).AsTask().WaitAsync(ct);
        if (navigation != _navigationId || !_pageReady) throw new InvalidOperationException("网页已跳转，请等待加载完成后重试。");
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
        var page = JsonSerializer.Deserialize<ExtractedPage>(json) ?? throw new InvalidDataException("没有读取到正文。");
        if (string.IsNullOrWhiteSpace(page.text)) throw new InvalidDataException("正文为空。");
        return page;
    }

    private async void ExtractText_Click(object sender, RoutedEventArgs e) => await RunJobAsync(async ct =>
    {
        StatusText.Text = "正在提取正文…";
        var page = await ExtractTextAsync(ct);
        TextEditor.Text = page.title + Environment.NewLine + Environment.NewLine + page.text;
        _textSource = page.url;
        TextEditor.Visibility = Visibility.Visible;
        if (_browser is not null) _browser.Visibility = Visibility.Collapsed;
        StatusText.Text = $"已提取 {page.text.Length:N0} 字 · {page.paragraphs} 段";
        if (page.warnings.Length > 0) { MessageBar.Title = "请核对内容"; MessageBar.Message = page.warnings; MessageBar.Severity = InfoBarSeverity.Informational; MessageBar.IsOpen = true; }
    });

    private void ShowPage_Click(object sender, RoutedEventArgs e)
    {
        TextEditor.Visibility = Visibility.Collapsed;
        if (_browser is not null) _browser.Visibility = Visibility.Visible;
    }

    private async void OcrPage_Click(object sender, RoutedEventArgs e) => await RunJobAsync(async ct =>
    {
        if (!_pageReady || _browser?.CoreWebView2 is not { } core)
            throw new InvalidOperationException("网页还没有加载完成，请等到页面显示后再识别。");
        ShowPage_Click(this, new RoutedEventArgs());
        await Task.Delay(250, ct);
        string screenshot = Path.Combine(_sessionDirectory, "visible-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var stream = File.Create(screenshot)) await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream.AsRandomAccessStream()).AsTask().WaitAsync(ct);
            string text = await new OcrService().RecognizeAsync(screenshot, ct);
            TextEditor.Text = text; _textSource = core.Source;
            TextEditor.Visibility = Visibility.Visible; _browser.Visibility = Visibility.Collapsed;
            StatusText.Text = "已识别当前可见画面（不包含未显示的正文）";
        }
        finally { try { File.Delete(screenshot); } catch (IOException) { } }
    });

    private void CopyText_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TextEditor.Text)) return;
        var package = new DataPackage(); package.SetText(TextEditor.Text); Clipboard.SetContent(package);
        StatusText.Text = "已复制文字";
    }
    private void TextEditor_Changed(object sender, TextChangedEventArgs e) { if (SaveButton is not null) UpdateEnabled(); }
    private async Task<string> SaveTextAsync(string directory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(TextEditor.Text)) throw new InvalidOperationException("请先提取正文。");
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        string title = CleanFileName(TextEditor.Text.Split('\n')[0]);
        string destination = Path.Combine(directory, title + ".txt");
        for (int i = 2; File.Exists(destination); i++) destination = Path.Combine(directory, $"{title} ({i}).txt");
        string partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await File.WriteAllTextAsync(partial, TextEditor.Text + "\n\n来源：" + _textSource, new UTF8Encoding(true), ct);
            ct.ThrowIfCancellationRequested(); File.Move(partial, destination, false); return destination;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
}
