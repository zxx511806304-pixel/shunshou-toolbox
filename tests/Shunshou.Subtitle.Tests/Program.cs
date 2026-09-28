using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Shunshou.Core;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var output = Path.Combine(root, "artifacts", "subtitles-v100", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(output);
var checks = new List<string>();
var publicUrlIndex = Array.IndexOf(args, "--public-url");
if (publicUrlIndex >= 0 && publicUrlIndex + 1 < args.Length)
{
    await PublicSubtitleTest.RunAsync(root, output, args[publicUrlIndex + 1]);
    return;
}
if (args.Contains("--limits-only"))
{
    await SubtitleLimitTest.RunAsync(root, output);
    return;
}
void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
async Task RejectAsync<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); } catch (T) { Check(true, name); return; }
    throw new Exception(name + " was accepted");
}
const string sourceUrl = "https://example.org/public-video";
const string srt = "1\n00:00:00,500 --> 00:00:02,000\n你好，<b>世界</b> &amp; 字幕。\n\n2\n00:00:02,100 --> 00:00:03,800\nSecond line.\n";
string Metadata(Dictionary<string, object[]>? manual = null, Dictionary<string, object[]>? automatic = null) => JsonSerializer.Serialize(new
{
    id = "fixture", title = "../CON:测试字幕", subtitles = manual ?? new(), automatic_captions = automatic ?? new(),
    http_headers = new Dictionary<string, string> { ["Referer"] = sourceUrl, ["Cookie"] = "must-not-copy", ["User-Agent"] = "Test\r\nInjected: no" }
});
var info = SubtitleDownloadService.ParseMetadata(sourceUrl, Metadata(
    new() { ["en"] = [new { ext = "srt", data = srt, name = "English" }], ["zh-Hans"] = [new { ext = "srt", data = srt, name = "简体中文" }],
        ["live_chat"] = [new { ext = "srt", data = srt }], ["../../all,.*"] = [new { ext = "srt", data = srt }],
        ["bad"] = [new { ext = "srt", url = "file:///C:/secret" }], ["bad_ext"] = [new { ext = "exe", data = "bad" }] },
    new() { ["zh-Hans"] = [new { ext = "srt", data = srt, name = "简体中文" }] }));
Check(info.Tracks.Count == 4, "manual and auto tracks retained; chat and unsafe formats excluded");
Check(info.Tracks[0].Language == "zh-Hans" && !info.Tracks[0].IsAutomatic, "Chinese website subtitle preferred");
Check(info.Tracks.Count(track => track.Language == "zh-Hans") == 2 && info.Tracks[^1].IsAutomatic, "same language manual and auto remain independently selectable");
Check(!info.Headers.ContainsKey("Cookie") && !info.Headers.ContainsKey("User-Agent") && info.Headers.ContainsKey("Referer"), "metadata headers whitelist rejects cookie and CRLF");
Check(SubtitleDownloadService.ParseMetadata(sourceUrl, "{\"title\":\"No captions\"}").Tracks.Count == 0, "no-caption result is empty without fake tracks");
await RejectAsync<NotSupportedException>(() => Task.FromResult(SubtitleDownloadService.ParseMetadata(sourceUrl, "{\"entries\":[]}")), "playlist rejected");
await RejectAsync<NotSupportedException>(() => Task.FromResult(SubtitleDownloadService.ParseMetadata(sourceUrl, "{\"is_live\":true}")), "live rejected");
foreach (var bad in new[] { "file:///C:/secret", "ftp://example.com/a", "https://user:secret@example.com/a", "--exec=cmd", "https://example.com/\npath" })
    await RejectAsync<ArgumentException>(() => Task.FromResult(SubtitleDownloadService.ParseMetadata(bad, "{}")), "invalid URL rejected " + bad.Trim());
