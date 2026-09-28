using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Shunshou.Core;

namespace Shunshou.ScreenRecording.Tests;

internal static class Program
{
    private static readonly List<object> results = [];
    private static readonly List<string> uncovered = [
        "Real microphone capture is deliberately not exercised.",
        "Multi-monitor/DPI combinations, first-time permissions, protected content, long recordings, and all GPU/driver combinations are not covered."];

    [STAThread]
    private static int Main(string[] args)
    {
        var capture = args.Contains("--capture");
        var positional = args.Where(a => !a.StartsWith("--")).ToArray();
        var root = Path.GetFullPath(positional.ElementAtOrDefault(0) ?? "artifacts/screen-recording-tests");
        root = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        var ffmpeg = Path.GetFullPath(positional.ElementAtOrDefault(1)
            ?? "dist/ShunshouToolbox-1.0.1-win-x64/app/tools/ffmpeg/bin");
        Directory.CreateDirectory(root);
        Console.WriteLine($"Focused recording artifacts: {root}");
        Exception? failure = null;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (capture)
        {
            using var fixture = new FixtureWindow();
            fixture.Shown += async (_, _) =>
            {
                try
                {
                    if (args.Contains("--interruption")) await RunInterruptionAsync(root, ffmpeg, fixture);
                    else await RunAsync(root, ffmpeg, fixture, args.Contains("--audio"), args.Contains("--supplement"));
                }
                catch (Exception ex) { failure = ex; Console.Error.WriteLine(ex); }
                finally { fixture.Close(); }
            };
            Application.Run(fixture);
        }
        else
        {
            try { RunAsync(root, ffmpeg, null, false, false).GetAwaiter().GetResult(); }
            catch (Exception ex) { failure = ex; Console.Error.WriteLine(ex); }
        }
        File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new
        {
            Passed = failure is null, CaptureExecuted = capture, MicrophoneCaptured = false,
            CapturedContent = !capture ? "None" : args.Contains("--interruption")
                ? "Generated animation window only; simulated monitoring failure with all audio capture disabled."
                : "Generated animation window, plus a production display crop fully inside that window.",
            Results = results, Uncovered = uncovered, Error = failure?.ToString()
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(failure is null ? "ALL FOCUSED RECORDING CHECKS PASSED" : "FOCUSED RECORDING CHECK FAILED");
        return failure is null ? 0 : 1;
    }

    private static async Task RunAsync(string root, string ffmpeg, FixtureWindow? fixture, bool audio, bool supplement)
    {
        await using var recorder = new ScreenRecordingService();
        var display = recorder.GetDisplays().FirstOrDefault(d => d.IsPrimary)
            ?? throw new InvalidOperationException("An interactive primary display is required.");
        var options = new ScreenRecordingOptions
        {
            DisplayDeviceName = display.DeviceName, OutputDirectory = Path.Combine(root, "recordings"),
            CaptureSystemAudio = false, CaptureMicrophone = false, ShowCursor = false,
            PreferHardwareEncoding = false, FrameRate = 30
        };
        var plan = recorder.CreatePlan(options with { Region = new(0, 0, 640, 360), VideoBitrateMbps = 4 });
        Check(plan.OutputWidth == 640 && plan.OutputHeight == 360 && plan.VideoBitrate == 4_000_000 && plan.FrameRate == 30,
            "physical crop and manual bitrate plan");
        var odd = recorder.CreatePlan(options with { Region = new(0, 0, 641, 361) });
        Check(odd.OutputWidth % 2 == 0 && odd.OutputHeight % 2 == 0 && odd.OutputWidth <= 641 && odd.OutputHeight <= 361,
            "odd crop dimensions become encoder-safe even dimensions");
        ExpectFailure(() => recorder.CreatePlan(options with { FrameRate = 0 }), "zero FPS");
        ExpectFailure(() => recorder.CreatePlan(options with { FrameRate = 121 }), "FPS above range");
        ExpectFailure(() => recorder.CreatePlan(options with { VideoBitrateMbps = double.NaN }), "NaN bitrate");
        ExpectFailure(() => recorder.CreatePlan(options with { VideoBitrateMbps = 0 }), "zero bitrate");
        ExpectFailure(() => recorder.CreatePlan(options with { MaxOutputHeight = 0 }), "zero output height");
        ExpectFailure(() => recorder.CreatePlan(options with { AudioBitrateKbps = 999 }), "unsupported AAC bitrate");
        ExpectFailure(() => recorder.CreatePlan(options with { Region = new(-1, 0, 320, 240) }), "negative crop origin");
        ExpectFailure(() => recorder.CreatePlan(options with { Region = new(0, 0, 0, 240) }), "empty crop");
        ExpectFailure(() => recorder.CreatePlan(options with { Region = new(display.Bounds.Width - 5, 0, 320, 240) }), "crop outside selected display");
        ExpectFailure(() => recorder.CreatePlan(options with { DisplayDeviceName = "__synthetic_missing_display__" }), "missing display");
        ExpectFailure(() => recorder.CreatePlan(options with { OutputDirectory = "" }), "empty output directory");
        results.Add(new { Name = "validated-options", Plan = plan, OddPlan = odd, InvalidCases = 11,
            AcceptedFps = new[] { 1, 15, 30, 60, 120 }.Select(f => recorder.CreatePlan(options with { FrameRate = f }).FrameRate).ToArray() });

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await recorder.StartAsync(options, cancelled.Token); throw new InvalidOperationException("A pre-cancelled start was accepted."); }
            catch (OperationCanceledException) { }
            Check(!Directory.Exists(options.OutputDirectory) || !Directory.EnumerateFiles(options.OutputDirectory).Any(),
                "pre-cancelled start creates no output file");
            results.Add(new { Name = "pre-cancelled-start", Passed = true });
        }
        if (fixture is null) { uncovered.Add("Native capture is opt-in and was not run in this invocation."); return; }
        Check(File.Exists(Path.Combine(ffmpeg, "ffmpeg.exe")) && File.Exists(Path.Combine(ffmpeg, "ffprobe.exe")), "FFmpeg tools exist");
        await Task.Delay(600);

