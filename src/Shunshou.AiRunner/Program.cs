using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Shunshou.Core;

namespace Shunshou.AiRunner;

internal sealed record AiJob(string Input, string Output, string ModelPath, string EngineDirectory, VideoRegion Region,
    string Provider = "Auto", double? PreviewSeconds = null, string ModelKind = "migan");
internal sealed record AiEvent(string Type, string Message, double Percent = 0, string? Provider = null,
    bool UsedFallback = false, string? FallbackReason = null, string? OutputPath = null, long Frames = 0);
internal sealed record MediaInfo(int VideoIndex, int Width, int Height, double Duration, double VideoStart, string Fps, double FpsValue, int AudioTracks, int AudioChannels);

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static void Emit(AiEvent message) { Console.WriteLine(JsonSerializer.Serialize(message, Json)); Console.Out.Flush(); }

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length != 2 || args[0] != "--job") throw new ArgumentException("Usage: Shunshou.AiRunner.exe --job job.json");
            var job = JsonSerializer.Deserialize<AiJob>(await File.ReadAllTextAsync(args[1]), Json) ?? throw new InvalidDataException("缺少 AI 任务参数。");
            if (job.ModelKind is not ("migan" or "sttn")) throw new NotSupportedException("该组件不支持所选 AI 模型。");
            if (job.Provider is not ("Auto" or "Cpu" or "DirectML")) throw new ArgumentException("未知计算设备。");
            OrtEnv.Instance().DisableTelemetryEvents();
            if (!File.Exists(job.Input) || !File.Exists(job.ModelPath)) throw new FileNotFoundException("找不到视频或本地 AI 模型。");
            if (File.Exists(job.Output)) throw new IOException("输出文件已经存在，已停止以保护原文件。");
            if (job.PreviewSeconds is <= 0 || job.PreviewSeconds is { } seconds && !double.IsFinite(seconds)) throw new ArgumentException("预览时长无效。");
            var ffmpeg = Path.Combine(job.EngineDirectory, "ffmpeg.exe");
            var probe = Path.Combine(job.EngineDirectory, "ffprobe.exe");
            var directory = Path.GetDirectoryName(Path.GetFullPath(job.Output))!;
            Directory.CreateDirectory(directory);
            var inspection = await new VideoWatermarkService(job.EngineDirectory).InspectAsync(job.Input, Path.Combine(directory, "ai-frame.png"), default);
            ValidateRegion(job.Region, inspection.Width, inspection.Height);
            var source = await Probe(probe, job.Input);
            var duration = Math.Min(job.PreviewSeconds ?? source.Duration, source.Duration);
            if (duration <= source.VideoStart) throw new InvalidDataException("预览范围内没有视频画面，请直接导出或选择其他视频。");
            Emit(new("progress", "正在准备本地 AI 引擎", 2));
            var engineLabel = job.ModelKind == "sttn" ? "深度 AI" : "轻量 AI";
            using IVideoProcessor inpaint = job.ModelKind == "sttn"
                ? new SttnProcessor(job.ModelPath, job.Provider, directory, value => Emit(value with { Percent = 5 }))
                : new MiganProcessor(job.ModelPath, job.Provider, directory, value => Emit(value with { Percent = 5 }));
            var bitrate = Math.Clamp((long)inspection.Width * inspection.Height * 6, 1_000_000, 40_000_000);
            var decodeArgs = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-protocol_whitelist", "file,pipe", "-i", job.Input,
                "-map", $"0:{source.VideoIndex}", "-vf", $"scale=ceil(iw*sar):ih,setsar=1,setpts=PTS-STARTPTS,fps={source.Fps}",
                "-t", Invariant(duration - source.VideoStart), "-an", "-sn", "-dn", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1" };
            var encodeArgs = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-protocol_whitelist", "file,pipe",
                "-itsoffset", Invariant(source.VideoStart), "-f", "rawvideo", "-pixel_format", "rgb24", "-video_size", $"{inspection.Width}x{inspection.Height}",
                "-framerate", source.Fps, "-i", "pipe:0", "-protocol_whitelist", "file,pipe", "-i", job.Input,
                "-map", "0:v:0", "-map", "1:a?", "-map_metadata", "-1", "-map_chapters", "-1", "-sn", "-dn",
                "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-c:v", "libopenh264", "-rc_mode", "bitrate", "-allow_skip_frames", "0",
                "-b:v", Invariant(bitrate), "-maxrate", Invariant(bitrate), "-bufsize", Invariant(bitrate * 2), "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "192k", "-t", Invariant(duration), "-movflags", "+faststart", job.Output };
            using var decoder = Start(ffmpeg, decodeArgs, false);
            using var encoder = Start(ffmpeg, encodeArgs, true);
            var decoderError = decoder.StandardError.ReadToEndAsync();
            var encoderError = encoder.StandardError.ReadToEndAsync();
            var encoderOutput = encoder.StandardOutput.ReadToEndAsync();
            long count = 0;
            try
            {
                var frameLength = checked(inspection.Width * inspection.Height * 3);
                var first = new byte[frameLength];
                if (!await ReadFrame(decoder.StandardOutput.BaseStream, first)) throw new InvalidDataException("视频未生成可修补的帧。");
                var window = Enumerable.Repeat(first, inpaint.Radius + 1).ToList();
                long decoded = 1;
                bool ended = false;
                for (var i = 0; i < inpaint.Radius; i++)
                {
                    var next = new byte[frameLength];
                    if (!ended && await ReadFrame(decoder.StandardOutput.BaseStream, next)) { decoded++; window.Add(next); }
                    else { ended = true; window.Add(window[^1]); }
                }
                while (count < decoded)
                {
                    // Keep original neighbouring frames in the window; never feed an earlier generated result back into the model.
                    var outputFrame = (byte[])window[inpaint.Radius].Clone();
                    inpaint.Process(window, outputFrame, inspection.Width, inspection.Height, job.Region);
                    await encoder.StandardInput.BaseStream.WriteAsync(outputFrame);
                    count++;
                    Emit(new("progress", $"{engineLabel} · {inpaint.Provider} · 已处理 {count} 帧", Math.Min(88, 5 + 83 * (count / source.FpsValue) / duration),
                        inpaint.Provider, inpaint.UsedFallback, inpaint.FallbackReason, Frames: count));
                    var previousLast = window[^1]; window.RemoveAt(0);
                    var next = new byte[frameLength];
                    if (!ended && await ReadFrame(decoder.StandardOutput.BaseStream, next)) { decoded++; window.Add(next); }
                    else { ended = true; window.Add(previousLast); }
                }
                encoder.StandardInput.Close();
                await Task.WhenAll(decoder.WaitForExitAsync(), encoder.WaitForExitAsync(), encoderOutput);
                if (decoder.ExitCode != 0 || encoder.ExitCode != 0)
                    throw new InvalidDataException("视频编解码失败。\n" + Trim(await decoderError) + "\n" + Trim(await encoderError));
                if (count == 0) throw new InvalidDataException("视频未生成可修补的帧。");
            }
            finally
            {
                Kill(decoder); Kill(encoder);
                await Task.WhenAll(decoder.WaitForExitAsync(), encoder.WaitForExitAsync(), decoderError, encoderError);
            }
            Emit(new("progress", "正在检查完整时长、音轨和解码", 91, inpaint.Provider, inpaint.UsedFallback, inpaint.FallbackReason));
            var output = await Probe(probe, job.Output);
            if (output.Width != inspection.Width + inspection.Width % 2 || output.Height != inspection.Height + inspection.Height % 2 ||
                Math.Abs(output.Duration - duration) > Math.Max(.15, 1.5 / source.FpsValue) || output.AudioTracks != source.AudioTracks || output.AudioChannels != source.AudioChannels)
                throw new InvalidDataException("AI 输出的时长、尺寸或音轨检查失败，结果不会发布。");
            await Run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-xerror", "-err_detect", "explode", "-protocol_whitelist", "file,pipe",
                "-i", job.Output, "-map", "0:v:0", "-map", "0:a?", "-f", "null", "-"]);
            Emit(new("complete", engineLabel + " 修补完成", 100, inpaint.Provider, inpaint.UsedFallback, inpaint.FallbackReason, job.Output, count));
            return 0;
        }
        catch (Exception ex)
        {
            Emit(new("error", Trim(ex.Message)));
            return 1;
        }
    }

    private static void ValidateRegion(VideoRegion r, int width, int height)
    {
        if (r.X < 0 || r.Y < 0 || r.Width < 2 || r.Height < 2 || (long)r.X + r.Width > width || (long)r.Y + r.Height > height)
            throw new ArgumentException("AI 选区必须位于视频画面以内，并至少为 2 × 2 像素。");
        if (r.X == 0 && r.Y == 0 && r.Width == width && r.Height == height)
            throw new ArgumentException("AI 修补需要选区周围的画面，请不要选中整个画面。");
    }

    private static async Task<bool> ReadFrame(Stream stream, byte[] frame)
    {
        var offset = 0;
        while (offset < frame.Length)
        {
            var read = await stream.ReadAsync(frame.AsMemory(offset));
            if (read == 0) { if (offset == 0) return false; throw new InvalidDataException("视频解码产生了不完整的帧。"); }
            offset += read;
        }
        return true;
    }

    private static async Task<MediaInfo> Probe(string executable, string input)
    {
        using var json = JsonDocument.Parse(await Run(executable, ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_entries",
            "format=duration,start_time:stream=index,codec_type,width,height,duration,start_time,avg_frame_rate,r_frame_rate,channels:stream_disposition=attached_pic", "-of", "json", input]));
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "video" &&
            (!s.TryGetProperty("disposition", out var d) || Number(d, "attached_pic") == 0));
        if (video.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("文件没有视频画面。");
        var format = json.RootElement.GetProperty("format");
        var duration = Number(format, "duration");
        if (duration <= 0) duration = Number(video, "duration");
        var fps = video.TryGetProperty("avg_frame_rate", out var rate) ? rate.GetString() ?? "" : "";
        var fpsValue = Rational(fps);
        if (fpsValue <= 0 && video.TryGetProperty("r_frame_rate", out rate)) { fps = rate.GetString() ?? ""; fpsValue = Rational(fps); }
        if (!double.IsFinite(duration) || duration <= 0 || !double.IsFinite(fpsValue) || fpsValue <= 0)
            throw new InvalidDataException("无法读取可靠的视频帧率或时长。");
        var audio = streams.Where(s => s.GetProperty("codec_type").GetString() == "audio").ToArray();
        return new((int)Number(video, "index"), (int)Number(video, "width"), (int)Number(video, "height"), duration,
            Math.Max(0, Number(video, "start_time") - Number(format, "start_time")), fps, fpsValue, audio.Length, audio.Sum(s => (int)Number(s, "channels")));
    }
    private static double Number(JsonElement item, string name) => item.TryGetProperty(name, out var value) && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;
    private static double Rational(string value)
    {
        var parts = value.Split('/');
        return parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && double.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) && d > 0 ? n / d : 0;
    }
    private static string Invariant(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
    private static string Trim(string value) => value.Length <= 1800 ? value : value[..1800];
    private static Process Start(string exe, IEnumerable<string> arguments, bool input)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException("无法启动本地视频引擎。");
    }
    private static void Kill(Process process) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    private static async Task<string> Run(string exe, IEnumerable<string> arguments)
    {
        using var process = Start(exe, arguments, false);
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidDataException(Trim(await stderr));
        return await stdout;
    }
}

