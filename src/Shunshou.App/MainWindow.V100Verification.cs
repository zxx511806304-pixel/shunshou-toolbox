using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Shunshou.App;

public sealed partial class MainWindow
{
    private async Task<int> VerifyV100Async(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        try
        {
            RequireUi(typeof(App).Assembly.GetName().Version?.ToString(3) == "1.3.0", "Unexpected release assembly version.");
            RootLayout.RequestedTheme = ElementTheme.Light;
            Navigation.SelectedItem = Navigation.MenuItems[1];
            OperationBox.SelectedItem = "网页转 PDF";
            await Task.Delay(500);
            RequireUi(WebPdfTools.Visibility == Visibility.Visible && GeneralArea.Visibility == Visibility.Collapsed && RunFooter.Visibility == Visibility.Collapsed, "Web PDF workspace overlap.");
            RequireUi(ConnectionStatusText.Text == "需联网" && !AcceptInputPaths(["C:\\fixture.pdf"]), "Web PDF connectivity/drop routing.");
            var web = await WebPdfTools.VerifyWorkflowAsync(Path.Combine(output, "web-pdf"));
            checks.Add("Actual WebView2 navigation and three-page selectable-text PDF, HTTP error, cancellation and non-overwrite output.");
            Navigation.SelectedItem = Navigation.MenuItems[4];
            OperationBox.SelectedItem = "下载视频字幕";
            RequireUi(SubtitleTools.Visibility == Visibility.Visible && VideoTools.Visibility == Visibility.Collapsed && RunFooter.Visibility == Visibility.Collapsed, "Subtitle workspace overlap.");
            RequireUi(ConnectionStatusText.Text == "需联网" && !AcceptInputPaths(["C:\\fixture.mp4"]), "Subtitle connectivity/drop routing.");
            var subtitles = await SubtitleTools.VerifyWorkflowAsync(Path.Combine(output, "subtitles"));
            checks.Add("Subtitle UI analyzed a real local HTTP page and downloaded a text file through packaged yt-dlp.");
            int capture = 0;
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
            {
                RootLayout.RequestedTheme = theme;
                await Task.Delay(650);
                string name = (++capture).ToString() + "-" + theme.ToString().ToLowerInvariant();
                var expected = theme == ElementTheme.Light ? "FFFAFAF7" : "FF191A18";
                var color = ((SolidColorBrush)RootLayout.Background).Color;
                RequireUi($"{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}" == expected, "Workspace theme resource did not update.");
                await SaveScreenshot(Path.Combine(output, "subtitles-" + name + ".png"));
                Navigation.SelectedItem = Navigation.MenuItems[1];
                OperationBox.SelectedItem = "网页转 PDF";
                await Task.Delay(250);
                await SaveScreenshot(Path.Combine(output, "web-pdf-" + name + ".png"));
                Navigation.SelectedItem = Navigation.MenuItems[4];
                OperationBox.SelectedItem = "链接下载视频";
                RequireUi(ConnectionStatusText.Text == "需联网", "Video download falsely claims offline.");
                OperationBox.SelectedItem = "视频水印处理";
                RequireUi(ConnectionStatusText.Text == "本地处理 · 离线可用", "Local AI lost offline label.");
                OperationBox.SelectedItem = "下载视频字幕";
            }
            Navigation.SelectedItem = Navigation.MenuItems[0];
            RequireUi(ConnectionStatusText.Text == "本地处理 · 离线可用", "Local conversion lost offline label.");
            RequireUi(GeneralArea.Visibility == Visibility.Visible && SubtitleTools.Visibility == Visibility.Collapsed && WebPdfTools.Visibility == Visibility.Collapsed, "Returning to local workspace failed.");
            checks.Add("Light → Dark → Light resource and rendered captures; mutually exclusive workspaces; online/local status follows selection.");
            var catalog = ComponentCatalog.Load();
            RequireUi(catalog.Components.Any(entry => entry.Id == "webview2") && catalog.Components.Any(entry => entry.Id == "yt-dlp" && entry.Purpose.Contains("字幕")), "New feature sources missing from About.");
            await File.WriteAllTextAsync(Path.Combine(output, "ui-results.json"), JsonSerializer.Serialize(new
            {
                Passed = true, Version = "1.3.0", Checks = checks, WebPdf = web, Subtitles = subtitles,
                NotCovered = "No unrelated feature regression; no system High Contrast or clean-PC session. External sites can change availability."
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "ui-results.json"), JsonSerializer.Serialize(new
            { Passed = false, Checks = checks, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            try { await SaveScreenshot(Path.Combine(output, "failure.png")); } catch { }
            return 1;
        }
    }
}
