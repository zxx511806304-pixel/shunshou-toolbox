using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Shunshou.Core;

public enum VideoAiProvider { Auto, Cpu, DirectML }
public enum VideoAiModel { Migan, Sttn }
public sealed record VideoAiResult(string OutputPath, string Provider, bool UsedFallback, string? FallbackReason);

/// <summary>Runs optional MI-GAN inference in a separate process so its DirectML runtime never collides with OCR's runtime.</summary>
public sealed class VideoAiService(string? runnerDirectory = null, string? engineDirectory = null, string? modelPath = null, VideoAiModel model = VideoAiModel.Migan)
{
    private static readonly string TemporaryRoot = Path.Combine(Path.GetTempPath(), "Shunshou", "ai-video-jobs");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public bool IsAvailable
    {
        get { try { ResolveAi(); return true; } catch (IOException) { return false; } catch (ArgumentException) { return false; } }
    }

    public Task<VideoAiResult> PreviewAsync(string input, string outputDir, VideoRegion region, VideoAiProvider provider,
        IProgress<ToolProgress>? progress, CancellationToken ct) => ProcessAsync(input, outputDir, region, provider, true, progress, ct);
    public Task<VideoAiResult> ExportAsync(string input, string outputDir, VideoRegion region, VideoAiProvider provider,
        IProgress<ToolProgress>? progress, CancellationToken ct) => ProcessAsync(input, outputDir, region, provider, false, progress, ct);

