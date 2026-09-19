using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shunshou.Core;
using Windows.Storage.Pickers;

namespace Shunshou.App;

public sealed partial class ScreenRecordingWorkspace : UserControl, IDisposable
{
    public nint HostWindowHandle { get; set; }
    public event EventHandler<bool>? BusyChanged;
    public bool IsBusy => _sessionActive;
    private readonly ScreenRecordingService _recorder = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private ScreenRecordingBounds? _region;
    private ScreenRecordingBar? _bar;
    private RecordingHotKeys? _hotKeys;
    private CancellationTokenSource? _startCancellation;
    private Task _session = Task.CompletedTask;
    private bool _ready, _loading, _loaded, _updating, _sessionActive, _countingDown, _disposed, _minimized;
    private string? _lastOutput;
    private sealed record DeviceChoice(string? Id, string Label) { public override string ToString() => Label; }
    private string PreferencesPath => Path.Combine(AppPaths.DataDirectory, "screen-recording.json");

    public ScreenRecordingWorkspace()
    {
        InitializeComponent();
        OutputBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ShunshouToolbox", "Output", "Recordings");
        _updating = true;
        ScopeBox.SelectedIndex = PresetBox.SelectedIndex = CountdownBox.SelectedIndex = 0;
        ResolutionBox.SelectedIndex = 1;
        AudioBitrateBox.SelectedIndex = 3;
        _updating = false;
        _ready = true;
        LoadPreferences();
        _timer.Tick += (_, _) => RefreshSessionStatus();
        _recorder.StateChanged += (_, _) => DispatcherQueue.TryEnqueue(() => { if (!_disposed) RefreshSessionStatus(); });
    }

    public async Task EnsureLoadedAsync()
    {
        if (_loaded || _disposed) return;
        if (_loading) { while (_loading && !_disposed) await Task.Delay(50); return; }
        await RefreshDevicesAsync();
    }

    private async Task RefreshDevicesAsync()
    {
        if (_sessionActive || _loading) return;
        _loading = true; SettingsPanel.IsEnabled = false; StartButton.IsEnabled = false;
        try
        {
            var current = (DisplayBox.SelectedItem as ScreenRecordingDisplay)?.DeviceName;
            var data = await Task.Run(() => (_recorder.GetDisplays(), _recorder.GetAudioDevices(false), _recorder.GetAudioDevices(true)));
            if (_disposed) return;
            DisplayBox.ItemsSource = data.Item1;
            DisplayBox.SelectedItem = data.Item1.FirstOrDefault(d => d.DeviceName == current) ?? data.Item1.FirstOrDefault(d => d.IsPrimary) ?? data.Item1.FirstOrDefault();
            SystemDeviceBox.ItemsSource = Choices(data.Item2);
            MicrophoneDeviceBox.ItemsSource = Choices(data.Item3);
            SystemDeviceBox.SelectedIndex = MicrophoneDeviceBox.SelectedIndex = 0;
            SystemAudioBox.IsEnabled = data.Item2.Count > 0;
            MicrophoneBox.IsEnabled = data.Item3.Count > 0;
            if (!SystemAudioBox.IsEnabled) SystemAudioBox.IsChecked = false;
            if (!MicrophoneBox.IsEnabled) MicrophoneBox.IsChecked = false;
            _loaded = true;
            UpdateSoundControls();
            UpdateRegionText();
            UpdateEncodingSummary();
            if (data.Item1.Count == 0) ShowError("未找到可录制的显示器。");
        }
        catch (Exception ex) { ShowError("无法读取录制设备：" + ex.Message); }
        finally { _loading = false; if (!_disposed) { SettingsPanel.IsEnabled = true; StartButton.IsEnabled = DisplayBox.SelectedItem is ScreenRecordingDisplay; } }
    }

