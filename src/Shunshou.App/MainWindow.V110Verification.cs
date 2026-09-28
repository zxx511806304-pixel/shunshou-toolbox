using System.Text.Json;
using Microsoft.UI.Xaml;
using Shunshou.Core;
using Shunshou.DesktopIntegration;
using Windows.ApplicationModel.DataTransfer;

namespace Shunshou.App;

public sealed partial class MainWindow
{
    /// <summary>
    /// Release check for 1.1.0: the special-character library, the optional start-up entry, and the
    /// linked-article text reader. Every step uses the real window, the real clipboard and the real registry.
    /// </summary>
    private async Task<int> VerifyV110Async(string output, string? sitesFile)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        var report = new Dictionary<string, object?>();
        try
        {
            NavigateToTool("text", "特殊字符库");
            await WaitForUiAsync(() => CharacterLibrary.VisibleCharacterCount > 0, "字符库没有渲染出来");
            RequireUi(Operation == "特殊字符库" && CharacterLibrary.Visibility == Visibility.Visible, "字符库页面没有打开");
            RequireUi(GeneralArea.Visibility == Visibility.Collapsed && RunFooter.Visibility == Visibility.Collapsed && OperationCard.Visibility == Visibility.Visible,
                "字符库页面仍显示文件列表或开始按钮");
            var (groups, characters) = CharacterLibrary.LibrarySize;
            RequireUi(groups == 22 && characters == 557, $"随包字符库数据不完整：{groups} 组 / {characters} 个");
            RequireUi(CharacterLibrary.VisibleGroupCount == 22 && CharacterLibrary.VisibleCharacterCount == 557, "字符库没有显示全部 22 组 557 个字符");
            report["CharacterLibrary"] = new { Groups = groups, Characters = characters, Summary = CharacterLibrary.CountSummary };
            checks.Add("字符库：随包 22 组 557 个字符全部渲染到界面");

            int wideColumns = CharacterLibrary.FirstGroupColumns;
            RequireUi(CharacterLibrary.InvokeTile("✓"), "没有找到 ✓ 按钮");
            await Task.Delay(400);
            string? clipboard = await ReadClipboardTextAsync();
            RequireUi(clipboard == "✓", "点击字符后剪贴板内容不是该字符：" + clipboard);
            RequireUi(CharacterLibrary.CurrentCharacter == "✓" && CharacterLibrary.StatusMessage.Contains("勾号") && CharacterLibrary.StatusMessage.Contains("U+2713"),
                "点击后的当前字符与状态提示不正确：" + CharacterLibrary.StatusMessage);
            checks.Add("字符库：点击 ✓ 真实写入剪贴板，并显示名称与 U+2713 编码");

            CharacterLibrary.InvokeTile("✿");
            await Task.Delay(300);
            RequireUi(await ReadClipboardTextAsync() == "✿" && CharacterLibrary.CurrentCharacter == "✿", "连续点击两个字符时后者没有覆盖剪贴板");
            checks.Add("字符库：连续点击不同字符时剪贴板跟随最后一次点击");

            SearchCharacters("箭头");
            await Task.Delay(250);
            RequireUi(CharacterLibrary.VisibleGroupCount == 2 && CharacterLibrary.IsTileVisible("→") && !CharacterLibrary.IsTileVisible("℃"),
                "按组名查找没有过滤出箭头分组：" + CharacterLibrary.CountSummary);
            RequireUi(CharacterLibrary.GroupHeadings.Contains("常用箭头") && CharacterLibrary.GroupHeadings.Contains("扩展箭头"), "查找结果缺少箭头分组");
            SearchCharacters("2713");
            await Task.Delay(250);
            RequireUi(CharacterLibrary.IsTileVisible("✓") && !CharacterLibrary.IsTileVisible("→"), "按编码查找没有命中 U+2713");
            SearchCharacters("不存在的字符");
            await Task.Delay(250);
            RequireUi(CharacterLibrary.VisibleCharacterCount == 0 && CharacterLibrary.CountSummary.Length == 0, "无结果时仍然显示字符");
            ClearCharacterSearch();
            await WaitForUiAsync(() => CharacterLibrary.VisibleCharacterCount == 557, "清除查找后没有恢复全部字符");
            report["CharacterSearch"] = new { ByGroup = "箭头", ByCodePoint = "2713", Empty = true };
            checks.Add("字符库：按名称、组名与 U+ 编码查找，空结果与“显示全部”都正常");

            var windowSize = AppWindow.Size;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(960, windowSize.Height));
            await Task.Delay(700);
            int narrowColumns = CharacterLibrary.FirstGroupColumns;
            AppWindow.Resize(windowSize);
            await Task.Delay(700);
            int restoredColumns = CharacterLibrary.FirstGroupColumns;
            RequireUi(narrowColumns < restoredColumns && narrowColumns >= 4, $"窗口变窄后字符没有自动换行：{narrowColumns} / {restoredColumns}");
            report["CharacterLayout"] = new { Narrow = narrowColumns, Restored = restoredColumns };
            checks.Add($"字符库：窗口宽度变化后自动重排（{narrowColumns} → {restoredColumns} 列）");

            int capture = 0;
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
            {
                RootLayout.RequestedTheme = theme;
                await Task.Delay(650);
                await SaveScreenshot(Path.Combine(output, $"characters-{++capture}-{theme}.png"));
            }
            RootLayout.RequestedTheme = ElementTheme.Light;
            checks.Add("字符库：浅色 / 深色 / 回到浅色的实际渲染截图");

            var startupChecks = await VerifyStartupEntryAsync(output);
            report["Startup"] = startupChecks.Details;
            checks.Add(startupChecks.Message);