    private async Task<VideoAiResult> ProcessAsync(string input, string outputDir, VideoRegion region, VideoAiProvider provider,
        bool preview, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(provider)) throw new ArgumentOutOfRangeException(nameof(provider));
        if (!Enum.IsDefined(model)) throw new ArgumentOutOfRangeException(nameof(model));
        ArgumentNullException.ThrowIfNull(region);
        if (string.IsNullOrWhiteSpace(input) || Uri.TryCreate(input, UriKind.Absolute, out var uri) && !uri.IsFile)
            throw new ArgumentException("请选择本地视频文件。");
        input = Path.GetFullPath(input);
        if (!File.Exists(input)) throw new FileNotFoundException("找不到视频文件。", input);
        if (File.GetAttributes(input).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("请先将视频保存为本地普通文件。");
        if (region.X < 0 || region.Y < 0 || region.Width < 2 || region.Height < 2) throw new ArgumentException("请选择至少 2 × 2 像素的有效区域。");
        var (runner, resolvedModel) = ResolveAi();
        var engine = ResolveEngine();
        var job = Path.Combine(TemporaryRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        try
        {
            var candidate = Path.Combine(job, "result.mp4");
            var jobPath = Path.Combine(job, "job.json");
            await File.WriteAllTextAsync(jobPath, JsonSerializer.Serialize(new
            {
                input, output = candidate, modelPath = resolvedModel, engineDirectory = engine, region, provider = provider.ToString(),
                previewSeconds = preview ? (double?)3 : null, modelKind = model == VideoAiModel.Sttn ? "sttn" : "migan"
            }, Json), ct);
            var info = new ProcessStartInfo(runner)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = job
            };
            info.ArgumentList.Add("--job"); info.ArgumentList.Add(jobPath);
            progress?.Report(new(1, model == VideoAiModel.Sttn ? "正在准备深度 AI 修补" : "正在准备轻量 AI 修补"));
            using var process = new Process { StartInfo = info };
            if (!process.Start()) throw new InvalidOperationException("无法启动本地 AI 组件。");
            using var cancellationRegistration = ct.Register(static state =>
            {
                var active = (Process)state!;
                try { if (!active.HasExited) active.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }, process, useSynchronizationContext: false);
            string? error = null;
            VideoAiResult? result = null;
            var outputTask = ReadOutput();
            var errorTask = DrainErrors(process.StandardError);
            try { await process.WaitForExitAsync(ct); await outputTask; }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                await process.WaitForExitAsync(CancellationToken.None);
                await outputTask; await errorTask;
                throw;
            }
            ct.ThrowIfCancellationRequested();
            var diagnostic = await errorTask;
            if (process.ExitCode != 0 || result is null || !File.Exists(candidate) || new FileInfo(candidate).Length == 0)
                throw new InvalidDataException(error ?? ("AI 修补未完成。" + (string.IsNullOrWhiteSpace(diagnostic) ? "请检查 AI 组件是否完整。" : diagnostic)));
            var path = await Publish(candidate, outputDir, input, preview, ct);
            progress?.Report(new(100, $"{(model == VideoAiModel.Sttn ? "深度" : "轻量")} AI 已完成 · {result.Provider}" + (result.UsedFallback ? " · 已回退 CPU" : "")));
            return result with { OutputPath = path };

            async Task ReadOutput()
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
                {
                    if (line.Length > 32_768) continue;
                    RunnerEvent? message;
                    try { message = JsonSerializer.Deserialize<RunnerEvent>(line, Json); }
                    catch (JsonException) { continue; }
                    if (message is null) continue;
                    if (message.Type == "error") error = message.Message;
                    else if (message.Type == "complete" && string.Equals(message.OutputPath, candidate, StringComparison.OrdinalIgnoreCase) && message.Provider is not null)
                        result = new(candidate, message.Provider, message.UsedFallback, message.FallbackReason);
                    else if (message.Type is "progress" or "provider")
                        progress?.Report(new(Math.Clamp(message.Percent, 0, 99), message.Message));
                }
            }
        }
        finally
        {
            var resolved = Path.GetFullPath(job);
            if (Path.GetDirectoryName(resolved) == Path.GetFullPath(TemporaryRoot) && Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
                try { if (Directory.Exists(resolved)) Directory.Delete(resolved, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record RunnerEvent(string Type, string Message, double Percent = 0, string? Provider = null,
        bool UsedFallback = false, string? FallbackReason = null, string? OutputPath = null);

    private (string Runner, string Model) ResolveAi()
    {
        var configured = runnerDirectory ?? Environment.GetEnvironmentVariable("SHUNSHOU_AI_RUNNER_DIR");
        var modelFilename = model == VideoAiModel.Sttn ? "sttn.onnx" : "model.onnx";
        if (configured is not null)
        {
            var resolvedModel = modelPath ?? Environment.GetEnvironmentVariable("SHUNSHOU_AI_MODEL") ?? Path.Combine(configured, "..", modelFilename);
            return Check(configured, resolvedModel);
        }
        var candidates = new List<string>
        {
            Path.Combine(AppPaths.DataDirectory, "ai"),
            Path.Combine(AppPaths.DataDirectory, "ai", "migan"),
            Path.Combine(AppPaths.DataDirectory, "ai", "sttn"),
            Path.Combine(AppContext.BaseDirectory, "tools", "ai-inpaint")
        };
        for (var parent = new DirectoryInfo(AppContext.BaseDirectory); parent is not null; parent = parent.Parent)
            candidates.Add(Path.Combine(parent.FullName, "runtime", "ai-inpaint"));
        foreach (var directory in candidates)
        {
            var resolvedModel = modelPath ?? Environment.GetEnvironmentVariable("SHUNSHOU_AI_MODEL") ?? Path.Combine(directory, modelFilename);
            if (File.Exists(Path.Combine(directory, "runner", "Shunshou.AiRunner.exe")) && File.Exists(resolvedModel)) return Check(Path.Combine(directory, "runner"), resolvedModel);
        }
        throw new FileNotFoundException($"缺少{(model == VideoAiModel.Sttn ? "深度" : "轻量")} AI 组件，请重新解压完整软件包。");
        static (string, string) Check(string directory, string model)
        {
            var runner = Path.GetFullPath(Path.Combine(directory, "Shunshou.AiRunner.exe")); model = Path.GetFullPath(model);
            if (!File.Exists(runner) || !File.Exists(model)) throw new FileNotFoundException("AI 组件不完整，请重新解压完整软件包。");
            return (runner, model);
        }
    }

    private string ResolveEngine()
    {
        var configured = engineDirectory ?? Environment.GetEnvironmentVariable("SHUNSHOU_FFMPEG_DIR");
        var directory = configured ?? Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "bin");
        if (configured is null && !File.Exists(Path.Combine(directory, "ffmpeg.exe")))
            for (var parent = new DirectoryInfo(AppContext.BaseDirectory); parent is not null; parent = parent.Parent)
            {
                var development = Path.Combine(parent.FullName, "runtime", "ffmpeg", "bin");
                if (!File.Exists(Path.Combine(development, "ffmpeg.exe"))) continue;
                directory = development; break;
            }
        directory = Path.GetFullPath(directory);
        if (!File.Exists(Path.Combine(directory, "ffmpeg.exe")) || !File.Exists(Path.Combine(directory, "ffprobe.exe")))
            throw new FileNotFoundException("缺少本地音视频引擎。");
        return directory;
    }

    private static async Task<string> DrainErrors(StreamReader reader)
    {
        var output = new StringBuilder(); string? line;
        while ((line = await reader.ReadLineAsync()) is not null) if (output.Length < 3000) output.AppendLine(line);
        return output.ToString();
    }

    private static async Task<string> Publish(string candidate, string directory, string input, bool preview, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        var stem = Path.GetFileNameWithoutExtension(input); if (stem.Length > 70) stem = stem[..70];
        var output = Path.Combine(directory, $"{stem}_{(preview ? "ai-preview" : "ai-edited")}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}.mp4");
        var partial = output + ".partial";
        try
        {
            await using (var source = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
            await using (var destination = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                await source.CopyToAsync(destination, ct);
            ct.ThrowIfCancellationRequested(); File.Move(partial, output, false); return output;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
}
