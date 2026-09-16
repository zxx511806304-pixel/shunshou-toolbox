using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Shunshou.Core;

var root = Path.GetFullPath(args.Where((a, index) => !a.StartsWith("--") && (index == 0 || args[index - 1] != "--engine")).FirstOrDefault()
    ?? Path.Combine("artifacts", "watermark", DateTime.Now.ToString("yyyyMMdd-HHmmss")));
Directory.CreateDirectory(root);
var engineOption = Array.IndexOf(args, "--engine");
var engine = Path.GetFullPath(engineOption >= 0 && engineOption + 1 < args.Length ? args[engineOption + 1] : "runtime/ffmpeg/bin");
var ffmpeg = Path.Combine(engine, "ffmpeg.exe");
var ffprobe = Path.Combine(engine, "ffprobe.exe");
var service = new VideoWatermarkService(engine);
var assemblyPath = typeof(VideoWatermarkService).Assembly.Location;
var assemblySha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(assemblyPath)));
Console.WriteLine($"Assembly: {assemblyPath}\nSHA256: {assemblySha256}\nEngine: {engine}");
var input = Path.Combine(root, "original.mp4");
var report = new List<string>();
try
{
    await Run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=24:duration=4",
        "-f", "lavfi", "-i", "sine=frequency=660:sample_rate=48000:duration=4", "-c:v", "libopenh264", "-b:v", "1500k", "-c:a", "aac", "-shortest", input]);
    var inputHash = SHA256.HashData(await File.ReadAllBytesAsync(input));
    var inspection = await service.InspectAsync(input, Path.Combine(root, "source.png"), default);
    Check(inspection.Width == 320 && inspection.Height == 240 && Math.Abs(inspection.Duration - 4) < .1 && inspection.HasAudio, "inspect dimensions, duration and audio");
    var region = new VideoRegion(180, 150, 79, 41);
    var blurred = await service.ExportAsync(input, root, VideoWatermarkMode.Blur, region, null, default);
    await Validate(blurred, 320, 240, 4, 1);
    var finalInputHash = SHA256.HashData(await File.ReadAllBytesAsync(input));
    Check(inputHash.SequenceEqual(finalInputHash), "original unchanged");
    report.Add("blur export + complete decode + source unchanged");
    if (!args.Contains("--micro"))
    {
        foreach (var mode in new[] { VideoWatermarkMode.Cover, VideoWatermarkMode.Repair, VideoWatermarkMode.Crop })
        {
            var output = await service.ExportAsync(input, root, mode, region, null, default);
            await Validate(output, mode == VideoWatermarkMode.Crop ? 80 : 320, mode == VideoWatermarkMode.Crop ? 42 : 240, 4, 1);
            report.Add($"{mode}: full duration, expected dimensions, audio and decode");
            if (mode == VideoWatermarkMode.Cover)
            {
                var pixels = Path.Combine(root, "cover-pixel.rgb");
                await Run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-i", output, "-vf", "crop=2:2:210:160,format=rgb24", "-frames:v", "1", "-f", "rawvideo", pixels]);
                Check((await File.ReadAllBytesAsync(pixels)).All(b => b < 15), "cover produces black region");
            }
        }
        var preview = await service.PreviewAsync(input, root, VideoWatermarkMode.Blur, region, null, default);
        await Validate(preview, 320, 240, 3, 1);
        report.Add("preview limited to 3 seconds");
        var rotated = Path.Combine(root, "rotated.mp4");
        await Run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-display_rotation:v:0", "90", "-i", input, "-c", "copy", rotated]);
        var rotationData = await Run(ffprobe, ["-v", "error", "-show_entries", "stream_side_data=rotation", "-of", "json", rotated]);
        using (var rotationJson = JsonDocument.Parse(rotationData))
            Check(rotationJson.RootElement.GetProperty("streams")[0].GetProperty("side_data_list")[0].GetProperty("rotation").GetInt32() == 90, "fixture has real rotation metadata");
        var rotatedInfo = await service.InspectAsync(rotated, Path.Combine(root, "rotated.png"), default);
        Check(rotatedInfo.Width == 240 && rotatedInfo.Height == 320, "rotation reflected by preview coordinates");
        var rotatedOutput = await service.ExportAsync(rotated, root, VideoWatermarkMode.Crop, new(10, 20, 181, 263), null, default);
        await Validate(rotatedOutput, 182, 264, 4, 1);
        var reinspection = await service.InspectAsync(rotatedOutput, Path.Combine(root, "rotated-result.png"), default);
        Check(reinspection.Width == 182 && reinspection.Height == 264, "rotation metadata cleared after transform");
        report.Add("rotated input preview, odd rectangle crop and upright output");

        var silent = Path.Combine(root, "silent.mp4");
        await Run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-i", input, "-an", "-c:v", "copy", silent]);
        var silentOutput = await service.ExportAsync(silent, root, VideoWatermarkMode.Cover, region, null, default);
        await Validate(silentOutput, 320, 240, 4, 0);
        report.Add("video without audio");

        await Throws<FileNotFoundException>(() => service.InspectAsync(Path.Combine(root, "missing.mp4"), Path.Combine(root, "missing.png"), default), "missing input");
        await Throws<ArgumentException>(() => service.InspectAsync("https://example.org/video.mp4", Path.Combine(root, "remote.png"), default), "URL rejected by local editor");
        await Throws<ArgumentOutOfRangeException>(() => service.ExportAsync(input, root, VideoWatermarkMode.Blur, new(-1, 0, 30, 30), null, default), "negative region");
        await Throws<ArgumentOutOfRangeException>(() => service.ExportAsync(input, root, VideoWatermarkMode.Blur, new(310, 230, 30, 30), null, default), "out-of-bounds region");
        await Throws<ArgumentOutOfRangeException>(() => service.ExportAsync(input, root, VideoWatermarkMode.Crop, new(int.MaxValue, 0, int.MaxValue, 40), null, default), "overflow region");
        await Throws<ArgumentException>(() => service.ExportAsync(input, root, VideoWatermarkMode.Repair, new(0, 0, 40, 40), null, default), "repair requires surrounding pixels");
        await Throws<ArgumentException>(() => service.ExportAsync(input, root, VideoWatermarkMode.Repair, new(10, 10, 200, 180), null, default), "repair resource bound");
        await Throws<ArgumentOutOfRangeException>(() => service.ExportAsync(input, root, (VideoWatermarkMode)99, region, null, default), "unknown mode");
        await Throws<IOException>(() => service.InspectAsync(input, Path.Combine(root, "source.png"), default), "existing preview not overwritten");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Throws<OperationCanceledException>(() => service.ExportAsync(input, root, VideoWatermarkMode.Blur, region, null, cancelled.Token), "pre-cancel");
        var before = Directory.GetFiles(root, "*_edited_*.mp4").Length;
        using var midway = new CancellationTokenSource();
        await Throws<OperationCanceledException>(() => service.ExportAsync(input, root, VideoWatermarkMode.Blur, region,
            new ImmediateProgress(p => { if (p.Percent >= 5) midway.Cancel(); }), midway.Token), "cancel before processing");
        Check(Directory.GetFiles(root, "*_edited_*.mp4").Length == before && Directory.GetFiles(root, "*.partial").Length == 0, "cancel publishes no partial output");
        report.Add("invalid input/regions/mode, resource bounds and cancellation without partial outputs");
    }
    await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = true, assemblyPath, assemblySha256, engine, checks = report }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"PASS ({report.Count} checks): {root}");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = false, assemblyPath, assemblySha256, engine, checks = report, error = error.ToString() }));
    return 1;
}

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + message);
    Console.WriteLine("PASS: " + message);
}

