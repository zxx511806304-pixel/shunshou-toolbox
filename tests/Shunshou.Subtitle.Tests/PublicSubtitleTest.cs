using System.Diagnostics;
using System.Text.Json;
using Shunshou.Core;

internal static class PublicSubtitleTest
{
    internal static async Task RunAsync(string root, string output, string url)
    {
        var timer = Stopwatch.StartNew();
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var service = new SubtitleDownloadService(Path.Combine(root, "runtime", "video-download"), Path.Combine(root, "runtime", "ffmpeg", "bin"));
        object result;
        try
        {
            var info = await service.AnalyzeAsync(url, stop.Token);
            var track = info.Tracks.FirstOrDefault(item => !item.IsAutomatic && item.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) ??
                info.Tracks.FirstOrDefault(item => !item.IsAutomatic && item.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)) ?? info.Tracks.First();
            var path = await service.DownloadAsync(info, track.Key, SubtitleOutputFormat.Text, output, null, stop.Token);
            var text = await File.ReadAllTextAsync(path, stop.Token);
            if (text.Trim().Length < 10 || text.Contains("-->")) throw new InvalidDataException("Public subtitle TXT content is empty or contains timecodes.");
            result = new { Passed = true, Url = url, info.Title, TrackCount = info.Tracks.Count, track.Language, track.IsAutomatic, Output = path, Characters = text.Length, ElapsedSeconds = timer.Elapsed.TotalSeconds, TlsValidationEnabled = true };
        }
        catch (Exception exception)
        {
            result = new { Passed = false, Url = url, Error = exception.Message, ElapsedSeconds = timer.Elapsed.TotalSeconds, TlsValidationEnabled = true };
        }
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(output, "public-results.json"), json);
        Console.WriteLine(json);
        Console.WriteLine(output);
        if (JsonSerializer.SerializeToElement(result).GetProperty("Passed").GetBoolean() == false) Environment.ExitCode = 1;
    }
}
