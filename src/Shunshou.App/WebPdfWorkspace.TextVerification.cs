using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;

namespace Shunshou.App;
public sealed partial class WebPdfWorkspace
{
    internal async Task<object> VerifyTextFixtureAsync(string output)
    {
        SelectTextMode(true);
        Directory.CreateDirectory(output);
        _browserDiagnosticsPath = Path.Combine(output, "text-browser-diagnostics.jsonl");
        using var fixture = new WebPdfFixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await LoadPageAsync(fixture.Url, timeout.Token);
        // The URL box raises TextChanged asynchronously; a real user can press 提取正文 right after loading.
        await Task.Delay(250, timeout.Token);
        RequireWeb(_pageReady && _browser?.CoreWebView2 is not null, "网页加载完成后状态被推迟到达的界面事件清空。");
        string fixtureScript = "document.body.innerHTML='<nav>NAVIGATION NOISE</nav><article><h1>顺手正文测试</h1>' + Array.from({length:8},(_,i)=>'<p>第'+(i+1)+'段：这是自己生成的正文内容，用来验证网页文字提取保留段落顺序，去掉导航噪声，并且不会输出脚本或者隐藏文本。每一段都应当完整出现。</p>').join('') + '<p style=\"display:none\">SECRET HIDDEN</p></article>';";
        await _browser!.CoreWebView2.ExecuteScriptAsync(fixtureScript);
        var page = await ExtractTextAsync(timeout.Token);
        RequireWeb(page.text.Contains("第1段") && page.text.Contains("第8段") && !page.text.Contains("NOISE") && !page.text.Contains("SECRET"), "Visible paragraph extraction or noise filtering failed");
        TextEditor.Text = page.title + "\n\n" + page.text; _textSource = page.url;
        string first = await SaveTextAsync(output, timeout.Token), second = await SaveTextAsync(output, timeout.Token);
        RequireWeb(first != second && File.ReadAllText(first).Contains("来源："), "TXT non-overwrite or provenance failed");
        await _browser.CoreWebView2.ExecuteScriptAsync("document.body.innerHTML='<article><p>'+'\\ue100'.repeat(600)+'</p></article>'");
        bool specialFontRejected = false;
        try { await ExtractTextAsync(timeout.Token); } catch(InvalidOperationException ex) { specialFontRejected = ex.Message.Contains("特殊字形"); }
        RequireWeb(specialFontRejected,"Private glyph text was misreported as successful content");
        await _browser.CoreWebView2.ExecuteScriptAsync(fixtureScript);
        TextEditor.Visibility = Visibility.Visible; _browser.Visibility = Visibility.Collapsed;
        UpdateEnabled();
        return new { Passed=true, Paragraphs=page.paragraphs, Chars=page.text.Length, HiddenContentExcluded=true, PrivateGlyphRejected=true, SourceIncluded=true, NoOverwrite=true };
    }

    internal async Task<object> ProbeLivePageAsync(string url, bool videoPage)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(28));
        var watch=Stopwatch.StartNew();
        try
        {
            ShowPage_Click(this,new RoutedEventArgs());
            await LoadPageAsync(url,timeout.Token);
            await Task.Delay(3500,timeout.Token);
            if(videoPage)
            {
                string json=await _browser!.CoreWebView2.ExecuteScriptAsync("JSON.stringify({title:document.title,video:!!document.querySelector('video'),direct:!![...document.querySelectorAll('video')].find(v=>/^https?:/.test(v.currentSrc||v.src)),login:/登录|验证/.test(document.body.innerText),chars:document.body.innerText.length})");
                return new { Url=url, Loaded=true, BrowserPage=JsonSerializer.Deserialize<string>(json),Seconds=watch.Elapsed.TotalSeconds };
            }
            var page=await ExtractTextAsync(timeout.Token);
            return new { Url=url, Extracted=true, Title=page.title, Characters=page.text.Length, Paragraphs=page.paragraphs, Method=page.method, Warning=page.warnings, Seconds=watch.Elapsed.TotalSeconds };
        }
        catch(Exception ex)
        {
            ResetBrowser();
            return new { Url=url,Extracted=false,Reason=ex.Message,Seconds=watch.Elapsed.TotalSeconds };
        }
    }
}
