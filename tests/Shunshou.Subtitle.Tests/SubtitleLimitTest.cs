using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Shunshou.Core;

internal static class SubtitleLimitTest
{
    internal static async Task RunAsync(string root, string output)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var address = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/large.srt";
        long sent = 0;
        var server = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token))) { }
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.0 200 OK\r\nContent-Type: application/x-subrip\r\nConnection: close\r\n\r\n"), stop.Token);
                var block = Encoding.ASCII.GetBytes(new string('A', 65536));
                for (int index = 0; index < 1600; index++)
                {
                    await stream.WriteAsync(block, stop.Token);
                    Interlocked.Add(ref sent, block.Length);
                    await Task.Delay(5, stop.Token);
                }
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or SocketException) { }
        });
        var info = SubtitleDownloadService.ParseMetadata("https://example.org/video", JsonSerializer.Serialize(new
        { title = "Unknown size fixture", subtitles = new Dictionary<string, object[]> { ["en"] = [new { ext = "srt", url = address }] } }));
        var service = new SubtitleDownloadService(Path.Combine(root, "runtime", "video-download"), Path.Combine(root, "runtime", "ffmpeg", "bin"));
        string? rejection = null;
        var clock = Stopwatch.StartNew();
        try { await service.DownloadAsync(info, info.Tracks[0].Key, SubtitleOutputFormat.Srt, output, null, stop.Token); }
        catch (InvalidDataException exception) { rejection = exception.Message; }
        finally { stop.Cancel(); listener.Stop(); await server; }
        if (rejection is null || !rejection.Contains("32 MB") || sent > 45_000_000 || Directory.EnumerateFiles(output).Any())
            throw new Exception($"Unknown-length subtitle cap failed: sent={sent}, error={rejection}");
        var result = new { Passed = true, UnknownLengthDownloadStopped = true, BytesSent = sent, NoOutputPublished = true, ElapsedSeconds = clock.Elapsed.TotalSeconds, Error = rejection };
        await File.WriteAllTextAsync(Path.Combine(output, "limit-results.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(result));
        Console.WriteLine(output);
    }
}