internal interface IVideoProcessor : IDisposable
{
    int Radius { get; }
    string Provider { get; }
    bool UsedFallback { get; }
    string? FallbackReason { get; }
    void Process(IReadOnlyList<byte[]> originalFrames, byte[] outputFrame, int width, int height, VideoRegion region);
}

internal sealed class MiganProcessor : IVideoProcessor
{
    private readonly string _model, _preference, _directory;
    private readonly Action<AiEvent> _notify;
    private InferenceSession? _session;
    private bool _profilePending;
    public string Provider { get; private set; } = "CPU";
    public bool UsedFallback { get; private set; }
    public string? FallbackReason { get; private set; }
    public int Radius => 0;
    public void Process(IReadOnlyList<byte[]> originalFrames, byte[] outputFrame, int width, int height, VideoRegion region) => Process(outputFrame, width, height, region);

    public MiganProcessor(string model, string preference, string directory, Action<AiEvent> notify)
    {
        _model = model; _preference = preference; _directory = directory; _notify = notify;
        if (preference == "Cpu") { CreateCpu(); return; }
        try
        {
            using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, EnableMemoryPattern = false,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, ProfileOutputPathPrefix = Path.Combine(directory, "ort-profile"), EnableProfiling = true };
            options.AppendExecutionProvider_DML(0);
            _session = new InferenceSession(model, options); _profilePending = true;
            Provider = "DirectML（正在验证）";
        }
        catch (Exception ex) when (IsProviderFailure(ex)) { Fallback(ex); }
        ValidateModel();
    }

    private void ValidateModel()
    {
        if (!_session!.InputMetadata.TryGetValue("image", out var image) || image.ElementType != typeof(byte) || image.Dimensions.Length != 4 ||
            !_session.InputMetadata.TryGetValue("mask", out var mask) || mask.ElementType != typeof(byte) || mask.Dimensions.Length != 4 ||
            !_session.OutputMetadata.Values.Any(o => o.ElementType == typeof(byte)))
            throw new InvalidDataException("AI 模型不是受支持的 MI-GAN uint8 图像修补 Pipeline。");
    }

    private void CreateCpu()
    {
        _session?.Dispose();
        using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, Math.Min(Environment.ProcessorCount, 8)) };
        _session = new InferenceSession(_model, options); Provider = "CPU"; _profilePending = false;
        ValidateModel();
    }

    private static bool IsProviderFailure(Exception e) => e is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException or TypeInitializationException or OutOfMemoryException;
    private void Fallback(Exception error)
    {
        Console.Error.WriteLine(error);
        if (_preference != "Auto") throw new InvalidOperationException("当前显卡或驱动无法运行此模型，请切换为自动或 CPU。", error);
        UsedFallback = true; FallbackReason = "当前显卡或驱动无法运行此模型，已改用 CPU。";
        CreateCpu(); _notify(new("provider", FallbackReason, Provider: Provider, UsedFallback: true, FallbackReason: FallbackReason));
    }

    public void Process(byte[] frame, int width, int height, VideoRegion region)
    {
        // Only transfer a context crop to the model. The original resolution and all pixels outside the selected region stay in the raw frame.
        var side = Math.Max(512L, Math.Max(region.Width, region.Height) + 256L);
        var cropWidth = (int)Math.Min(width, side); var cropHeight = (int)Math.Min(height, side);
        var left = Math.Clamp(region.X + region.Width / 2 - cropWidth / 2, 0, width - cropWidth);
        var top = Math.Clamp(region.Y + region.Height / 2 - cropHeight / 2, 0, height - cropHeight);
        var area = checked(cropWidth * cropHeight);
        var image = new byte[checked(area * 3)]; var mask = new byte[area]; Array.Fill(mask, (byte)255);
        for (var y = 0; y < cropHeight; y++)
            for (var x = 0; x < cropWidth; x++)
            {
                var source = ((top + y) * width + left + x) * 3; var pixel = y * cropWidth + x;
                image[pixel] = frame[source]; image[area + pixel] = frame[source + 1]; image[area * 2 + pixel] = frame[source + 2];
                if (left + x >= region.X && left + x < region.X + region.Width && top + y >= region.Y && top + y < region.Y + region.Height) mask[pixel] = 0;
            }
        byte[] repaired;
        try { repaired = Infer(image, mask, cropWidth, cropHeight); }
        catch (Exception ex) when (IsProviderFailure(ex) && Provider.StartsWith("DirectML", StringComparison.Ordinal))
        { Fallback(ex); repaired = Infer(image, mask, cropWidth, cropHeight); }
        if (_profilePending)
        {
            var profile = _session!.EndProfiling(); _profilePending = false;
            using var profileStream = new FileStream(profile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var data = JsonDocument.Parse(profileStream);
            var providers = data.RootElement.EnumerateArray().Where(e => e.TryGetProperty("args", out var a) && a.TryGetProperty("provider", out _))
                .Select(e => e.GetProperty("args").GetProperty("provider").GetString() ?? "").Distinct().ToArray();
            if (!providers.Any(p => p.Contains("Dml", StringComparison.OrdinalIgnoreCase)))
            {
                Fallback(new InvalidOperationException("本次推理未使用 DirectML 运算节点。"));
                repaired = Infer(image, mask, cropWidth, cropHeight);
            }
            else Provider = providers.Any(p => p.Contains("CPU", StringComparison.OrdinalIgnoreCase)) ? "DirectML + CPU" : "DirectML";
            _notify(new("provider", $"实际计算设备：{Provider}", Provider: Provider, UsedFallback: UsedFallback, FallbackReason: FallbackReason));
        }
        for (var y = region.Y; y < region.Y + region.Height; y++)
            for (var x = region.X; x < region.X + region.Width; x++)
            {
                var pixel = (y - top) * cropWidth + x - left; var target = (y * width + x) * 3;
                frame[target] = repaired[pixel]; frame[target + 1] = repaired[area + pixel]; frame[target + 2] = repaired[area * 2 + pixel];
            }
    }

    private byte[] Infer(byte[] image, byte[] mask, int width, int height)
    {
        var inputs = new[] { NamedOnnxValue.CreateFromTensor("image", new DenseTensor<byte>(image, [1, 3, height, width])),
            NamedOnnxValue.CreateFromTensor("mask", new DenseTensor<byte>(mask, [1, 1, height, width])) };
        using var results = _session!.Run(inputs);
        var result = results.First().AsTensor<byte>();
        if (result.Dimensions.Length != 4 || result.Dimensions[0] != 1 || result.Dimensions[1] != 3 || result.Dimensions[2] != height || result.Dimensions[3] != width)
            throw new InvalidDataException("AI 返回的画面尺寸不正确。");
        return result.ToArray();
    }
    public void Dispose() => _session?.Dispose();
}
