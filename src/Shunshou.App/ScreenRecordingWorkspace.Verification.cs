using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Shunshou.Core;
using Windows.Graphics;
using Windows.UI;

namespace Shunshou.App;

public sealed partial class ScreenRecordingWorkspace
{
    internal async Task VerifyControlsAsync(string output)
    {
        await EnsureLoadedAsync();
        RequireRecording(_loaded && DisplayBox.SelectedItem is ScreenRecordingDisplay && _recorder.State == ScreenRecordingState.Idle, "Device discovery must not start capture.");
        PresetBox.SelectedIndex = 1;
        RequireRecording(FrameRateBox.Value == 60 && ResolutionBox.SelectedIndex == 0 && AutoBitrateBox.IsChecked == true, "High-quality preset.");
        FrameRateBox.Value = 48; AutoBitrateBox.IsChecked = false; BitrateBox.Value = 12.5; ResolutionBox.SelectedIndex = 2;
        var plan = _recorder.CreatePlan(ReadOptions());
        RequireRecording(PresetBox.SelectedIndex == 3 && plan.FrameRate == 48 && plan.OutputHeight <= 720 && plan.VideoBitrate == 12_500_000, "Custom FPS, resolution and bitrate must reach encoder plan.");
        FrameRateBox.Value = double.NaN;
        bool rejected = false;
        try { _ = ReadOptions(); } catch (InvalidOperationException) { rejected = true; }
        RequireRecording(rejected, "Blank frame rate must be rejected.");
        PresetBox.SelectedIndex = 0;
        OutputBox.Text = output;
        SystemAudioBox.IsChecked = MicrophoneBox.IsChecked = false;
        MinimizeBox.IsChecked = false;
        CountdownBox.SelectedIndex = 1;
        _session = RunSessionAsync();
        await Task.Delay(150);
        RequireRecording(_sessionActive && !SettingsPanel.IsEnabled, "Countdown locks recording options.");
        await StopAndSaveAsync();
        RequireRecording(!IsBusy && _recorder.State == ScreenRecordingState.Idle && !Directory.Exists(output), "Cancel countdown must not start capture or create video output.");
        CountdownBox.SelectedIndex = 0;
        SystemAudioBox.IsChecked = true;
        AdvancedExpander.IsExpanded = false;
    }

    internal bool AdvancedShown { set => AdvancedExpander.IsExpanded = value; }
    internal void ScrollAdvancedIntoView() => SettingsScroll.ChangeView(null, 300, null, true);
    internal void ScrollToStart() => SettingsScroll.ChangeView(null, 0, null, true);

    internal async Task<string> VerifyActualRecordingAsync(string output, Func<string, UIElement, Task> capture)
    {
        var display = (ScreenRecordingDisplay)DisplayBox.SelectedItem;
        var fixture = new Window { Title = "Synthetic recording fixture" };
        var surface = new Grid { Background = new SolidColorBrush(Color.FromArgb(255, 27, 104, 180)) };
        surface.Children.Add(new TextBlock { Text = "SHUNSHOU RECORDING TEST", FontSize = 22, Margin = new Thickness(25), Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)) });
        var tile = new Border { Width = 90, Height = 80, Background = new SolidColorBrush(Color.FromArgb(255, 250, 218, 78)), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(30, 100, 0, 0) };
        surface.Children.Add(tile);
        fixture.Content = surface;
        var presenter = OverlappedPresenter.Create(); presenter.SetBorderAndTitleBar(false, false); presenter.IsAlwaysOnTop = true;
        fixture.AppWindow.SetPresenter(presenter);
        var fixturePosition = new PointInt32(display.Bounds.X + 80, display.Bounds.Y + 110);
        fixture.AppWindow.MoveAndResize(new RectInt32(fixturePosition.X, fixturePosition.Y, 640, 360));
        fixture.Activate();
        var animation = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        int phase = 0;
        animation.Tick += (_, _) => tile.Margin = new Thickness(30 + (++phase % 8) * 35, 100, 0, 0);
        animation.Start();
        try
        {
            await Task.Delay(500);
            ScopeBox.SelectedIndex = 1;
            _region = new ScreenRecordingBounds(96, 126, 600, 320);
            ResolutionBox.SelectedIndex = 0; FrameRateBox.Value = 30;
            AutoBitrateBox.IsChecked = false; BitrateBox.Value = 4;
            SystemAudioBox.IsChecked = MicrophoneBox.IsChecked = false;
            MinimizeBox.IsChecked = false; CountdownBox.SelectedIndex = 2; OutputBox.Text = output;
            _session = RunSessionAsync();
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (_recorder.State != ScreenRecordingState.Recording && !_session.IsCompleted && DateTime.UtcNow < deadline) await Task.Delay(100);
            RequireRecording(_recorder.State == ScreenRecordingState.Recording && _bar is not null, "Real UI start and floating controls: " + MessageBar.Message);
            _bar!.AppWindow.Move(new PointInt32(display.Bounds.X + 180, display.Bounds.Y + 180));
            await Task.Delay(650);
            await capture(Path.Combine(output, "recording-controls.png"), _bar.CaptureRoot);
            TogglePause();
            await Task.Delay(300);
            RequireRecording(_recorder.State == ScreenRecordingState.Paused && PauseButton.Content.ToString() == "继续", "Real UI pause.");
            TogglePause(); await Task.Delay(1000);
            await StopAndSaveAsync();
            RequireRecording(!IsBusy && OpenButton.IsEnabled && _lastOutput is not null && File.Exists(_lastOutput), "UI stop saves playable output and restores controls.");
            return _lastOutput!;
        }
        finally { animation.Stop(); if (IsBusy) await StopAndSaveAsync(); fixture.Close(); }
    }
    private static void RequireRecording(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
