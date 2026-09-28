using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Text;
using ScreenRecorderLib;

namespace Shunshou.Core;

/// <summary>Owns one explicit, local recording session. Device discovery never starts capture.</summary>
public sealed class ScreenRecordingService : IAsyncDisposable
{
    private static int _engineInUse;
    private readonly object _gate = new();
    private Session? _session;
    private bool _disposed;

    public event EventHandler<ScreenRecordingStatus>? StateChanged;
    public ScreenRecordingState State { get { lock (_gate) return _session?.State ?? ScreenRecordingState.Idle; } }
    public TimeSpan Elapsed { get { lock (_gate) return _session?.Clock.Elapsed ?? TimeSpan.Zero; } }
    public string? OutputPath { get { lock (_gate) return _session?.OutputPath; } }
    public string? LastError { get { lock (_gate) return _session?.Error?.Message; } }
    public ScreenRecordingPlan? ActivePlan { get { lock (_gate) return _session?.Plan; } }
    public Task<string> Completion { get { lock (_gate) return _session?.Completed.Task ?? Task.FromResult(""); } }

    public IReadOnlyList<ScreenRecordingDisplay> GetDisplays()
    {
        using var dpi = new PhysicalDpiScope();
        var names = Recorder.GetDisplays().ToDictionary(d => d.DeviceName, d => d.FriendlyName, StringComparer.OrdinalIgnoreCase);
        var result = new List<ScreenRecordingDisplay>();
        Native.EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref Native.Rect rect, nint data) =>
        {
            var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
            if (Native.GetMonitorInfo(monitor, ref info) && names.TryGetValue(info.DeviceName, out var name))
                result.Add(new(info.DeviceName, string.IsNullOrWhiteSpace(name) ? info.DeviceName : name,
                    new(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top),
                    (info.Flags & 1) != 0));
            return true;
        }, 0);
        return result.OrderByDescending(d => d.IsPrimary).ThenBy(d => d.Bounds.X).ThenBy(d => d.Bounds.Y).ToArray();
    }

    public IReadOnlyList<ScreenRecordingAudioDevice> GetAudioDevices(bool input) =>
        Recorder.GetSystemAudioDevices(input ? AudioDeviceSource.InputDevices : AudioDeviceSource.OutputDevices)
            .Select(device => new ScreenRecordingAudioDevice(device.DeviceName, device.FriendlyName, input)).ToArray();

    /// <summary>Reads endpoint mute and volume without changing either or starting an audio capture.</summary>
    public (bool IsMuted, float Volume) GetOutputAudioLevel(string? deviceId = null) => ScreenRecordingAudioDefaults.GetOutputAudioLevel(deviceId);

    public ScreenRecordingPlan CreatePlan(ScreenRecordingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var display = GetDisplays().FirstOrDefault(d => d.DeviceName.Equals(options.DisplayDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("所选显示器已断开，请刷新显示器后重新选择。");
        return BuildPlan(options, display);
    }

    internal static ScreenRecordingPlan BuildPlan(ScreenRecordingOptions options, ScreenRecordingDisplay display)
    {
        if (string.IsNullOrWhiteSpace(options.OutputDirectory)) throw new ArgumentException("请选择录屏保存文件夹。", nameof(options.OutputDirectory));
        _ = Path.GetFullPath(options.OutputDirectory);
        if (options.FrameRate is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(options.FrameRate), "帧率应为 1–120 FPS。");
        if (options.VideoBitrateMbps is { } rate && (!double.IsFinite(rate) || rate is < .1 or > 200))
            throw new ArgumentOutOfRangeException(nameof(options.VideoBitrateMbps), "视频码率应为 0.1–200 Mbps，或选择自动。");
        if (options.MaxOutputHeight is < 64 or > 8192)
            throw new ArgumentOutOfRangeException(nameof(options.MaxOutputHeight), "输出高度应为 64–8192 像素，或选择原始分辨率。");
        if (options.AudioBitrateKbps is not (96 or 128 or 160 or 192))
            throw new ArgumentOutOfRangeException(nameof(options.AudioBitrateKbps), "AAC 音频支持 96、128、160 或 192 kbps。");
        var region = options.Region ?? new ScreenRecordingBounds(0, 0, display.Bounds.Width, display.Bounds.Height);
        if (region.X < 0 || region.Y < 0 || region.Width < 16 || region.Height < 16 ||
            (long)region.X + region.Width > display.Bounds.Width || (long)region.Y + region.Height > display.Bounds.Height)
            throw new ArgumentException("录制区域必须完整位于所选屏幕内，宽高至少为 16 像素。", nameof(options.Region));
        var scale = Math.Min(1d, (options.MaxOutputHeight ?? region.Height) / (double)region.Height);
        var width = (int)Math.Floor(region.Width * scale) / 2 * 2;
        var height = (int)Math.Floor(region.Height * scale) / 2 * 2;
        if (width is < 16 or > 8192 || height is < 16 or > 8192)
            throw new ArgumentException("当前区域的输出尺寸超出编码范围，请调整录制区域或降低分辨率。");
        var bitrate = options.VideoBitrateMbps.HasValue ? (int)Math.Round(options.VideoBitrateMbps.Value * 1_000_000)
            : (int)Math.Clamp(Math.Round(width * (double)height * options.FrameRate * .16), 2_000_000, 60_000_000);
        return new(display, region, width, height, options.FrameRate, bitrate, options.AudioBitrateKbps);
    }

    public Task StartAsync(ScreenRecordingOptions options, CancellationToken ct = default) => StartCoreAsync(options, 0, ct);

    /// <summary>Test-only source: accepts only a visible window created by this process; never captures the desktop.</summary>
    internal Task StartWindowAsync(ScreenRecordingOptions options, nint windowHandle, CancellationToken ct = default) =>
        StartCoreAsync(options, windowHandle, ct);

    internal ScreenRecordingPlan CreateWindowPlan(ScreenRecordingOptions options, nint windowHandle)
    {
        using var dpi = new PhysicalDpiScope();
        Native.GetWindowThreadProcessId(windowHandle, out var processId);
        if (windowHandle == 0 || processId != Environment.ProcessId || !Native.IsWindowVisible(windowHandle) || Native.IsIconic(windowHandle) ||
            !Native.GetWindowRect(windowHandle, out var rect)) throw new ArgumentException("测试录制只接受本进程创建的可见窗口。");
        return BuildPlan(options, new("fixture-window", "录屏测试窗口", new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top), true));
    }

    private async Task StartCoreAsync(ScreenRecordingOptions options, nint windowHandle, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var plan = windowHandle == 0 ? CreatePlan(options) : CreateWindowPlan(options, windowHandle);
        var outputDevice = ResolveAudioDevice(options.CaptureSystemAudio, false, options.AudioOutputDeviceId);
        var inputDevice = ResolveAudioDevice(options.CaptureMicrophone, true, options.AudioInputDeviceId);
        var directory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(directory);
        Session session;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is { } active && !active.Completed.Task.IsCompleted) throw new InvalidOperationException("已有录制正在进行，请先停止并保存。");
            if (Interlocked.CompareExchange(ref _engineInUse, 1, 0) != 0) throw new InvalidOperationException("已有录制窗口正在使用录屏引擎。");
            session = new Session(options, plan, directory, inputDevice, outputDevice);
            _session = session;
        }
        Notify(session);
        try
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(session.WorkDirectory);
            // A unique private directory and CreateNew prevent overwriting any existing file.
            session.Stream = new FileStream(session.Candidate, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            var nativeOptions = CreateNativeOptions(session, windowHandle);
            session.Recorder = Recorder.CreateRecorder(nativeOptions);
            session.Recorder.OnStatusChanged += (_, e) => NativeStatusChanged(session, e.Status);
            session.Recorder.OnRecordingComplete += (_, _) => FinishOnWorker(session, null, nativeCompleted: true);
            session.Recorder.OnRecordingFailed += (_, e) => FinishOnWorker(session, new InvalidOperationException("录屏引擎未能完成录制：" + e.Error));
            session.Pulse = new Timer(_ => Notify(session), null, 250, 250);
            session.Recorder.Record(session.Stream);
            await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            if (options.CaptureSystemAudio || options.CaptureMicrophone)
                await VerifyAudioStartedAsync(session, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (session.Completed.Task.IsCompleted) await session.Completed.Task.ConfigureAwait(false);
            lock (_gate)
            {
                if (!session.Completed.Task.IsCompleted)
                {
                    // The UI remains in Starting until every requested sound source has actually started.
                    if (session.State == ScreenRecordingState.Starting) session.State = ScreenRecordingState.Recording;
                    if (options.CaptureMicrophone || options.CaptureSystemAudio)
                    {
                        session.MonitorCancellation = new CancellationTokenSource();
                        _ = MonitorDevicesAsync(session, session.MonitorCancellation.Token);
                    }
                }
            }
            Notify(session);
        }
        catch (Exception ex)
        {
            lock (_gate) session.Error ??= ex is TimeoutException
                ? new InvalidOperationException("录屏引擎启动超时，请降低帧率、分辨率或关闭硬件编码后重试。", ex) : ex;
            await StopAfterErrorAsync(session).ConfigureAwait(false);
            ExceptionDispatchInfo.Capture(session.Error).Throw();
            throw; // Unreachable; preserves flow analysis on earlier compiler toolchains.
        }
    }

    private string? ResolveAudioDevice(bool enabled, bool input, string? requested)
    {
        if (!enabled) return null;
        var devices = GetAudioDevices(input);
        var label = input ? "麦克风" : "系统声音输出设备";
        if (devices.Count == 0) throw new InvalidOperationException($"没有可用的{label}。请连接设备，或关闭对应录音选项。");
        if (string.IsNullOrWhiteSpace(requested)) requested = ScreenRecordingAudioDefaults.GetDefaultDeviceId(input);
        if (!devices.Any(d => d.Id.Equals(requested, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"所选{label}已不可用，请刷新后重新选择。");
        return requested;
    }

    private static RecorderOptions CreateNativeOptions(Session s, nint hwnd)
    {
        var plan = s.Plan;
        var sourceRect = new ScreenRect(plan.Region.X, plan.Region.Y, plan.Region.Width, plan.Region.Height);
        RecordingSourceBase source = hwnd == 0 ? new DisplayRecordingSource(s.Options.DisplayDeviceName)
        {
            RecorderApi = RecorderApi.DesktopDuplication, IsCursorCaptureEnabled = s.Options.ShowCursor,
            SourceRect = sourceRect, OutputSize = new ScreenSize(plan.OutputWidth, plan.OutputHeight)
        } : new WindowRecordingSource(hwnd)
        {
            IsCursorCaptureEnabled = s.Options.ShowCursor, IsBorderRequired = true,
            SourceRect = sourceRect, OutputSize = new ScreenSize(plan.OutputWidth, plan.OutputHeight)
        };
        var options = RecorderOptions.Default;
        options.SourceOptions.RecordingSources = [source];
        options.OutputOptions.RecorderMode = RecorderMode.Video;
        options.OutputOptions.OutputFrameSize = new ScreenSize(plan.OutputWidth, plan.OutputHeight);
        options.VideoEncoderOptions = new VideoEncoderOptions
        {
            Encoder = new H264VideoEncoder { BitrateMode = H264BitrateControlMode.UnconstrainedVBR, EncoderProfile = H264Profile.High },
            Bitrate = plan.VideoBitrate, Framerate = plan.FrameRate, IsFixedFramerate = true,
            IsHardwareEncodingEnabled = s.Options.PreferHardwareEncoding,
            IsMp4FastStartEnabled = true, IsFragmentedMp4Enabled = false, IsLowLatencyEnabled = false
        };
        options.AudioOptions = new AudioOptions
        {
            IsAudioEnabled = s.Options.CaptureSystemAudio || s.Options.CaptureMicrophone,
            IsOutputDeviceEnabled = s.Options.CaptureSystemAudio, IsInputDeviceEnabled = s.Options.CaptureMicrophone,
            AudioOutputDevice = s.OutputDevice, AudioInputDevice = s.InputDevice, Channels = AudioChannels.Stereo,
            Bitrate = (AudioBitrate)(s.Options.AudioBitrateKbps * 1000 / 8),
            InputVolume = 1f, OutputVolume = 1f
        };
        options.MouseOptions.IsMousePointerEnabled = s.Options.ShowCursor;
        options.MouseOptions.IsMouseClicksDetected = false;
        // Upstream 6.6 does not propagate every WASAPI startup failure. Its local startup log is checked below.
        options.LogOptions = new LogOptions { IsLogEnabled = true, LogSeverityLevel = LogLevel.Info, LogFilePath = s.LogPath };
        return options;
    }

    private async Task VerifyAudioStartedAsync(Session s, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(8))
        {
            ct.ThrowIfCancellationRequested();
            if (s.Completed.Task.IsCompleted) { await s.Completed.Task.ConfigureAwait(false); return; }
            var log = ReadSessionLog(s);
            if ((!s.Options.CaptureMicrophone || log.Contains("Started audio capture on AudioInputDevice:", StringComparison.Ordinal)) &&
                (!s.Options.CaptureSystemAudio || log.Contains("Started audio capture on AudioOutputDevice:", StringComparison.Ordinal))) return;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        throw new InvalidOperationException("所选声音设备未能开始录音。请检查设备及 Windows 麦克风隐私权限，或关闭对应录音选项后重试。");
    }

    private static string ReadSessionLog(Session s)
    {
        try
        {
            using var stream = new FileStream(s.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 2_000_000) stream.Seek(-2_000_000, SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            return reader.ReadToEnd().Replace("\0", "", StringComparison.Ordinal);
        }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    private async Task MonitorDevicesAsync(Session s, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(2000, ct).ConfigureAwait(false);
                if (s.Options.CaptureMicrophone || s.Options.CaptureSystemAudio)
                {
                    var log = ReadSessionLog(s);
                    if (log.Contains("Audio capture loop failed", StringComparison.Ordinal) ||
                        log.Contains("Exception in WASAPICapture", StringComparison.Ordinal) ||
                        log.Contains("IAudioCaptureClient::GetBuffer failed", StringComparison.Ordinal) ||
                        log.Contains("IAudioCaptureClient::GetNextPacketSize failed", StringComparison.Ordinal))
                        throw new IOException("声音采集发生错误，录制已停止。请检查声音设备后重试。");
                }
                if (s.Options.CaptureMicrophone && !GetAudioDevices(true).Any(d => d.Id == s.InputDevice))
                    throw new IOException("麦克风已断开，录制已停止；请重新连接设备。");
                if (s.Options.CaptureSystemAudio && !GetAudioDevices(false).Any(d => d.Id == s.OutputDevice))
                    throw new IOException("系统声音输出设备已断开，录制已停止；请重新选择设备。");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            try { await StopForAudioErrorAsync(s, ex).ConfigureAwait(false); }
            catch { /* Completion and the UI retain the error and any safely saved interrupted video. */ }
        }
    }

    private Task<string> StopForAudioErrorAsync(Session s, Exception error)
    {
        lock (_gate)
        {
            if (s.Completed.Task.IsCompleted || s.State == ScreenRecordingState.Stopping) return s.Completed.Task;
            s.Error ??= error;
            s.PreserveInterruptedVideo = true;
        }
        // Unlike a startup/encoder failure, a lost audio device allows the video encoder to finalize normally.
        return StopAsync();
    }

    internal async Task FailAudioMonitoringForTestAsync(string message)
    {
        Session s;
        lock (_gate) s = _session is { State: ScreenRecordingState.Recording or ScreenRecordingState.Paused } active
            ? active : throw new InvalidOperationException("需要先启动可控测试录制。");
        await StopForAudioErrorAsync(s, new IOException(message)).ConfigureAwait(false);
    }

    public void Pause()
    {
        Session s;
        lock (_gate) s = _session is { State: ScreenRecordingState.Recording } value ? value
            : throw new InvalidOperationException("当前没有正在录制的内容。");
        lock (s.NativeGate) s.Recorder?.Pause();
    }

    public void Resume()
    {
        Session s;
        lock (_gate) s = _session is { State: ScreenRecordingState.Paused } value ? value
            : throw new InvalidOperationException("当前录制尚未暂停。");
        lock (s.NativeGate) s.Recorder?.Resume();
    }

    public async Task<string> StopAsync()
    {
        Session s;
        lock (_gate)
        {
            s = _session ?? throw new InvalidOperationException("尚未开始录制。");
            if (!s.Completed.Task.IsCompleted) { s.State = ScreenRecordingState.Stopping; s.Clock.Stop(); }
        }
        Notify(s);
        lock (s.NativeGate) s.Recorder?.Stop();
        return await s.Completed.Task.ConfigureAwait(false);
    }

    private async Task StopAfterErrorAsync(Session s)
    {
        lock (s.NativeGate) s.Recorder?.Stop();
        // Dispose on a worker joins the native recording task, releasing screen and sound capture before returning.
        FinishOnWorker(s, s.Error);
        try { await s.Completed.Task.ConfigureAwait(false); } catch { /* The caller reports the original error. */ }
    }

    private void NativeStatusChanged(Session s, RecorderStatus status)
    {
        lock (_gate)
        {
            if (s.Completed.Task.IsCompleted || s.State == ScreenRecordingState.Stopping) return;
            switch (status)
            {
                case RecorderStatus.Recording:
                    if (s.State != ScreenRecordingState.Starting) s.State = ScreenRecordingState.Recording;
                    s.Clock.Start(); s.Started.TrySetResult(); break;
                case RecorderStatus.Paused:
                    s.State = ScreenRecordingState.Paused; s.Clock.Stop(); break;
                case RecorderStatus.Finishing:
                    s.State = ScreenRecordingState.Stopping; s.Clock.Stop(); break;
            }
        }
        Notify(s);
    }

    private void FinishOnWorker(Session s, Exception? error, bool nativeCompleted = false)
    {
        lock (_gate) { s.Error ??= error; s.NativeCompleted |= nativeCompleted; }
        if (Interlocked.Exchange(ref s.Finishing, 1) != 0) return;
        _ = Task.Run(() => FinishAsync(s));
    }

    private async Task FinishAsync(Session s)
    {
        try
        {
            s.Pulse?.Dispose();
            s.MonitorCancellation?.Cancel();
            s.Clock.Stop();
            lock (s.NativeGate) { s.Recorder?.Dispose(); s.Recorder = null; }
            s.Stream?.Dispose(); s.Stream = null;
            if (s.Error is null || (s.PreserveInterruptedVideo && s.NativeCompleted))
            {
                if (!File.Exists(s.Candidate) || new FileInfo(s.Candidate).Length < 128)
                    throw new InvalidDataException("录屏没有产生有效视频，请检查显示器和编码设置。");
                if (s.Error is not null)
                {
                    // Native success is mandatory; independently ask Windows to parse the finalized MP4.
                    // An encoder failure or an unreadable/incomplete video is never presented as a saved recording.
                    var video = await Windows.Storage.StorageFile.GetFileFromPathAsync(s.Candidate);
                    var properties = await video.Properties.GetVideoPropertiesAsync();
                    if (properties.Duration <= TimeSpan.Zero || properties.Width != s.Plan.OutputWidth || properties.Height != s.Plan.OutputHeight)
                        throw new InvalidDataException("声音中断后的录制文件未通过完整性检查，未发布该片段。");
                }
                s.OutputPath = Publish(s.Candidate, s.OutputDirectory);
                if (s.Error is not null) s.Error = new IOException(s.Error.Message + " 已保存中断前内容：" + Path.GetFileName(s.OutputPath), s.Error);
            }
        }
        catch (Exception ex) { lock (_gate) s.Error ??= ex; }
        finally
        {
            s.Stream?.Dispose();
            s.MonitorCancellation?.Dispose();
            CleanWorkDirectory(s);
            Interlocked.Exchange(ref _engineInUse, 0);
            lock (_gate)
            {
                s.State = s.Error is null ? ScreenRecordingState.Completed : ScreenRecordingState.Failed;
                if (s.Error is null) { s.Started.TrySetResult(); s.Completed.TrySetResult(s.OutputPath!); }
                else { s.Started.TrySetException(s.Error); s.Completed.TrySetException(s.Error); }
            }
            Notify(s);
        }
    }

    internal static string Publish(string candidate, string outputDirectory)
    {
        var prefix = "Recording_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        for (var index = 0; index < 10000; index++)
        {
            var path = Path.Combine(outputDirectory, prefix + (index == 0 ? "" : $"_{index}") + ".mp4");
            try { File.Move(candidate, path, false); return path; }
            catch (IOException) when (File.Exists(path)) { }
        }
        throw new IOException("同名录屏文件过多，请选择其他输出文件夹。");
    }

    private static void CleanWorkDirectory(Session s)
    {
        // Only files created for this session are removed. No recursive cleanup of user-chosen folders.
        try { if (File.Exists(s.Candidate)) File.Delete(s.Candidate); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { if (File.Exists(s.LogPath)) File.Delete(s.LogPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { Directory.Delete(s.WorkDirectory, false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Notify(Session s)
    {
        ScreenRecordingStatus status;
        lock (_gate) { if (_session != s) return; status = new(s.State, s.Clock.Elapsed, s.OutputPath, s.Error?.Message); }
        try { StateChanged?.Invoke(this, status); } catch { /* A closing UI subscriber must not break native capture cleanup. */ }
    }

    public async ValueTask DisposeAsync()
    {
        Session? s;
        lock (_gate) { if (_disposed) return; _disposed = true; s = _session; }
        if (s is not null && !s.Completed.Task.IsCompleted)
        {
            try { await StopAsync().ConfigureAwait(false); }
            catch { await StopAfterErrorAsync(s).ConfigureAwait(false); }
        }
        GC.SuppressFinalize(this);
    }

    private sealed class Session(ScreenRecordingOptions options, ScreenRecordingPlan plan, string directory, string? input, string? output)
    {
        public readonly object NativeGate = new();
        public readonly ScreenRecordingOptions Options = options;
        public readonly ScreenRecordingPlan Plan = plan;
        public readonly string OutputDirectory = directory;
        public readonly string WorkDirectory = Path.Combine(directory, ".recording-" + Guid.NewGuid().ToString("N"));
        public string Candidate => Path.Combine(WorkDirectory, "capture.mp4");
        public string LogPath => Path.Combine(WorkDirectory, "engine.log");
        public readonly string? InputDevice = input, OutputDevice = output;
        public readonly Stopwatch Clock = new();
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<string> Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ScreenRecordingState State = ScreenRecordingState.Starting;
        public Recorder? Recorder;
        public FileStream? Stream;
        public Timer? Pulse;
        public CancellationTokenSource? MonitorCancellation;
        public Exception? Error;
        public string? OutputPath;
        public int Finishing;
        public bool PreserveInterruptedVideo, NativeCompleted;
    }

    private sealed class PhysicalDpiScope : IDisposable
    {
        private readonly nint _previous = Native.SetThreadDpiAwarenessContext(new nint(-4));
        public void Dispose() { if (_previous != 0) Native.SetThreadDpiAwarenessContext(_previous); }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct MonitorInfo
        {
            public int Size; public Rect Monitor; public Rect Work; public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        }
        internal delegate bool MonitorProc(nint monitor, nint dc, ref Rect rect, nint data);
        [DllImport("user32.dll")] internal static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorProc proc, nint data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] internal static extern nint SetThreadDpiAwarenessContext(nint context);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out int processId);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
        [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
    }
}