var cues = SubtitleDownloadService.ParseSrt(srt);
Check(cues.Count == 2 && cues[0].Start == "00:00:00,500", "SRT cues parsed with exact timing");
Check(SubtitleDownloadService.Render(cues, SubtitleOutputFormat.Text).Contains("你好，世界 & 字幕。"), "TXT preserves Chinese and decodes markup");
Check(SubtitleDownloadService.Render(cues, SubtitleOutputFormat.Vtt).StartsWith("WEBVTT\n\n1\n00:00:00.500 --> 00:00:02.000"), "VTT header and decimal timings");
Check(SubtitleDownloadService.ParseSrt("1\n00:77:00,000 --> 00:78:00,000\nbad").Count == 0, "invalid timing rejected");
var service = new SubtitleDownloadService(Path.Combine(root, "runtime", "video-download"), Path.Combine(root, "runtime", "ffmpeg", "bin"));
await RejectAsync<ArgumentException>(() => service.DownloadAsync(info, "manual:all", SubtitleOutputFormat.Srt, output, null, CancellationToken.None), "unlisted track rejected");
foreach (var format in Enum.GetValues<SubtitleOutputFormat>())
{
    var track = format == SubtitleOutputFormat.Text ? info.Tracks.Single(item => item.Language == "../../all,.*") : info.Tracks[0];
    var path = await service.DownloadAsync(info, track.Key, format, output, null, CancellationToken.None);
    Check(Path.GetDirectoryName(path) == output && File.Exists(path), "actual yt-dlp export " + format + " confined to output directory");
    var content = await File.ReadAllTextAsync(path);
    Check(content.Contains("Second line.") && content.Contains("你好"), "actual " + format + " content readable");
}
var autoOutput = await service.DownloadAsync(info, info.Tracks[^1].Key, SubtitleOutputFormat.Srt, output, null, CancellationToken.None);
Check(File.Exists(autoOutput), "actual automatic subtitle path works");
Check(!Directory.EnumerateFiles(output).Any(path => path.EndsWith(".partial") || path.EndsWith(".mp4")), "no partials or videos exported");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    await RejectAsync<OperationCanceledException>(() => service.DownloadAsync(info, info.Tracks[0].Key, SubtitleOutputFormat.Srt, output, null, cancelled.Token), "pre-cancel stops download");
}

// A local HTTP fixture exercises actual HTML extraction and real process cancellation without an external service.
using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
var address = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
using var serverStop = new CancellationTokenSource();
var pendingClients = new List<Task>();
var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var server = Task.Run(async () =>
{
    try
    {
        while (!serverStop.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(serverStop.Token);
            var task = Task.Run(async () =>
            {
                using (client)
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true))
                {
                    var request = await reader.ReadLineAsync(serverStop.Token) ?? "";
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(serverStop.Token))) { }
                    if (request.Contains("/hang")) { received.TrySetResult(); await Task.Delay(Timeout.Infinite, serverStop.Token); return; }
                    var body = request.Contains("/track.vtt") ? "WEBVTT\n\n00:00:00.000 --> 00:00:02.000\nFixture captions.\n" :
                        $"<!doctype html><title>Public subtitle fixture</title><video controls src='{address}/video.mp4'><track kind='subtitles' srclang='en' label='English' src='{address}/track.vtt'></video>";
                    var bytes = Encoding.UTF8.GetBytes(body);
                    var header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: " + (request.Contains("/track.vtt") ? "text/vtt" : "text/html") + "\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, serverStop.Token); await stream.WriteAsync(bytes, serverStop.Token);
                }
            }, serverStop.Token);
            lock (pendingClients) pendingClients.Add(task);
        }
    }
    catch (OperationCanceledException) { }
});
try
{
    var onlineInfo = await service.AnalyzeAsync(address + "/video", CancellationToken.None);
    Check(onlineInfo.Tracks.Count == 1 && onlineInfo.Tracks[0].Language == "en", "actual yt-dlp extracts HTML5 webpage subtitle metadata");
    var downloaded = await service.DownloadAsync(onlineInfo, onlineInfo.Tracks[0].Key, SubtitleOutputFormat.Text, output, null, CancellationToken.None);
    Check((await File.ReadAllTextAsync(downloaded)).Contains("Fixture captions."), "actual HTTP subtitle download and VTT-to-TXT conversion");
    using var cancel = new CancellationTokenSource();
    var analyze = service.AnalyzeAsync(address + "/hang", cancel.Token);
    await received.Task.WaitAsync(TimeSpan.FromSeconds(30));
    var stopwatch = Stopwatch.StartNew(); cancel.Cancel();
    await RejectAsync<OperationCanceledException>(async () => await analyze.WaitAsync(TimeSpan.FromSeconds(5)), "running downloader cancels while server stalls");
    Check(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "cancel exits promptly");
}
finally
{
    serverStop.Cancel(); listener.Stop(); await server;
    Task[] clients; lock (pendingClients) clients = pendingClients.ToArray();
    try { await Task.WhenAll(clients); } catch (OperationCanceledException) { }
}
await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { Passed = true, Checks = checks, Output = output }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(output);