async Task Validate(string file, int width, int height, double duration, int audioTracks)
{
    var text = await Run(ffprobe, ["-v", "error", "-show_entries", "format=duration:stream=codec_type,width,height", "-of", "json", file]);
    using var data = JsonDocument.Parse(text);
    var streams = data.RootElement.GetProperty("streams").EnumerateArray().ToArray();
    var video = streams.First(s => s.GetProperty("codec_type").GetString() == "video");
    Check(video.GetProperty("width").GetInt32() == width && video.GetProperty("height").GetInt32() == height, "output dimensions");
    Check(Math.Abs(double.Parse(data.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture) - duration) < .15, "full expected duration");
    Check(streams.Count(s => s.GetProperty("codec_type").GetString() == "audio") == audioTracks, "audio track count");
    await Run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-xerror", "-err_detect", "explode", "-i", file, "-map", "0:v", "-map", "0:a?", "-f", "null", "-"]);
}

async Task Throws<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); }
    catch (T) { Console.WriteLine("PASS: " + name); return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + name);
}

static async Task<string> Run(string executable, IEnumerable<string> arguments)
{
    var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    using var process = Process.Start(info)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var error = await stderr;
    if (process.ExitCode != 0) throw new InvalidOperationException(error);
    return await stdout;
}

sealed class ImmediateProgress(Action<ToolProgress> callback) : IProgress<ToolProgress>
{
    public void Report(ToolProgress value) => callback(value);
}
