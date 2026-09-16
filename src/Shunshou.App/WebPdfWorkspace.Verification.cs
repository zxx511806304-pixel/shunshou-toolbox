using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Web.WebView2.Core;
using UglyToad.PdfPig;

namespace Shunshou.App;

public sealed partial class WebPdfWorkspace
{
    private string? _browserDiagnosticsPath;
    private void TraceBrowser(string name, object detail)
    {
        if (_browserDiagnosticsPath is null) return;
        try { File.AppendAllText(_browserDiagnosticsPath, System.Text.Json.JsonSerializer.Serialize(new { At = DateTimeOffset.Now, Event = name, Detail = detail }) + Environment.NewLine); } catch (IOException) { }
    }

    internal async Task<object> VerifyWorkflowAsync(string output)
    {
        Directory.CreateDirectory(output);
        _browserDiagnosticsPath = Path.Combine(output, "browser-diagnostics.jsonl");
        foreach (string invalid in new[] { "", "file:///C:/Windows/win.ini", "javascript:alert(1)", "data:text/html,test", "https://name:password@example.com/" })
        {
            bool rejected = false;
            try { ValidateUrl(invalid); } catch (ArgumentException) { rejected = true; }
            RequireWeb(rejected, "Non-web URL or URL credentials were accepted.");
        }
        using var fixture = new WebPdfFixture();
        using (var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }))
        {
            var html = await client.GetStringAsync(fixture.Url);
            RequireWeb(html.Contains("WEB PDF PAGE 1"), "Loopback HTTP fixture was not reachable before WebView initialization.");
            TraceBrowser("fixture-http-ready", new { fixture.Url });
        }
        UrlBox.Text = fixture.Url;
        await RunJobAsync(ct => LoadPageAsync(fixture.Url, ct));
        RequireWeb(_pageReady && _job is null && !MessageBar.IsOpen && SaveButton.IsEnabled, "Fixture page did not load or controls remained locked: " + MessageBar.Message);
        RequireWeb(_browser!.CoreWebView2.Profile.IsInPrivateModeEnabled, "Web preview did not use an in-private profile.");
        RequireWeb(_environment!.UserDataFolder.StartsWith(_sessionDirectory, StringComparison.OrdinalIgnoreCase), "Web preview reused a browser profile.");
        string? first = null, second = null;
        OutputBox.Text = output;
        await RunJobAsync(async ct => { first = _output = await SavePdfAsync(output, ct); }, 120);
        RequireWeb(first is not null && File.Exists(first), "No real PDF was exported: " + MessageBar.Message);
        int pages;
        double width, height;
        using (var document = PdfDocument.Open(first!))
        {
            pages = document.NumberOfPages;
            RequireWeb(pages == 3, "A three-page print document did not export exactly three pages.");
            for (int page = 1; page <= pages; page++)
                RequireWeb(document.GetPage(page).Text.Contains($"WEB PDF PAGE {page}"), $"PDF page {page} text was missing or not selectable.");
            width = document.GetPage(1).Width;
            height = document.GetPage(1).Height;
            RequireWeb(Math.Abs(width - 595.28) < 2 && Math.Abs(height - 841.89) < 2, "PDF was not A4 portrait.");
        }
        await RunJobAsync(async ct => { second = await SavePdfAsync(output, ct); }, 120);
        RequireWeb(first != second && second is not null && File.Exists(second), "Saving twice overwrote the first PDF.");
        await RunJobAsync(ct => LoadPageAsync(fixture.Url + "missing", ct));
        RequireWeb(!_pageReady && !SaveButton.IsEnabled && MessageBar.IsOpen && MessageBar.Message.Contains("404"), "HTTP error was silently exported as a successful webpage.");
        var watch = Stopwatch.StartNew();
        await RunJobAsync(async ct =>
        {
            _job!.CancelAfter(200);
            await LoadPageAsync(fixture.Url + "slow", ct);
        });
        RequireWeb(watch.Elapsed < TimeSpan.FromSeconds(4) && _job is null && !SaveButton.IsEnabled && LoadButton.IsEnabled, "Cancelled navigation did not restore the controls promptly.");
        long cancellationMilliseconds = watch.ElapsedMilliseconds;
        await RunJobAsync(ct => LoadPageAsync(fixture.Url, ct));
        RequireWeb(_pageReady && SaveButton.IsEnabled, "Web preview did not recover after cancellation: " + MessageBar.Message);
        var image = Path.Combine(output, "webpage-render.png");
        using (var stream = File.Create(image))
            await _browser!.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream.AsRandomAccessStream());
        StatusText.Text = "已保存 · " + Path.GetFileName(first);
        return new
        {
            Passed = true,
            Pdf = first,
            DuplicatePdf = second,
            Pages = pages,
            A4Points = new { Width = width, Height = height },
            SelectableTextVerified = true,
            PrivateProfileVerified = true,
            InvalidUrlRejected = true,
            Http404Rejected = true,
            CancellationMilliseconds = cancellationMilliseconds,
            Render = image,
            RuntimeVersion = _environment!.BrowserVersionString
        };
    }

    private static void RequireWeb(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    // A loopback-only fixture exercises the actual WebView navigation/print APIs without
    // relying on an external website, an account, or HttpListener URL reservations.
    private sealed class WebPdfFixture : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _server;
        internal string Url { get; }
        internal WebPdfFixture()
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
            _server = ServeAsync();
        }
        private async Task ServeAsync()
        {
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                    _ = ReplyAsync(client);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) when (_cancellation.IsCancellationRequested) { }
        }
        private async Task ReplyAsync(TcpClient client)
        {
            using (client)
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                string? request = await reader.ReadLineAsync(_cancellation.Token);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_cancellation.Token))) { }
                if (request?.Contains(" /slow ") == true) { await Task.Delay(Timeout.Infinite, _cancellation.Token); return; }
                bool missing = request?.Contains(" /missing ") == true;
                var body = Encoding.UTF8.GetBytes(missing ? "Not found" : FixtureHtml);
                var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {(missing ? "404 Not Found" : "200 OK")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, _cancellation.Token);
                await stream.WriteAsync(body, _cancellation.Token);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
        }
        public void Dispose() { _cancellation.Cancel(); _listener.Stop(); }
        private const string FixtureHtml = """
            <!doctype html><html><head><meta charset="utf-8"><title>Shunshou Web PDF Check</title>
            <style>@page { size:A4; margin:10mm } body{font:16px Arial,sans-serif;color:#263426;margin:0;background:#fff}
            section{height:240mm;break-after:page;padding:8mm;box-sizing:border-box}section:last-child{break-after:auto}
            h1{font-size:28px}p{max-width:150mm;line-height:1.6}.sample{padding:12mm;background:#edf0e8;border:1px solid #59664f}</style></head>
            <body><section><h1>WEB PDF PAGE 1</h1><p>This page checks selectable text and standard A4 printing.</p><div class="sample">Shunshou Toolbox · Local PDF export</div></section>
            <section><h1>WEB PDF PAGE 2</h1><p>Page breaks must stay intact across the exported document.</p><div class="sample">Second page</div></section>
            <section><h1>WEB PDF PAGE 3</h1><p>The final page checks complete output and preserved text.</p><div class="sample">Third page</div></section></body></html>
            """;
    }
}