            NavigateToTool("text", "链接提取文字");
            await WaitForUiAsync(() => WebPdfTools.Visibility == Visibility.Visible, "链接提取文字页面没有打开");
            var fixture = await WebPdfTools.VerifyTextFixtureAsync(Path.Combine(output, "link-text"));
            RequireUi(WebPdfTools.Visibility == Visibility.Visible, "链接提取文字页面在提取后消失");
            capture = 0;
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
            {
                RootLayout.RequestedTheme = theme;
                await Task.Delay(650);
                await SaveScreenshot(Path.Combine(output, $"link-text-{++capture}-{theme}.png"));
            }
            RootLayout.RequestedTheme = ElementTheme.Light;
            report["LinkText"] = fixture;
            checks.Add("链接提取文字：本地样例页提取段落、排除隐藏文本、写入 TXT 并带上来源");

            var live = new List<object>();
            if (sitesFile is not null && File.Exists(sitesFile))
            {
                using var input = JsonDocument.Parse(await File.ReadAllTextAsync(sitesFile));
                foreach (var site in input.RootElement.EnumerateArray())
                {
                    string name = site.TryGetProperty("name", out var label) ? label.GetString() ?? "" : "";
                    var result = await WebPdfTools.ProbeLivePageAsync(site.GetProperty("url").GetString()!, site.GetProperty("video").GetBoolean());
                    live.Add(new { Site = name, Result = result });
                    await File.WriteAllTextAsync(Path.Combine(output, "live-sites.json"), JsonSerializer.Serialize(live, new JsonSerializerOptions { WriteIndented = true }));
                }
                checks.Add($"链接提取文字：真实网站抽查 {live.Count} 个（结果见 live-sites.json）");
            }
            report["OnlineSites"] = live;

            await File.WriteAllTextAsync(Path.Combine(output, "ui-results.json"), JsonSerializer.Serialize(new
            {
                Passed = true,
                Version = "1.1.0",
                Checks = checks,
                Details = report,
                NotCovered = "Clean Windows 10/11 machine, High Contrast rendering, and websites that need an account or paid access."
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "ui-results.json"), JsonSerializer.Serialize(new
            { Passed = false, Version = "1.1.0", Checks = checks, Details = report, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            try { await SaveScreenshot(Path.Combine(output, "failure.png")); } catch { }
            return 1;
        }
    }

    private void SearchCharacters(string query)
    {
        CharacterLibrary.ApplySearch(query);
    }

    private void ClearCharacterSearch() => CharacterLibrary.ApplySearch(null);

    /// <summary>Writes and removes the real current-user start-up entry, then restores whatever was there before.</summary>
    private async Task<(string Message, object Details)> VerifyStartupEntryAsync(string output)
    {
        var store = new UserRunStartupStore();
        string? original = store.Read(StartupRegistration.ValueName);
        var details = new Dictionary<string, object?>();
        // Start from a clean state even if an earlier check left an entry behind; the original value is restored below.
        if (original is not null && StartupRegistration.TryGetOwnedExecutablePath(original, out string ownedPath))
        {
            details["PreviousEntry"] = original;
            if (!File.Exists(ownedPath)) details["PreviousExecutableMissing"] = true;
            // A value written from a development build host is never restored; only the packaged entry point is.
            if (string.Equals(Path.GetFileName(ownedPath), "Shunshou.App.exe", StringComparison.OrdinalIgnoreCase)) original = null;
            store.Delete(StartupRegistration.ValueName);
        }
        try
        {
            // Drive the real menu item, exactly as a click on “开机自启动” does.
            StartupMenuItem.IsChecked = true;
            StartupToggle_Click(StartupMenuItem, new RoutedEventArgs());
            await Task.Delay(300);
            string expected = StartupRegistration.ExpectedValue(AppPaths.LauncherPath);
            string? written = store.Read(StartupRegistration.ValueName);
            RequireUi(written == expected, "点击菜单后启动项内容不正确：" + written);
            RequireUi(StartupMenuItem.IsChecked && StartupMenuItem.IsEnabled, "菜单勾选状态没有同步");
            details["Written"] = written;
            details["MenuChecked"] = true;
            RootLayout.RequestedTheme = ElementTheme.Light;
            await Task.Delay(400);
            await SaveScreenshot(Path.Combine(output, "startup-enabled-Light.png"));
            RootLayout.RequestedTheme = ElementTheme.Dark;
            await Task.Delay(400);
            await SaveScreenshot(Path.Combine(output, "startup-enabled-Dark.png"));
            StartupMenuItem.IsChecked = false;
            StartupToggle_Click(StartupMenuItem, new RoutedEventArgs());
            await Task.Delay(300);
            RequireUi(store.Read(StartupRegistration.ValueName) is null && !StartupMenuItem.IsChecked, "关闭后仍残留启动项或勾选状态");
            details["RemovedAfterDisable"] = true;
        }
        finally
        {
            if (original is null) SetStartupEnabled(false);
            else
            {
                store.Write(StartupRegistration.ValueName, original);
                RefreshStartupMenu();
            }
            details["RestoredTo"] = original is null ? "未开启（原状态）" : original;
            details["Launcher"] = AppPaths.LauncherPath;
        }
        await File.WriteAllTextAsync(Path.Combine(output, "startup-entry.json"), JsonSerializer.Serialize(details, new JsonSerializerOptions { WriteIndented = true }));
        return ("开机自启动：左侧开关真实写入当前用户启动项（含 --startup 参数），关闭后不残留并恢复原状态", details);
    }

    private static async Task<string?> ReadClipboardTextAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            return content.Contains(StandardDataFormats.Text) ? await content.GetTextAsync() : null;
        }
        catch (Exception) { return null; }
    }
}
