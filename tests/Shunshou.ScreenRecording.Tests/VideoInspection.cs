using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Shunshou.ScreenRecording.Tests;

internal sealed record VideoMetadata(int Width, int Height, double FrameRate, double Duration,
    long Bitrate, int AudioStreams, string Codec, long Bytes, int DistinctDecodedFrames);

internal static class VideoInspection
{
    internal static async Task<VideoMetadata> InspectAsync(string binDirectory, string path, string artifactPrefix)
    {
        var json = await RunAsync(Path.Combine(binDirectory, "ffprobe.exe"),
            "-v", "error", "-show_streams", "-show_format", "-of", "json", path);
        await File.WriteAllTextAsync(artifactPrefix + "-ffprobe.json", json);
        using var doc = JsonDocument.Parse(json);
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.Single(s => s.GetProperty("codec_type").GetString() == "video");
        var rate = video.GetProperty("avg_frame_rate").GetString()!.Split('/');
        var fps = double.Parse(rate[0], CultureInfo.InvariantCulture) / double.Parse(rate[1], CultureInfo.InvariantCulture);
        var format = doc.RootElement.GetProperty("format");
        var md5 = await RunAsync(Path.Combine(binDirectory, "ffmpeg.exe"), "-v", "error", "-i", path,
            "-map", "0:v:0", "-vf", "fps=5,scale=160:-1", "-an", "-f", "framemd5", "-");
        await File.WriteAllTextAsync(artifactPrefix + "-decoded-frames.txt", md5);
        var distinct = md5.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith('#')).Select(l => l.Split(',').Last().Trim()).Distinct().Count();
        await RunAsync(Path.Combine(binDirectory, "ffmpeg.exe"), "-v", "error", "-ss", "0.5", "-i", path,
            "-frames:v", "1", "-y", artifactPrefix + "-frame.png");
        return new(video.GetProperty("width").GetInt32(), video.GetProperty("height").GetInt32(), fps,
            double.Parse(format.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture),
            format.TryGetProperty("bit_rate", out var bitrate) ? long.Parse(bitrate.GetString()!, CultureInfo.InvariantCulture) : 0,
            streams.Count(s => s.GetProperty("codec_type").GetString() == "audio"),
            video.GetProperty("codec_name").GetString()!, new FileInfo(path).Length, distinct);
    }

    internal static async Task<string> RunAsync(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Cannot start {executable}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
        catch { try { process.Kill(entireProcessTree: true); } catch { } throw; }
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(executable)} failed ({process.ExitCode}): {error}");
        return output;
    }
}