        var windowOptions = options with { Region = new(0, 0, fixture.Width, fixture.Height) };
        if (supplement)
        {
            await RunExtrasAsync(recorder, options, windowOptions, fixture, ffmpeg, root, audio);
            return;
        }
        var states = new List<ScreenRecordingState>();
        recorder.StateChanged += (_, status) => { lock (states) states.Add(status.State); };
        var elapsed = Stopwatch.StartNew();
        await recorder.StartWindowAsync(windowOptions, fixture.Handle);
        var activePlan = recorder.ActivePlan ?? throw new InvalidOperationException("No active plan.");
        Check(recorder.State == ScreenRecordingState.Recording, "started recorder state");
        await Task.Delay(1900);
        recorder.Pause();
        Check(recorder.State == ScreenRecordingState.Paused, "paused recorder state");
        var pausedAt = recorder.Elapsed;
        await Task.Delay(2100);
        Check(Math.Abs((recorder.Elapsed - pausedAt).TotalMilliseconds) < 200, "elapsed timer freezes while paused");
        recorder.Resume();
        Check(recorder.State == ScreenRecordingState.Recording, "resumed recorder state");
        await Task.Delay(1900);
        var output = await recorder.StopAsync();
        elapsed.Stop();
        Check(recorder.State == ScreenRecordingState.Completed && File.Exists(output), "stop finalizes a real file");
        var baseline = await VideoInspection.InspectAsync(ffmpeg, output, Path.Combine(root, "pause-resume"));
        CheckVideo(baseline, activePlan, 3.1, 5.2);
        Check(baseline.AudioStreams == 0, "silent mode has no audio track");
        Check(elapsed.Elapsed.TotalSeconds - baseline.Duration > 1.6, "paused time is excluded from MP4 duration");
        var outputHash = SHA256.HashData(await File.ReadAllBytesAsync(output));
        results.Add(new { Name = "window-pause-resume", Video = baseline, ActivePlan = activePlan,
            WallSeconds = elapsed.Elapsed.TotalSeconds, States = states.Select(s => s.ToString()).ToArray(), Output = output });

        var manual = windowOptions with { FrameRate = 15, VideoBitrateMbps = 4, MaxOutputHeight = 720,
            PreferHardwareEncoding = true };
        await recorder.StartWindowAsync(manual, fixture.Handle);
        var manualPlan = recorder.ActivePlan!;
        await Task.Delay(2300);
        var output2 = await recorder.StopAsync();
        var manualVideo = await VideoInspection.InspectAsync(ffmpeg, output2, Path.Combine(root, "manual-720p"));
        CheckVideo(manualVideo, manualPlan, 1.7, 3.8);
        Check(manualVideo.Height <= 720, "720p cap is applied");
        Check(manualVideo.Bitrate > 10_000 && manualVideo.Bitrate < manualPlan.VideoBitrate * 2.5,
            "actual VBR bitrate is nonzero and within a reasonable ceiling");
        var retainedHash = SHA256.HashData(await File.ReadAllBytesAsync(output));
        Check(!string.Equals(output, output2, StringComparison.OrdinalIgnoreCase)
            && outputHash.SequenceEqual(retainedHash), "later recording does not overwrite earlier output");
        results.Add(new { Name = "manual-bitrate-fps-size-no-overwrite", Video = manualVideo, ActivePlan = manualPlan, Output = output2 });

