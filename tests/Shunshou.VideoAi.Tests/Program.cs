using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Shunshou.Core;

var root = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine("artifacts", "ai-tests", DateTime.Now.ToString("yyyyMMdd-HHmmss")));
Directory.CreateDirectory(root);
var runner = Path.GetFullPath("runtime/ai-inpaint/runner");
var engine = Path.GetFullPath("runtime/ffmpeg/bin");
var selectedModel = args.Contains("--sttn") ? VideoAiModel.Sttn : VideoAiModel.Migan;
var model = Path.GetFullPath(args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ??
    (selectedModel == VideoAiModel.Sttn ? "runtime/ai-inpaint/sttn.onnx" : ".tools/downloads/migan_pipeline_v2.onnx"));
var ffmpeg = Path.Combine(engine, "ffmpeg.exe");
var probe = Path.Combine(engine, "ffprobe.exe");
var service = new VideoAiService(runner, engine, model, selectedModel);
var checks = new List<string>();
var source = Path.Combine(root, "source.mp4");
try
{
    Assert(service.IsAvailable, "model and runner available");
    await Run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=128x96:rate=2:duration=1",
        "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-c:v", "libopenh264", "-b:v", "500k", "-c:a", "aac", "-shortest", source]);
    var hash = SHA256.HashData(await File.ReadAllBytesAsync(source));
    var region = new VideoRegion(80, 50, 28, 20);
    var cpu = await service.ExportAsync(source, root, region, VideoAiProvider.Cpu, null, default);
    Assert(cpu.Provider == "CPU" && !cpu.UsedFallback, "explicit CPU reported");
    await Validate(cpu.OutputPath, 128, 96, 1);
    var automatic = await service.PreviewAsync(source, root, region, VideoAiProvider.Auto, null, default);
    Assert(automatic.Provider is "CPU" or "DirectML" or "DirectML + CPU", "actual provider reported");
    if (automatic.UsedFallback) Assert(automatic.Provider == "CPU" && !string.IsNullOrWhiteSpace(automatic.FallbackReason), "fallback is disclosed");
    await Validate(automatic.OutputPath, 128, 96, 1);
    var finalHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
    Assert(hash.SequenceEqual(finalHash), "original unchanged");
    var originalPixels = await Frame(source, "original.rgb");
    var aiPixels = await Frame(cpu.OutputPath, "ai.rgb");
    var difference = 0L;
    for (var y = region.Y; y < region.Y + region.Height; y++)
        for (var x = region.X; x < region.X + region.Width; x++)
            for (var c = 0; c < 3; c++) difference += Math.Abs(aiPixels[(y * 128 + x) * 3 + c] - originalPixels[(y * 128 + x) * 3 + c]);
    Assert(difference > region.Width * region.Height * 3 * 5, "real inference changes masked image content");
    await Throws<ArgumentException>(() => service.ExportAsync(source, root, new(-1, 1, 8, 8), VideoAiProvider.Cpu, null, default), "invalid selection");
    await Throws<FileNotFoundException>(() => new VideoAiService(runner, engine, Path.Combine(root, "missing.onnx")).ExportAsync(source, root, region, VideoAiProvider.Cpu, null, default), "missing model");
    using var cancellation = new CancellationTokenSource();
    var before = Directory.GetFiles(root, "*_ai-edited_*.mp4").Length;
    await Throws<OperationCanceledException>(() => service.ExportAsync(source, root, region, VideoAiProvider.Cpu,
        new InlineProgress(p => { if (p.Percent > 10 && p.Percent < 90) cancellation.Cancel(); }), cancellation.Token), "cancel running AI inference");
    Assert(before == Directory.GetFiles(root, "*_ai-edited_*.mp4").Length && Directory.GetFiles(root, "*.partial").Length == 0, "cancel publishes no partial file");
    await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = true, model, runner, automatic.Provider, automatic.UsedFallback, checks }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("PASS: " + root);
    return 0;
}
catch (Exception ex)
{
    await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = false, checks, error = ex.ToString() }));
    Console.Error.WriteLine(ex); return 1;
}

void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks.Add(message); Console.WriteLine("PASS: " + message); }
async Task Validate(string path, int width, int height, double duration)
{
    using var json = JsonDocument.Parse(await Run(probe, ["-v", "error", "-show_entries", "format=duration:stream=codec_type,width,height", "-of", "json", path]));
    var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
    var video = streams.First(s => s.GetProperty("codec_type").GetString() == "video");
    Assert(video.GetProperty("width").GetInt32() == width && video.GetProperty("height").GetInt32() == height, "output dimensions preserved");
    Assert(Math.Abs(double.Parse(json.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture) - duration) < .15, "full duration preserved");
    Assert(streams.Count(s => s.GetProperty("codec_type").GetString() == "audio") == 1, "audio retained");
    await Run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-xerror", "-err_detect", "explode", "-i", path, "-map", "0:v", "-map", "0:a?", "-f", "null", "-"]);
    checks.Add("full output decode");
}
async Task<byte[]> Frame(string input, string filename)
{
    var path = Path.Combine(root, filename);
    await Run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-i", input, "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", path]);
    return await File.ReadAllBytesAsync(path);
}
async Task Throws<T>(Func<Task> action, string message) where T : Exception
{
    try { await action(); } catch (T) { checks.Add(message); Console.WriteLine("PASS: " + message); return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + message);
}
static async Task<string> Run(string executable, IEnumerable<string> args)
{
    var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in args) info.ArgumentList.Add(argument);
    using var process = Process.Start(info)!;
    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new InvalidDataException(await stderr);
    return await stdout;
}
sealed class InlineProgress(Action<ToolProgress> callback) : IProgress<ToolProgress> { public void Report(ToolProgress value) => callback(value); }
