using System.Net;
using System.Net.Sockets;
using System.Text;
using Shunshou.Core;

namespace Shunshou.App;

public sealed partial class SubtitleWorkspace
{
    internal async Task<object> VerifyWorkflowAsync(string output)
    {
        Directory.CreateDirectory(output);
        var rejected = false;
        try { await VerifyAnalyzeAsync("file:///not-a-video"); }
        catch (InvalidOperationException) { rejected = MessageBar.IsOpen && _job is null && AnalyzeButton.IsEnabled; }
        if (!rejected) throw new InvalidOperationException("字幕非法网址没有被拒绝或界面未解锁。");

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stopping = new CancellationTokenSource();
        var address = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            try
            {
                while (!stopping.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stopping.Token);
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    var request = await reader.ReadLineAsync(stopping.Token) ?? "";
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stopping.Token))) { }
                    bool caption = request.Contains("/captions.vtt", StringComparison.Ordinal);
                    var body = caption ? "WEBVTT\n\n00:00:00.000 --> 00:00:02.000\n顺手工具箱字幕验证。\n\n00:00:02.000 --> 00:00:04.000\nReadable caption text.\n" :
                        $"<!doctype html><meta charset='utf-8'><title>Subtitle verification</title><video controls src='{address}/fixture.mp4'><track kind='subtitles' srclang='zh-Hans' label='简体中文' src='{address}/captions.vtt'></video>";
                    var bytes = Encoding.UTF8.GetBytes(body);
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {(caption ? "text/vtt" : "text/html")}; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, stopping.Token); await stream.WriteAsync(bytes, stopping.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            var tracks = await VerifyAnalyzeAsync(address + "/video");
            if (tracks != 1) throw new InvalidOperationException("字幕网页解析轨道数不正确。");
            var path = await VerifyDownloadAsync(0, SubtitleOutputFormat.Text, output);
            var text = await File.ReadAllTextAsync(path);
            if (!text.Contains("顺手工具箱字幕验证。") || !text.Contains("Readable caption text.") || text.Contains("-->"))
                throw new InvalidOperationException("字幕 TXT 导出内容不正确。");
            if (_job is not null || !StartButton.IsEnabled || !AnalyzeButton.IsEnabled || !OpenButton.IsEnabled || MessageBar.IsOpen)
                throw new InvalidOperationException("字幕处理完成后界面未恢复。");
            return new { Passed = true, Tracks = tracks, Output = path, BadUrlRejected = rejected, ControlsEnabled = true };
        }
        finally { stopping.Cancel(); listener.Stop(); await server; }
    }
}
