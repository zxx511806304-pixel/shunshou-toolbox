using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Shunshou.Core;

namespace Shunshou.SmokeTests;

public static class VideoDownloadSmokeTests
{
    public static async Task RunAsync(string root)
    {
        root = Path.Combine(Path.GetFullPath(root), "video-download");
        Directory.CreateDirectory(root);
        var repo = FindRepository();
        var engines = Path.Combine(repo, "runtime", "ffmpeg", "bin");
        var service = new VideoDownloadService(Path.Combine(repo, "runtime", "video-download"), engines);
        foreach (var url in new[] { "", "file:///C:/secret.mp4", "ftp://example.com/a", "https://u:p@example.com/video", "https://example.com/\n--exec=bad", "--exec=cmd" })
            await CompressionTests.Throws<ArgumentException>(() => service.AnalyzeAsync(url, default), "reject unsupported/credential/control-character URL");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await CompressionTests.Throws<OperationCanceledException>(() => service.DownloadAsync("https://example.com/test.mp4", root, null, null, cancelled.Token), "pre-cancelled download never starts an engine");
        }
        var source = Path.Combine(root, "fixture.mp4");
        await CommandAsync(Path.Combine(engines, "ffmpeg.exe"), ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i",
            "testsrc2=size=160x120:rate=12:duration=1", "-c:v", "libopenh264", "-b:v", "150000", "-movflags", "+faststart", source]);
        var originalHash = CompressionTests.HashFile(source);
        var payload = await File.ReadAllBytesAsync(source);
        using var server = new FixtureServer(payload);
        var output = Path.Combine(root, "output");
        var previousNoProxy = Environment.GetEnvironmentVariable("NO_PROXY");
        Environment.SetEnvironmentVariable("NO_PROXY", "127.0.0.1,localhost");
        try
        {
            var metadata = await service.AnalyzeAsync(server.Url, default);
            CompressionTests.Check(metadata.Title.Length > 0 && metadata.Url == server.Url, "HTTP fixture metadata parsed");
            var file = await service.DownloadAsync(server.Url, output, null, null, default);
            var second = await service.DownloadAsync(server.Url, output, null, null, default);
            CompressionTests.Check(file != second && File.Exists(file) && File.Exists(second), "repeat downloads retain both completed outputs");
            CompressionTests.Check(CompressionTests.HashFile(file) == originalHash && CompressionTests.HashFile(second) == originalHash, "direct media downloads are byte exact");
            CompressionTests.Check(!Directory.EnumerateFiles(output).Any(path => path.EndsWith(".partial", StringComparison.Ordinal)), "only completed media published");
            await CompressionTests.Throws<InvalidDataException>(() => service.DownloadAsync(server.Url.Replace("fixture.mp4", "missing.mp4"), output, null, null, default), "HTTP failure publishes no result");
            CompressionTests.Check(Directory.EnumerateFiles(output).Count() == 2, "failed download preserves existing outputs");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
            await CompressionTests.Throws<OperationCanceledException>(() => service.AnalyzeAsync(server.Url.Replace("fixture.mp4", "slow.mp4"), cancellation.Token), "cancel interrupts a running downloader");
            CompressionTests.Check(CompressionTests.HashFile(source) == originalHash, "fixture source remains untouched");
            Console.WriteLine("PASS: link validation, isolated runtime, real local HTTP analysis/download, byte-exact output, no overwrite, failed request and cancellation.");
        }
        finally { Environment.SetEnvironmentVariable("NO_PROXY", previousNoProxy); }
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "runtime", "video-download", "python.exe"))) return directory.FullName;
        throw new DirectoryNotFoundException("Pinned video-download runtime is missing.");
    }

    private static async Task CommandAsync(string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Fixture engine did not start.");
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
    }

    private sealed class FixtureServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stopped = new();
        private readonly byte[] payload;
        public string Url { get; }

        public FixtureServer(byte[] payload)
        {
            this.payload = payload;
            listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/fixture.mp4";
            _ = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!stopped.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(stopped.Token);
                    _ = RespondAsync(client);
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) when (stopped.IsCancellationRequested) { }
        }

        private async Task RespondAsync(TcpClient client)
        {
            using (client)
            try
            {
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                var request = await reader.ReadLineAsync(stopped.Token) ?? "";
                while (await reader.ReadLineAsync(stopped.Token) is { Length: > 0 }) { }
                if (request.Contains("slow.mp4", StringComparison.Ordinal)) await Task.Delay(20_000, stopped.Token);
                var missing = request.Contains("missing.mp4", StringComparison.Ordinal);
                var body = missing ? Encoding.UTF8.GetBytes("missing") : payload;
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(missing ? "404 Not Found" : "200 OK")}\r\nContent-Type: video/mp4\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, stopped.Token);
                if (!request.StartsWith("HEAD ", StringComparison.Ordinal)) await stream.WriteAsync(body, stopped.Token);
            }
            catch (IOException) { }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        public void Dispose() { stopped.Cancel(); listener.Stop(); stopped.Dispose(); }
    }
}
