using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Shunshou.Core;

namespace Shunshou.AiRunner;

/// <summary>True five-frame STTN attention, using a stable crop across original neighbouring frames.</summary>
internal sealed class SttnProcessor : IVideoProcessor
{
    private const int ModelWidth = 432, ModelHeight = 240, Plane = ModelWidth * ModelHeight;
    private readonly string _model, _preference, _directory;
    private readonly Action<AiEvent> _notify;
    private InferenceSession? _session;
    private bool _profilePending;
    public int Radius => 2;
    public string Provider { get; private set; } = "CPU";
    public bool UsedFallback { get; private set; }
    public string? FallbackReason { get; private set; }

    public SttnProcessor(string model, string preference, string directory, Action<AiEvent> notify)
    {
        _model = model; _preference = preference; _directory = directory; _notify = notify;
        if (preference == "Cpu") { CreateCpu(); return; }
        try
        {
            using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, EnableMemoryPattern = false,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, ProfileOutputPathPrefix = Path.Combine(directory, "sttn-ort-profile"), EnableProfiling = true };
            // A single fused temporal graph can exceed Windows' GPU scheduling timeout on slower adapters.
            // Separate operator dispatches retain DirectML acceleration without requiring system TDR changes.
            options.AddSessionConfigEntry("ep.dml.disable_graph_fusion", "1");
            options.AppendExecutionProvider_DML(0);
            _session = new InferenceSession(model, options); _profilePending = true;
            Provider = "DirectML（正在验证）";
        }
        catch (Exception ex) when (IsProviderFailure(ex)) { Fallback(ex); }
        ValidateModel();
    }

    private void ValidateModel()
    {
        if (!_session!.InputMetadata.TryGetValue("frames", out var frames) || frames.ElementType != typeof(float) || !frames.Dimensions.SequenceEqual(new[] { 1, 5, 3, ModelHeight, ModelWidth }) ||
            !_session.InputMetadata.TryGetValue("masks", out var masks) || masks.ElementType != typeof(float) || !masks.Dimensions.SequenceEqual(new[] { 1, 5, 1, ModelHeight, ModelWidth }) ||
            !_session.OutputMetadata.TryGetValue("result", out var output) || output.ElementType != typeof(float) || !output.Dimensions.SequenceEqual(new[] { 1, 3, ModelHeight, ModelWidth }))
            throw new InvalidDataException("深度 AI 组件不是受支持的 STTN 五帧时序模型。");
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
        if (_preference != "Auto") throw new InvalidOperationException("当前显卡或驱动无法运行深度模型，请切换为自动或 CPU。", error);
        UsedFallback = true; FallbackReason = "当前显卡或驱动无法运行深度模型，已改用 CPU。";
        CreateCpu(); _notify(new("provider", FallbackReason, Provider: Provider, UsedFallback: true, FallbackReason: FallbackReason));
    }

    public void Process(IReadOnlyList<byte[]> originalFrames, byte[] outputFrame, int width, int height, VideoRegion region)
    {
        if (originalFrames.Count != 5) throw new ArgumentException("STTN 需要五帧上下文。");
        // Keep the watermark large enough in the model's input. Resizing a whole 4K frame would erase small details.
        var scale = Math.Max(1d, Math.Max((region.Width + 128d) / ModelWidth, (region.Height + 96d) / ModelHeight));
        var cropWidth = Math.Min(width, (int)Math.Ceiling(ModelWidth * scale));
        var cropHeight = Math.Min(height, (int)Math.Ceiling(ModelHeight * scale));
        var left = Math.Clamp(region.X + region.Width / 2 - cropWidth / 2, 0, width - cropWidth);
        var top = Math.Clamp(region.Y + region.Height / 2 - cropHeight / 2, 0, height - cropHeight);
        var frames = new float[5 * 3 * Plane];
        var masks = new float[5 * Plane];
        var marginX = 3d * cropWidth / ModelWidth; var marginY = 3d * cropHeight / ModelHeight;
        for (var y = 0; y < ModelHeight; y++)
            for (var x = 0; x < ModelWidth; x++)
            {
                var sourceX = left + (x + .5) * cropWidth / ModelWidth - .5;
                var sourceY = top + (y + .5) * cropHeight / ModelHeight - .5;
                var pixel = y * ModelWidth + x;
                var hole = sourceX >= region.X - marginX && sourceX < region.X + region.Width + marginX &&
                    sourceY >= region.Y - marginY && sourceY < region.Y + region.Height + marginY ? 1f : 0f;
                for (var t = 0; t < 5; t++)
                {
                    masks[t * Plane + pixel] = hole;
                    for (var c = 0; c < 3; c++)
                        frames[(t * 3 + c) * Plane + pixel] = SampleRgb(originalFrames[t], width, height, sourceX, sourceY, c) * (2f / 255) - 1;
                }
            }
        float[] result;
        try { result = Infer(frames, masks); }
        catch (Exception ex) when (IsProviderFailure(ex) && Provider.StartsWith("DirectML", StringComparison.Ordinal))
        { Fallback(ex); result = Infer(frames, masks); }
        if (_profilePending)
        {
            var profile = _session!.EndProfiling(); _profilePending = false;
            using var stream = new FileStream(profile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var data = JsonDocument.Parse(stream);
            var providers = data.RootElement.EnumerateArray().Where(e => e.TryGetProperty("args", out var a) && a.TryGetProperty("provider", out _))
                .Select(e => e.GetProperty("args").GetProperty("provider").GetString() ?? "").Distinct().ToArray();
            if (!providers.Any(p => p.Contains("Dml", StringComparison.OrdinalIgnoreCase)))
            {
                Fallback(new InvalidOperationException("本次深度推理未使用 DirectML 运算节点。"));
                result = Infer(frames, masks);
            }
            else Provider = providers.Any(p => p.Contains("CPU", StringComparison.OrdinalIgnoreCase)) ? "DirectML + CPU" : "DirectML";
            _notify(new("provider", $"深度 AI 实际计算设备：{Provider}", Provider: Provider, UsedFallback: UsedFallback, FallbackReason: FallbackReason));
        }
        for (var y = region.Y; y < region.Y + region.Height; y++)
            for (var x = region.X; x < region.X + region.Width; x++)
            {
                var modelX = (x - left + .5) * ModelWidth / cropWidth - .5;
                var modelY = (y - top + .5) * ModelHeight / cropHeight - .5;
                // A small transition lives entirely inside the chosen region. Pixels outside it are never written.
                var edge = Math.Min(Math.Min(region.X == 0 ? 3 : x - region.X + 1, region.Y == 0 ? 3 : y - region.Y + 1),
                    Math.Min(region.X + region.Width == width ? 3 : region.X + region.Width - x, region.Y + region.Height == height ? 3 : region.Y + region.Height - y));
                var alpha = Math.Min(1f, edge / 3f);
                var target = (y * width + x) * 3;
                for (var c = 0; c < 3; c++)
                {
                    var value = (SamplePlane(result, modelX, modelY, c) + 1) * 127.5f;
                    if (!float.IsFinite(value)) throw new InvalidDataException("深度 AI 返回了无效画面。");
                    outputFrame[target + c] = (byte)Math.Clamp(MathF.Round(outputFrame[target + c] * (1 - alpha) + value * alpha), 0, 255);
                }
            }
    }

    private float[] Infer(float[] frames, float[] masks)
    {
        var inputs = new[] { NamedOnnxValue.CreateFromTensor("frames", new DenseTensor<float>(frames, [1, 5, 3, ModelHeight, ModelWidth])),
            NamedOnnxValue.CreateFromTensor("masks", new DenseTensor<float>(masks, [1, 5, 1, ModelHeight, ModelWidth])) };
        using var results = _session!.Run(inputs);
        var output = results.First().AsTensor<float>();
        if (!output.Dimensions.SequenceEqual(new[] { 1, 3, ModelHeight, ModelWidth })) throw new InvalidDataException("深度 AI 输出尺寸不正确。");
        return output.ToArray();
    }
    private static float SampleRgb(byte[] image, int width, int height, double x, double y, int channel)
    {
        x = Math.Clamp(x, 0, width - 1); y = Math.Clamp(y, 0, height - 1);
        var x0 = (int)x; var y0 = (int)y; var x1 = Math.Min(width - 1, x0 + 1); var y1 = Math.Min(height - 1, y0 + 1);
        var dx = (float)(x - x0); var dy = (float)(y - y0);
        var top = image[(y0 * width + x0) * 3 + channel] * (1 - dx) + image[(y0 * width + x1) * 3 + channel] * dx;
        var bottom = image[(y1 * width + x0) * 3 + channel] * (1 - dx) + image[(y1 * width + x1) * 3 + channel] * dx;
        return top * (1 - dy) + bottom * dy;
    }
    private static float SamplePlane(float[] image, double x, double y, int channel)
    {
        x = Math.Clamp(x, 0, ModelWidth - 1); y = Math.Clamp(y, 0, ModelHeight - 1);
        var x0 = (int)x; var y0 = (int)y; var x1 = Math.Min(ModelWidth - 1, x0 + 1); var y1 = Math.Min(ModelHeight - 1, y0 + 1);
        var dx = (float)(x - x0); var dy = (float)(y - y0); var offset = channel * Plane;
        var top = image[offset + y0 * ModelWidth + x0] * (1 - dx) + image[offset + y0 * ModelWidth + x1] * dx;
        var bottom = image[offset + y1 * ModelWidth + x0] * (1 - dx) + image[offset + y1 * ModelWidth + x1] * dx;
        return top * (1 - dy) + bottom * dy;
    }
    public void Dispose() => _session?.Dispose();
}