        // Exercise the exact production Display + Region route. The crop is fully inside our opaque topmost fixture.
        fixture.BringToFront();
        fixture.Activate();
        await Task.Delay(300);
        var location = fixture.PointToScreen(Point.Empty);
        var crop = new ScreenRecordingBounds(location.X - display.Bounds.X + 24,
            location.Y - display.Bounds.Y + 140, 640, 360);
        Check(fixture.Width >= 664 && fixture.Height >= 500, "production crop stays inside synthetic window");
        await recorder.StartAsync(options with { Region = crop, FrameRate = 30 });
        var cropPlan = recorder.ActivePlan!;
        await Task.Delay(2300);
        var output3 = await recorder.StopAsync();
        var cropped = await VideoInspection.InspectAsync(ffmpeg, output3, Path.Combine(root, "production-display-crop"));
        CheckVideo(cropped, cropPlan, 1.7, 3.8);
        Check(cropped.Width == 640 && cropped.Height == 360 && cropped.AudioStreams == 0, "production crop geometry and silent audio");
        results.Add(new { Name = "production-display-crop", Video = cropped, ActivePlan = cropPlan, Output = output3 });
        await RunExtrasAsync(recorder, options, manual, fixture, ffmpeg, root, audio);
    }

    private static async Task RunExtrasAsync(ScreenRecordingService recorder, ScreenRecordingOptions options,
        ScreenRecordingOptions windowOptions, FixtureWindow fixture, string ffmpeg, string root, bool audio)
    {
        var filesBeforeCancel = Directory.Exists(options.OutputDirectory)
            ? Directory.EnumerateFiles(options.OutputDirectory).ToArray() : [];
        var cancelledDuringStart = false;
        using (var startupCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            try { await recorder.StartWindowAsync(windowOptions, fixture.Handle, startupCancellation.Token); }
            catch (OperationCanceledException) { cancelledDuringStart = true; }
        }
        if (!cancelledDuringStart)
        {
            // Fast machines may complete startup before the timer fires; stop cleanly and report that boundary.
            await recorder.StopAsync();
            uncovered.Add("The 50 ms cancellation probe completed startup first, so in-progress startup cancellation was not hit on this host.");
        }
        else
        {
            Check(!Directory.Exists(options.OutputDirectory) || Directory.EnumerateFiles(options.OutputDirectory).Count() == filesBeforeCancel.Length,
                "cancelled startup does not publish a partial MP4");
        }
        Check(!Directory.Exists(options.OutputDirectory) || !Directory.EnumerateDirectories(options.OutputDirectory, ".recording-*").Any(),
            "cancelled startup releases staging files");
        results.Add(new { Name = "startup-cancellation", CancelledDuringStart = cancelledDuringStart, State = recorder.State.ToString() });
        var display = recorder.GetDisplays().Single(d => d.DeviceName == options.DisplayDeviceName);
        fixture.BringToFront();
        fixture.Activate();
        await Task.Delay(300);
        var location = fixture.PointToScreen(Point.Empty);
        var crop = new ScreenRecordingBounds(location.X - display.Bounds.X + 24,
            location.Y - display.Bounds.Y + 140, 640, 360);
        Check(fixture.Width >= 664 && fixture.Height >= 500, "60 FPS production crop stays inside synthetic window");
        await recorder.StartAsync(options with { Region = crop, FrameRate = 60, VideoBitrateMbps = 6 });
        var activePlan = recorder.ActivePlan!;
        await Task.Delay(1450);
        var path = await recorder.StopAsync();
        var video = await VideoInspection.InspectAsync(ffmpeg, path, Path.Combine(root, "production-60fps"));
        CheckVideo(video, activePlan, 1, 2.5);
        Check(Math.Abs(video.FrameRate - 60) < .1, "custom 60 FPS is present in actual MP4 metadata");
        results.Add(new { Name = "production-custom-60fps", Video = video, ActivePlan = activePlan, Output = path });
        if (audio) await SystemAudioCheck.RunAsync(recorder, windowOptions with { CaptureSystemAudio = true }, fixture.Handle, ffmpeg, root, results, uncovered);
        else uncovered.Add("System loopback audio was not exercised; use --audio to opt into a generated test tone.");
        Check(!Directory.EnumerateDirectories(options.OutputDirectory, ".recording-*").Any(), "successful stops remove recording staging folders");
    }

    private static void CheckVideo(VideoMetadata video, ScreenRecordingPlan plan, double minDuration, double maxDuration)
    {
        Check(video.Codec == "h264", "MP4 contains H.264 video");
        Check(video.Width == plan.OutputWidth && video.Height == plan.OutputHeight, "actual encoded dimensions match plan");
        Check(Math.Abs(video.FrameRate - plan.FrameRate) < 1, "actual encoded FPS matches selection");
        Check(video.Duration >= minDuration && video.Duration <= maxDuration, $"duration within {minDuration}..{maxDuration} seconds (actual {video.Duration})");
        Check(video.DistinctDecodedFrames >= 6, "decoded pixels change across multiple recorded frames");
    }

    private static async Task RunInterruptionAsync(string root, string ffmpeg, FixtureWindow fixture)
    {
        await using var recorder = new ScreenRecordingService();
        var state = AudioOutputState.Read();
        var defaultLevel = recorder.GetOutputAudioLevel();
        var explicitLevel = recorder.GetOutputAudioLevel(state.DeviceId);
        Check(defaultLevel.IsMuted == explicitLevel.IsMuted && Math.Abs(defaultLevel.Volume - explicitLevel.Volume) < .0001,
            "default and explicit endpoint volume APIs agree without changing settings");
        Check(defaultLevel.IsMuted == state.Muted && Math.Abs(defaultLevel.Volume - state.Volume) < .0001,
            "production volume API matches independent read-only COM measurement");
        results.Add(new { Name = "read-only-volume-api", DefaultMuted = defaultLevel.IsMuted,
            DefaultVolume = defaultLevel.Volume, ExplicitMuted = explicitLevel.IsMuted, ExplicitVolume = explicitLevel.Volume,
            UserSettingsChanged = false });
        var display = recorder.GetDisplays().First(d => d.IsPrimary);
        var options = new ScreenRecordingOptions
        {
            DisplayDeviceName = display.DeviceName, OutputDirectory = Path.Combine(root, "recordings"),
            Region = new(0, 0, 640, 360), CaptureSystemAudio = false, CaptureMicrophone = false,
            ShowCursor = false, PreferHardwareEncoding = false, FrameRate = 30
        };
        await Task.Delay(500);
        await recorder.StartWindowAsync(options, fixture.Handle);
        var interruptedPlan = recorder.ActivePlan!;
        await Task.Delay(2000);
        IOException? interruption = null;
        try { await recorder.FailAudioMonitoringForTestAsync("Generated test: simulated audio endpoint loss."); }
        catch (IOException ex) { interruption = ex; }
        Check(interruption is not null && recorder.State == ScreenRecordingState.Failed, "simulated monitoring failure remains an error state");
        Check(recorder.Completion.IsFaulted, "interrupted completion task stays faulted");
        var saved = recorder.OutputPath;
        Check(!string.IsNullOrWhiteSpace(saved) && File.Exists(saved), "normally finalized interrupted video is preserved");
        Check(interruption!.Message.Contains("已保存中断前内容", StringComparison.Ordinal)
            && interruption.Message.Contains(Path.GetFileName(saved!), StringComparison.Ordinal), "error message identifies the saved interrupted video");
        var interruptedVideo = await VideoInspection.InspectAsync(ffmpeg, saved!, Path.Combine(root, "interrupted-preserved"));
        CheckVideo(interruptedVideo, interruptedPlan, 1.5, 3.5);
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(saved!));
        results.Add(new { Name = "simulated-audio-monitoring-failure-preserves-video", Video = interruptedVideo,
            ActivePlan = interruptedPlan, Output = saved, State = recorder.State.ToString(),
            Error = interruption.Message, RealAudioDevicesChanged = false, SystemAudioCaptured = false, MicrophoneCaptured = false });

        await recorder.StartWindowAsync(options with { FrameRate = 60 }, fixture.Handle);
        var restartedPlan = recorder.ActivePlan!;
        await Task.Delay(1450);
        var restarted = await recorder.StopAsync();
        var restartedVideo = await VideoInspection.InspectAsync(ffmpeg, restarted, Path.Combine(root, "restart-after-interruption"));
        CheckVideo(restartedVideo, restartedPlan, 1, 2.5);
        Check(recorder.State == ScreenRecordingState.Completed, "normal recording succeeds after interrupted recording released resources");
        var retainedHash = SHA256.HashData(await File.ReadAllBytesAsync(saved!));
        Check(!string.Equals(saved, restarted, StringComparison.OrdinalIgnoreCase) && hash.SequenceEqual(retainedHash),
            "restart preserves the earlier interrupted file");
        Check(!Directory.EnumerateDirectories(options.OutputDirectory, ".recording-*").Any(), "interruption and restart leave no recording staging folders");
        results.Add(new { Name = "normal-restart-after-simulated-interruption", Video = restartedVideo,
            ActivePlan = restartedPlan, Output = restarted, State = recorder.State.ToString() });
        uncovered.Add("The audio interruption was simulated at the monitoring callback; no actual user device was disconnected or microphone captured.");
    }

    private static void ExpectFailure(Action action, string name)
    {
        try { action(); }
        catch (ArgumentException) { Console.WriteLine("PASS invalid option: " + name); return; }
        catch (InvalidOperationException) { Console.WriteLine("PASS invalid option: " + name); return; }
        throw new InvalidOperationException("Invalid input was accepted: " + name);
    }

    internal static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
