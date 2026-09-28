using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Shunshou.App;

public sealed partial class MainWindow
{
    // Release-scoped checks: navigation, rendered themes and the two new video workspaces only.
    private async Task<int> VerifyV040Async(string output, bool full)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        try
        {
            var args = Environment.GetCommandLineArgs();
            int fixtureIndex = Array.IndexOf(args, "--video-fixture");
            string? fixture = fixtureIndex >= 0 && fixtureIndex + 1 < args.Length ? args[fixtureIndex + 1] : null;
            if (args.Contains("--close-running-ai"))
            {
                RequireUi(fixture is not null, "Shutdown verification requires a generated video fixture.");
                Navigation.SelectedItem = Navigation.MenuItems[4];
                OperationBox.SelectedItem = "视频水印处理";
                await VideoTools.PrepareCloseWhileAiAsync(fixture!, output);
                return 0; // The normal Closed handler cancels the active work. The external harness checks all child PIDs.
            }
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
            {
                RootLayout.RequestedTheme = theme;
                Navigation.SelectedItem = Navigation.MenuItems[0];
                await Task.Delay(850);
                var expected = theme == ElementTheme.Light ? "FFFAFAF7" : "FF191A18";
                var workspace = ((SolidColorBrush)RootLayout.Background).Color;
                RequireUi($"{workspace.A:X2}{workspace.R:X2}{workspace.G:X2}{workspace.B:X2}" == expected, "Workspace brush did not follow the active theme.");
                RequireUi(GeneralArea.Visibility == Visibility.Visible && VideoTools.Visibility == Visibility.Collapsed, "Existing page layout changed unexpectedly.");
                await SaveScreenshot(Path.Combine(output, "compression-" + theme.ToString().ToLowerInvariant() + ".png"));
                checks.Add($"{theme}: rendered compression page captured after transitions; workspace brush matched.");
                Navigation.SelectedItem = Navigation.MenuItems[4];
                OperationBox.SelectedItem = "链接下载视频";
                RequireUi(VideoTools.Visibility == Visibility.Visible && GeneralArea.Visibility == Visibility.Collapsed && RunFooter.Visibility == Visibility.Collapsed, "Download workspace/footer overlap.");
                RequireUi(!AcceptInputPaths(["C:\\not-a-real-video.mp4"]), "Link download accepted a file drop.");
                await Task.Delay(200);
                await SaveScreenshot(Path.Combine(output, "download-" + theme.ToString().ToLowerInvariant() + ".png"));
                OperationBox.SelectedItem = "视频水印处理";
                RequireUi(VideoTools.Visibility == Visibility.Visible && OperationDescription.Text.Contains("框选"), "Watermark navigation failed.");
                if (fixture is not null) await VideoTools.VerifyLoadAsync(fixture);
                VideoTools.VerifySelectEngine(0);
                await Task.Delay(200);
                await SaveScreenshot(Path.Combine(output, "watermark-" + theme.ToString().ToLowerInvariant() + ".png"));
                VideoTools.VerifySelectEngine(1);
                await Task.Delay(100);
                await SaveScreenshot(Path.Combine(output, "ai-" + theme.ToString().ToLowerInvariant() + ".png"));
                VideoTools.VerifySelectEngine(2);
                VideoTools.VerifySelectEngine(0);
                var about = ComponentAboutDialog.Create(RootLayout.XamlRoot);
                about.RequestedTheme = theme;
                var aboutTask = about.ShowAsync();
                await Task.Delay(350);
                await SaveElementScreenshot(Path.Combine(output, "about-" + theme.ToString().ToLowerInvariant() + ".png"), about);
                if (about.Content is ScrollViewer { Content: StackPanel aboutContent })
                {
                    var group = aboutContent.Children.OfType<Expander>().FirstOrDefault(e => e.Header?.ToString() == "AI 视频修补")
                        ?? aboutContent.Children.OfType<Expander>().FirstOrDefault();
                    if (group is not null)
                    {
                        group.IsExpanded = true;
                        group.StartBringIntoView();
                        await Task.Delay(250);
                        await SaveElementScreenshot(Path.Combine(output, "about-expanded-" + theme.ToString().ToLowerInvariant() + ".png"), about);
                    }
                }
                about.Hide();
                await aboutTask;
            }
            checks.Add("New media operation navigation, isolated footer, file-drop routing and loaded frame geometry.");
            if (full)
            {
                RequireUi(fixture is not null, "Full video UI checks require --video-fixture.");
                await VideoTools.VerifyWorkflowAsync(output);
                checks.Add("Video UI: 3-second preview playback opened, full export completed, invalid URL feedback and state recovery.");
                var shopResult = await VerifyShopAsync(Path.Combine(output, "shop"));
                RequireUi(shopResult == 0, "Shop/theme targeted regression failed.");
                checks.Add("Shop resource/URL integrity, compact QR layout, failure handling and theme toggle passed.");
            }
            int aiFixtureIndex = Array.IndexOf(args, "--ai-fixture");
            if (aiFixtureIndex >= 0 && aiFixtureIndex + 1 < args.Length)
            {
                Navigation.SelectedItem = Navigation.MenuItems[4];
                OperationBox.SelectedItem = "视频水印处理";
                await VideoTools.VerifyAiWorkflowAsync(args[aiFixtureIndex + 1], Path.Combine(output, "ai"));
                checks.Add("Both AI engine UI routes produced real local previews and released busy state.");
            }
            await File.WriteAllTextAsync(Path.Combine(output, "ui-results.json"), JsonSerializer.Serialize(new
            { Passed = true, Scope = full ? "related-ui-full" : "micro", Checks = checks,
              NotCovered = "No old conversion/uninstall/recovery regression; no live system High Contrast session or clean-PC test." }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "ui-results.json"), JsonSerializer.Serialize(new { Passed = false, Checks = checks, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            try { await SaveScreenshot(Path.Combine(output, "failure.png")); } catch { }
            return 1;
        }
    }
}
