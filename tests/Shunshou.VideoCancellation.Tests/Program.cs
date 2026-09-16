using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Shunshou.Core;

// This executable doubles as a controlled runner/child. No AI model is loaded and no inference runs.
if (args.FirstOrDefault() == "--child") { Thread.Sleep(TimeSpan.FromMinutes(2)); return 0; }
if (args.FirstOrDefault() == "--job")
{
    var childInfo = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Shunshou.AiRunner.exe")) { UseShellExecute = false, CreateNoWindow = true };
    childInfo.ArgumentList.Add("--child");
    using var child = Process.Start(childInfo)!;
    var statePath = Environment.GetEnvironmentVariable("SHUNSHOU_CANCEL_TEST_STATE")!;
    File.WriteAllText(statePath + ".writing", JsonSerializer.Serialize(new ProcessIds(Environment.ProcessId, child.Id)));
    File.Move(statePath + ".writing", statePath, false);
    Thread.Sleep(TimeSpan.FromMinutes(2));
    return 0;
}

var root = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine("artifacts", "video-cancellation", DateTime.Now.ToString("yyyyMMdd-HHmmss")));
Directory.CreateDirectory(root);
var fakeRunner = Path.Combine(root, "fake-runner");
Directory.CreateDirectory(fakeRunner);
foreach (var suffix in new[] { ".exe", ".dll", ".deps.json", ".runtimeconfig.json" })
    File.Copy(Path.Combine(AppContext.BaseDirectory, "Shunshou.AiRunner" + suffix), Path.Combine(fakeRunner, "Shunshou.AiRunner" + suffix), true);
var input = Path.Combine(root, "input.mp4"); var model = Path.Combine(root, "model.onnx");
File.WriteAllText(input, "fixture is not decoded"); File.WriteAllText(model, "fixture is not loaded");
var stateFile = Path.Combine(root, "process-ids.json");
Environment.SetEnvironmentVariable("SHUNSHOU_CANCEL_TEST_STATE", stateFile);
var dotnetRoot = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()).Parent?.Parent?.Parent?.FullName;
if (dotnetRoot is not null && File.Exists(Path.Combine(dotnetRoot, "dotnet.exe")))
    Environment.SetEnvironmentVariable("DOTNET_ROOT", dotnetRoot);
var previousContext = SynchronizationContext.Current;
var context = new PausableContext();
ProcessIds? processes = null;
using var cancellation = new CancellationTokenSource();
try
{
    SynchronizationContext.SetSynchronizationContext(context);
    var service = new VideoAiService(fakeRunner, Path.GetFullPath("runtime/ffmpeg/bin"), model);
    var processing = service.ExportAsync(input, root, new(1, 1, 2, 2), VideoAiProvider.Cpu, null, cancellation.Token);
    PumpUntil(() => File.Exists(stateFile) || processing.IsCompleted, TimeSpan.FromSeconds(10));
    if (processing.IsCompleted) processing.GetAwaiter().GetResult();
    processes = JsonSerializer.Deserialize<ProcessIds>(File.ReadAllText(stateFile))!;
    if (Exited(processes.Parent) || Exited(processes.Child)) throw new InvalidOperationException("Fixture processes did not remain running.");

    // Freeze the simulated UI dispatcher. Cancellation happens on a different thread and must not need a UI continuation.
    var watch = Stopwatch.StartNew();
    Task.Run(() => cancellation.Cancel()).GetAwaiter().GetResult();
    while ((!Exited(processes.Parent) || !Exited(processes.Child)) && watch.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(10);
    var parentExited = Exited(processes.Parent); var childExited = Exited(processes.Child);
    var completedBeforeUiResumed = processing.IsCompleted;
    if (!parentExited || !childExited) throw new InvalidOperationException("Cancellation left a process running while the UI context was paused.");
    if (completedBeforeUiResumed) throw new InvalidOperationException("The fixture did not isolate process termination from the UI continuation.");

    PumpUntil(() => processing.IsCompleted, TimeSpan.FromSeconds(5));
    try { processing.GetAwaiter().GetResult(); throw new InvalidOperationException("Expected cancellation."); }
    catch (OperationCanceledException) { }
    var report = new { passed = true, parentExited, childExited, completedBeforeUiResumed, elapsedMs = watch.ElapsedMilliseconds,
        checks = new[] { "UI synchronization context paused", "real Core wrapper starts runner and child", "thread-pool cancellation terminates both processes without UI continuation", "resumed continuation completes cancellation cleanup", "no AI inference performed" } };
    File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(report));
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = false, error = ex.ToString() }));
    return 1;
}
finally
{
    SynchronizationContext.SetSynchronizationContext(previousContext);
    if (processes is not null) { Stop(processes.Child); Stop(processes.Parent); }
}

void PumpUntil(Func<bool> complete, TimeSpan timeout)
{
    var watch = Stopwatch.StartNew();
    while (!complete() && watch.Elapsed < timeout) { context.PumpOne(); Thread.Sleep(5); }
    if (!complete()) throw new TimeoutException("The controlled cancellation fixture timed out.");
}
static bool Exited(int id)
{
    try { using var process = Process.GetProcessById(id); return process.HasExited; }
    catch (ArgumentException) { return true; }
}
static void Stop(int id)
{
    try { using var process = Process.GetProcessById(id); if (!process.HasExited) process.Kill(true); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
}
sealed record ProcessIds(int Parent, int Child);
sealed class PausableContext : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback Action, object? State)> _queue = new();
    public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));
    public void PumpOne() { if (_queue.TryDequeue(out var work)) work.Action(work.State); }
}
