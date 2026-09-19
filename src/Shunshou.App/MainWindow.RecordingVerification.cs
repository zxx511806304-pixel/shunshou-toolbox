using System.Text.Json;
using Microsoft.UI.Xaml;

namespace Shunshou.App;

public sealed partial class MainWindow
{
    private async Task<int> VerifyRecordingUiAsync(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        try
        {
            Navigation.SelectedItem = Navigation.MenuItems[3];
            OperationBox.SelectedItem = "屏幕录制";
            await RecordingTools.VerifyControlsAsync(Path.Combine(output, "cancelled-countdown"));
            RequireUi(!AcceptInputPaths([Path.Combine(output, "dummy.mp4")]), "Recording workspace must reject file drops.");
            int index = 0;
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
            {
                RootLayout.RequestedTheme = theme;
                await Task.Delay(650);
                await SaveScreenshot(Path.Combine(output, $"recording-{++index}-{theme}.png"));
            }
            RecordingTools.AdvancedShown = true;
            await Task.Delay(350);
            RecordingTools.ScrollAdvancedIntoView();
            await Task.Delay(250);
            await SaveScreenshot(Path.Combine(output, "recording-advanced.png"));
            RecordingTools.AdvancedShown = false;
            RecordingTools.ScrollToStart();
            string video = await RecordingTools.VerifyActualRecordingAsync(Path.Combine(output, "generated-recording"), SaveElementScreenshot);
            RequireUi(!_busy && Navigation.IsEnabled, "Host busy state resets after recording.");
            await File.WriteAllTextAsync(Path.Combine(output, "ui-results.json"), JsonSerializer.Serialize(new
            {
                Passed = true, Video = video, Checks = new[] { "No capture on device discovery", "Presets, custom FPS/bitrate/resolution and invalid input", "Countdown cancellation", "Light-Dark-Light rendered states", "Native UI start/pause/resume/stop saves video", "Floating controls", "File-drop rejection" },
                NotCovered = "Real microphone capture, multi-monitor mixed DPI and all GPU models."
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "ui-results.json"), JsonSerializer.Serialize(new { Passed = false, Error = ex.ToString() }));
            return 1;
        }
    }
}
