using System.Runtime.InteropServices;
using Shunshou.App;
using static Shunshou.App.RecordingNative;

var results = new List<object>();
var process = System.Diagnostics.Process.GetCurrentProcess();
static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static nint Packed(int x, int y) => (nint)((uint)(ushort)x | ((uint)(ushort)y << 16));
static async Task<nint> FindSelection()
{
    var deadline = DateTime.UtcNow.AddSeconds(10);
    do
    {
        nint selected = 0;
        TestNative.EnumWindows((hwnd, _) =>
        {
            TestNative.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != Environment.ProcessId) return true;
            var name = new System.Text.StringBuilder(256);
            TestNative.GetClassName(hwnd, name, name.Capacity);
            if (name.ToString().StartsWith("ShunshouRecordingRegion_")) selected = hwnd;
            return true;
        }, 0);
        if (selected != 0) return selected;
        await Task.Delay(20);
    } while (DateTime.UtcNow < deadline);
    throw new Exception("Selection window did not appear");
}

// These are messages to this test process only: no cursor or user input is injected globally.
foreach (var spec in new[] { ("normal", 20, 30, 420, 330, 20, 30, 400, 300), ("reverse", 500, 400, 100, 80, 100, 80, 400, 320), ("clamped", -20, -10, 900, 600, 0, 0, 640, 480) })
{
    var task = ScreenRecordingPicker.SelectAsync(0, -100, -80, 640, 480);
    var window = await FindSelection();
    Assert(TestNative.AreDpiAwarenessContextsEqual(TestNative.GetWindowDpiAwarenessContext(window), -4), "selector is not per-monitor DPI aware v2");
    var previousDpi = SetThreadDpiAwarenessContext(-4);
    TestNative.GetWindowRect(window, out var bounds);
    SetThreadDpiAwarenessContext(previousDpi);
    Assert(bounds.Left == -100 && bounds.Top == -80 && bounds.Width == 640 && bounds.Height == 480, "physical origin/size changed");
    PostMessage(window, 0x0201, 1, Packed(spec.Item2, spec.Item3));
    PostMessage(window, 0x0200, 1, Packed(spec.Item4, spec.Item5));
    PostMessage(window, 0x0202, 0, Packed(spec.Item4, spec.Item5));
    var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
    Assert(result == (spec.Item6, spec.Item7, spec.Item8, spec.Item9), $"Incorrect {spec.Item1}: {result}");
    Assert(!IsWindow(window), "selection window leaked");
    results.Add(new { Test = spec.Item1, Passed = true, Result = result.ToString(), Bounds = $"{bounds.Left},{bounds.Top},{bounds.Width},{bounds.Height}" });
}
{
    var task = ScreenRecordingPicker.SelectAsync(0, 0, 0, 640, 480);
    var window = await FindSelection();
    var concurrentRejected = false;
    try { _ = ScreenRecordingPicker.SelectAsync(0, 0, 0, 640, 480); }
    catch (InvalidOperationException) { concurrentRejected = true; }
    Assert(concurrentRejected, "two selectors allowed concurrently");
    PostMessage(window, 0x0201, 1, Packed(20, 20));
    PostMessage(window, 0x0202, 0, Packed(30, 30));
    await Task.Delay(100);
    Assert(!task.IsCompleted, "undersized region accepted");
    PostMessage(window, 0x0100, 0x1b, 0);
    Assert(await task.WaitAsync(TimeSpan.FromSeconds(10)) is null, "Escape failed");
    results.Add(new { Test = "undersized retry and Escape", Passed = true });
}
foreach (var message in new uint[] { 0x0204, 0x0006 })
{
    var task = ScreenRecordingPicker.SelectAsync(0, 0, 0, 640, 480);
    var window = await FindSelection();
    PostMessage(window, message, 0, 0);
    Assert(await task.WaitAsync(TimeSpan.FromSeconds(10)) is null, "cancellation failed");
    results.Add(new { Test = $"cancel {message:x}", Passed = true });
}
{
    Assert(RecordingHotKeys.TryCreate(out var keys, out var error), error ?? "shortcut registration failed");
    using (keys)
    {
        var paused = new TaskCompletionSource();
        var stopped = new TaskCompletionSource();
        keys!.TogglePauseRequested += (_, _) => paused.TrySetResult();
        keys.StopRequested += (_, _) => stopped.TrySetResult();
        var hwnd = (nint)typeof(RecordingHotKeys).GetField("_window", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(keys)!;
        PostMessage(hwnd, 0x0312, 0x5179, 0);
        PostMessage(hwnd, 0x0312, 0x517a, 0);
        await Task.WhenAll(paused.Task, stopped.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert(!RecordingHotKeys.TryCreate(out var duplicate, out var conflict) && duplicate is null && conflict!.Contains("F9"), "shortcut conflict not reported");
        results.Add(new { Test = "shortcuts callbacks and conflict", Passed = true, Conflict = conflict });
    }
    Assert(RecordingHotKeys.TryCreate(out var recreated, out error), error ?? "shortcuts leaked after dispose");
    recreated!.Dispose();
    results.Add(new { Test = "shortcuts dispose/re-register", Passed = true });
}
{
    // Reserve F10 on this thread, so the candidate must undo its successful F9 registration.
    Assert(RegisterHotKey(0, 0x5321, 0x4003, 0x79), "cannot reserve F10 for the partial registration test");
    try
    {
        Assert(!RecordingHotKeys.TryCreate(out var candidate, out var error) && candidate is null && error!.Contains("F10"), "second shortcut conflict not reported");
        Assert(RegisterHotKey(0, 0x5322, 0x4003, 0x78), "F9 leaked after F10 conflict");
        UnregisterHotKey(0, 0x5322);
        results.Add(new { Test = "F10 conflict rolls back F9 registration", Passed = true });
    }
    finally { UnregisterHotKey(0, 0x5321); }
}
// Baseline after initial font/GDI setup; its process-wide caches are not window leaks.
var gdiBefore = TestNative.GetGuiResources(process.Handle, 0);
for (var iteration = 0; iteration < 10; iteration++)
{
    var task = ScreenRecordingPicker.SelectAsync(0, 0, 0, 640, 480);
    var window = await FindSelection();
    PostMessage(window, 0x0100, 0x1b, 0);
    Assert(await task.WaitAsync(TimeSpan.FromSeconds(10)) is null, "repeat cancellation failed");
}
var gdiAfter = TestNative.GetGuiResources(process.Handle, 0);
Assert(gdiAfter <= gdiBefore + 2, $"GDI resources leaked: {gdiBefore} -> {gdiAfter}");
results.Add(new { Test = "GDI resources after repeated selection", Passed = true, Before = gdiBefore, After = gdiAfter });
var json = System.Text.Json.JsonSerializer.Serialize(new { Passed = true, Results = results }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "native-results.json"), json);
Console.WriteLine(json);

internal static class TestNative
{
    internal delegate bool EnumProc(nint hwnd, nint parameter);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, nint parameter);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out int processId);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] internal static extern int GetClassName(nint hwnd, System.Text.StringBuilder buffer, int maxCount);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd, out Rect rectangle);
    [DllImport("user32.dll")] internal static extern nint GetWindowDpiAwarenessContext(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);
    [DllImport("user32.dll")] internal static extern uint GetGuiResources(nint process, uint flags);
}