    private static DeviceChoice[] Choices(IReadOnlyList<ScreenRecordingAudioDevice> devices) =>
        devices.Count == 0 ? [new(null, "没有可用设备")] : new[] { new DeviceChoice(null, "系统默认设备") }.Concat(devices.Select(d => new DeviceChoice(d.Id, d.Name))).ToArray();

    private async void Refresh_Click(object sender, RoutedEventArgs args) => await RefreshDevicesAsync();
    private void Display_Changed(object sender, SelectionChangedEventArgs args) { _region = null; if (_ready) { UpdateRegionText(); UpdateEncodingSummary(); } }
    private void Scope_Changed(object sender, SelectionChangedEventArgs args) { if (_ready) { RegionButton.IsEnabled = ScopeBox.SelectedIndex == 1; UpdateRegionText(); UpdateEncodingSummary(); } }
    private async void SelectRegion_Click(object sender, RoutedEventArgs args)
    {
        if (_sessionActive || DisplayBox.SelectedItem is not ScreenRecordingDisplay display) return;
        SettingsPanel.IsEnabled = StartButton.IsEnabled = false;
        BusyChanged?.Invoke(this, true);
        try
        {
            var bounds = display.Bounds;
            var chosen = await ScreenRecordingPicker.SelectAsync(HostWindowHandle, bounds.X, bounds.Y, bounds.Width, bounds.Height);
            if (chosen is { } r) { _region = new(r.X, r.Y, r.Width, r.Height); UpdateRegionText(); UpdateEncodingSummary(); }
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { if (!_disposed) { SettingsPanel.IsEnabled = StartButton.IsEnabled = true; BusyChanged?.Invoke(this, false); } }
    }

    private void UpdateRegionText()
    {
        if (DisplayBox.SelectedItem is not ScreenRecordingDisplay display) { RegionText.Text = "请选择显示器"; return; }
        RegionText.Text = ScopeBox.SelectedIndex == 1
            ? _region is { } r ? $"选区 {r.Width} × {r.Height} · 左侧 {r.X}，顶部 {r.Y}" : "点击“框选区域”，拖动选取要录制的位置；Esc 取消。"
            : $"整屏 {display.Bounds.Width} × {display.Bounds.Height}";
    }

    private void Preset_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (!_ready || _updating || PresetBox.SelectedIndex == 3) return;
        _updating = true;
        (ResolutionBox.SelectedIndex, FrameRateBox.Value, BitrateBox.Value, AutoBitrateBox.IsChecked) = PresetBox.SelectedIndex switch
        {
            1 => (0, 60d, 20d, true),
            2 => (2, 24d, 3d, false),
            _ => (1, 30d, 8d, true)
        };
        BitrateBox.IsEnabled = AutoBitrateBox.IsChecked != true;
        _updating = false;
        UpdateEncodingSummary();
    }
    private void Option_Changed(object sender, SelectionChangedEventArgs args) => MarkCustom();
    private void Number_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) => MarkCustom();
    private void Bitrate_Changed(object sender, RoutedEventArgs args)
    {
        if (BitrateBox is not null) BitrateBox.IsEnabled = AutoBitrateBox.IsChecked != true;
        MarkCustom();
    }
    private void MarkCustom() { if (!_ready || _updating) return; PresetBox.SelectedIndex = 3; UpdateEncodingSummary(); }
    private void Sound_Changed(object sender, RoutedEventArgs args) { if (_ready) UpdateSoundControls(); }
    private void UpdateSoundControls()
    {
        SystemDeviceBox.IsEnabled = SystemAudioBox.IsChecked == true;
        MicrophoneDeviceBox.IsEnabled = MicrophoneBox.IsChecked == true;
        AudioBitrateBox.IsEnabled = SystemAudioBox.IsChecked == true || MicrophoneBox.IsChecked == true;
        UpdateAudioHint();
    }

    private void AudioDevice_Changed(object sender, SelectionChangedEventArgs args) { if (_ready) UpdateAudioHint(); }
    private void UpdateAudioHint()
    {
        if (MutedAudioHint is null) return;
        bool muted = false;
        try
        {
            if (SystemAudioBox.IsChecked == true)
            {
                var level = _recorder.GetOutputAudioLevel((SystemDeviceBox.SelectedItem as DeviceChoice)?.Id);
                muted = level.IsMuted || level.Volume <= .001;
            }
        }
        catch (Exception) { /* Start validates the selected device and reports actionable failures. */ }
        MutedAudioHint.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
    }

    private ScreenRecordingOptions ReadOptions()
    {
        if (DisplayBox.SelectedItem is not ScreenRecordingDisplay display) throw new InvalidOperationException("请选择显示器。");
        if (ScopeBox.SelectedIndex == 1 && _region is null) throw new InvalidOperationException("请先框选录制区域。");
        if (!double.IsFinite(FrameRateBox.Value) || FrameRateBox.Value != Math.Truncate(FrameRateBox.Value)) throw new InvalidOperationException("帧率请填写 1–120 之间的整数。");
        return new()
        {
            DisplayDeviceName = display.DeviceName, OutputDirectory = OutputBox.Text.Trim(),
            Region = ScopeBox.SelectedIndex == 1 ? _region : null,
            FrameRate = checked((int)FrameRateBox.Value), MaxOutputHeight = ResolutionBox.SelectedIndex switch { 1 => 1080, 2 => 720, _ => null },
            VideoBitrateMbps = AutoBitrateBox.IsChecked == true ? null : BitrateBox.Value,
            CaptureSystemAudio = SystemAudioBox.IsChecked == true, CaptureMicrophone = MicrophoneBox.IsChecked == true,
            AudioOutputDeviceId = (SystemDeviceBox.SelectedItem as DeviceChoice)?.Id, AudioInputDeviceId = (MicrophoneDeviceBox.SelectedItem as DeviceChoice)?.Id,
            AudioBitrateKbps = AudioBitrateBox.SelectedIndex switch { 0 => 96, 1 => 128, 2 => 160, _ => 192 },
            ShowCursor = CursorBox.IsChecked == true, PreferHardwareEncoding = HardwareBox.IsChecked == true
        };
    }
    private void UpdateEncodingSummary()
    {
        if (!_loaded || _updating) return;
        try
        {
            var plan = _recorder.CreatePlan(ReadOptions());
            EncodingSummary.Text = $"MP4 · {plan.OutputWidth} × {plan.OutputHeight} · {plan.FrameRate} FPS · 视频 {plan.VideoBitrate / 1_000_000d:0.#} Mbps";
        }
        catch (Exception ex) { EncodingSummary.Text = ex.Message; }
    }

    private async void Start_Click(object sender, RoutedEventArgs args)
    {
        if (_sessionActive) { await StopAndSaveAsync(); return; }
        _session = RunSessionAsync();
        await _session;
    }
    private async Task RunSessionAsync()
    {
        if (_sessionActive || _disposed) return;
        string? previousOutput = _recorder.OutputPath;
        try
        {
            var options = ReadOptions();
            UpdateAudioHint();
            var plan = _recorder.CreatePlan(options);
            MessageBar.IsOpen = false;
            _sessionActive = true; _countingDown = true;
            _lastOutput = null;
            _startCancellation = new();
            var ct = _startCancellation.Token;
            SettingsPanel.IsEnabled = OpenButton.IsEnabled = false;
            StartButton.Content = "取消开始";
            BusyChanged?.Invoke(this, true);
            SavePreferences();
            if (RecordingHotKeys.TryCreate(out var keys, out var hotKeyError))
            {
                _hotKeys = keys;
                keys!.TogglePauseRequested += (_, _) => DispatcherQueue.TryEnqueue(TogglePause);
                keys.StopRequested += (_, _) => DispatcherQueue.TryEnqueue(async () => await StopAndSaveAsync());
                ShortcutText.Text = "Ctrl + Alt + F9 暂停 / 继续 · Ctrl + Alt + F10 停止";
            }
            else { ShortcutText.Text = "快捷键被占用，请用录制控制条操作"; ShowMessage("快捷键不可用", hotKeyError ?? ShortcutText.Text, InfoBarSeverity.Informational); }
            int seconds = CountdownBox.SelectedIndex switch { 1 => 5, 2 => 0, _ => 3 };
            for (int i = seconds; i > 0; i--) { StatusText.Text = $"{i} 秒后开始录制…"; await Task.Delay(1000, ct); }
            ct.ThrowIfCancellationRequested();
            _bar = new ScreenRecordingBar(plan.Display.Bounds, ActualTheme);
            _bar.TogglePauseRequested += (_, _) => TogglePause();
            _bar.StopRequested += async (_, _) => await StopAndSaveAsync();
            if (_bar.ExcludedFromCapture) _bar.Activate();
            else { _bar.Shutdown(); _bar = null; }
            if (MinimizeBox.IsChecked == true && (_bar is not null || _hotKeys is not null))
            {
                ShowWindow(HostWindowHandle, 6); _minimized = true;
                await Task.Delay(300, ct);
            }
            _countingDown = false;
            StatusText.Text = "正在启动录制…";
            await _recorder.StartAsync(options, ct);
            _timer.Start(); RefreshSessionStatus();
            string output = await _recorder.Completion;
            _lastOutput = output;
            var bytes = new FileInfo(output).Length;
            StatusText.Text = $"已保存 · {bytes / 1_000_000d:0.0} MB";
            ShowMessage("录制已保存", Path.GetFileName(output), InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消开始"; }
        catch (Exception ex)
        {
            if (_recorder.State == ScreenRecordingState.Failed && _recorder.OutputPath is { } partial && partial != previousOutput && File.Exists(partial))
                _lastOutput = partial;
            StatusText.Text = _lastOutput is null ? "录制未完成" : "录制中断 · 已保存现有内容";
            ShowError(ex.Message);
        }
        finally
        {
            _timer.Stop();
            _hotKeys?.Dispose(); _hotKeys = null;
            _bar?.Shutdown(); _bar = null;
            _startCancellation?.Dispose(); _startCancellation = null;
            _countingDown = _sessionActive = false;
            if (_minimized) { ShowWindow(HostWindowHandle, 9); _minimized = false; }
            if (!_disposed)
            {
                SettingsPanel.IsEnabled = true; StartButton.IsEnabled = true; StartButton.Content = "开始录制";
                PauseButton.IsEnabled = false; PauseButton.Content = "暂停";
                OpenButton.IsEnabled = _lastOutput is not null;
                BusyChanged?.Invoke(this, false);
            }
        }
    }

    public async Task StopAndSaveAsync()
    {
        if (!_sessionActive) return;
        if (_countingDown || _recorder.State == ScreenRecordingState.Starting) { _startCancellation?.Cancel(); }
        else if (_recorder.State is ScreenRecordingState.Recording or ScreenRecordingState.Paused)
        {
            try { await _recorder.StopAsync(); }
            catch (Exception ex) { ShowError(ex.Message); }
        }
        await _session;
    }
    private void Pause_Click(object sender, RoutedEventArgs args) => TogglePause();
    private void TogglePause()
    {
        try
        {
            if (_recorder.State == ScreenRecordingState.Recording) _recorder.Pause();
            else if (_recorder.State == ScreenRecordingState.Paused) _recorder.Resume();
            RefreshSessionStatus();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private void RefreshSessionStatus()
    {
        if (!_sessionActive || _countingDown) return;
        var state = _recorder.State;
        var elapsed = _recorder.Elapsed;
        if (state is ScreenRecordingState.Recording or ScreenRecordingState.Paused or ScreenRecordingState.Stopping)
        {
            string duration = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
            StatusText.Text = state == ScreenRecordingState.Stopping ? "正在保存录制…" : (state == ScreenRecordingState.Paused ? "已暂停 · " : "正在录制 · ") + duration;
        }
        StartButton.Content = state == ScreenRecordingState.Starting ? "取消开始" : "停止并保存";
        StartButton.IsEnabled = state != ScreenRecordingState.Stopping;
        PauseButton.IsEnabled = state is ScreenRecordingState.Recording or ScreenRecordingState.Paused;
        PauseButton.Content = state == ScreenRecordingState.Paused ? "继续" : "暂停";
        _bar?.Update(state, elapsed);
    }
    private async void Browse_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, HostWindowHandle);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) OutputBox.Text = folder.Path;
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private void Open_Click(object sender, RoutedEventArgs args)
    {
        try { if (_lastOutput is not null) Process.Start(new ProcessStartInfo(_lastOutput) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private void ShowError(string message) => ShowMessage("无法完成录制", message, InfoBarSeverity.Error);
    private void ShowMessage(string title, string message, InfoBarSeverity severity) { MessageBar.Title = title; MessageBar.Message = message; MessageBar.Severity = severity; MessageBar.IsOpen = true; }

    private sealed record Preferences(int Preset, int Resolution, int Fps, bool AutoBitrate, double Bitrate, int AudioBitrate, bool SystemSound, bool Microphone, bool Cursor, bool Hardware, bool Minimize, int Countdown, string Output);
    private void SavePreferences()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var prefs = new Preferences(PresetBox.SelectedIndex, ResolutionBox.SelectedIndex, (int)FrameRateBox.Value, AutoBitrateBox.IsChecked == true, BitrateBox.Value, AudioBitrateBox.SelectedIndex, SystemAudioBox.IsChecked == true, MicrophoneBox.IsChecked == true, CursorBox.IsChecked == true, HardwareBox.IsChecked == true, MinimizeBox.IsChecked == true, CountdownBox.SelectedIndex, OutputBox.Text.Trim());
            File.WriteAllText(PreferencesPath, JsonSerializer.Serialize(prefs));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Recording can proceed without remembering preferences. */ }
    }
    private void LoadPreferences()
    {
        try
        {
            var file = new FileInfo(PreferencesPath);
            if (!file.Exists || file.Length > 16384) return;
            var prefs = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(file.FullName));
            if (prefs is null) return;
            _updating = true;
            PresetBox.SelectedIndex = Math.Clamp(prefs.Preset, 0, 3); ResolutionBox.SelectedIndex = Math.Clamp(prefs.Resolution, 0, 2);
            FrameRateBox.Value = Math.Clamp(prefs.Fps, 1, 120); AutoBitrateBox.IsChecked = prefs.AutoBitrate;
            BitrateBox.Value = double.IsFinite(prefs.Bitrate) ? Math.Clamp(prefs.Bitrate, 0.5, 100) : 8;
            AudioBitrateBox.SelectedIndex = Math.Clamp(prefs.AudioBitrate, 0, 3);
            SystemAudioBox.IsChecked = prefs.SystemSound; MicrophoneBox.IsChecked = prefs.Microphone;
            CursorBox.IsChecked = prefs.Cursor; HardwareBox.IsChecked = prefs.Hardware; MinimizeBox.IsChecked = prefs.Minimize;
            CountdownBox.SelectedIndex = Math.Clamp(prefs.Countdown, 0, 2);
            if (!string.IsNullOrWhiteSpace(prefs.Output)) OutputBox.Text = prefs.Output;
            UpdateSoundControls();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        finally { _updating = false; }
    }
    public void Dispose()
    {
        _disposed = true; _timer.Stop(); _startCancellation?.Cancel();
        _hotKeys?.Dispose(); _bar?.Shutdown();
        _ = _recorder.DisposeAsync().AsTask();
    }
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
}
