using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Shunshou.Core;

namespace Shunshou.App;

public sealed partial class MainWindow
{
    private async Task<int> VerifyV101Async(string fixtures, string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        var timings = new List<double>();
        try
        {
            string source = Path.Combine(output, "generated-search");
            Directory.CreateDirectory(source);
            string image = Path.Combine(source, "Shunshou101-图片.png");
            File.Copy(Path.Combine(fixtures, "ocr-zh-en.png"), image, true);
            await File.WriteAllTextAsync(Path.Combine(source, "Shunshou101-文字.txt"), "Generated search fixture only.");
            Directory.CreateDirectory(Path.Combine(source, "Shunshou101-文件夹"));
            Navigation.SelectedItem = Navigation.MenuItems[5];
            await RefreshSearchIndexAsync();
            RequireUi(_searchIndexConnected, "An existing real Everything index is required for this integration test; none was connected.");
            // Let the existing service receive our new file events before measuring warmed queries.
            var service = new EverythingSearchService();
            bool ready = false;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                var results = await service.SearchAsync([source], "Shunshou101-", false, null, default);
                if (results.Results.Count == 3) { ready = true; break; }
                await Task.Delay(250);
            }
            RequireUi(ready, "Generated files were not present in the real index.");
            SearchQuery.Text = "Shunshou101-";
            SearchScopeBox.SelectedIndex = 0;
            ImagesOnly.IsChecked = false;
            for (int i = 0; i < 3; i++)
            {
                var clock = Stopwatch.StartNew();
                await RunSearchAsync();
                timings.Add(clock.Elapsed.TotalMilliseconds);
                RequireUi(_searchRows.Count >= 3 && !_busy && !TaskProgress.IsIndeterminate, "Real indexed query or idle UI state failed.");
            }
            SelectSearchFolder(source, "Shunshou101-");
            await RunSearchAsync();
            RequireUi(_searchRows.Count == 3, "Indexed folder scope failed.");
            await File.WriteAllTextAsync(Path.Combine(output, "generated-path-check.json"), JsonSerializer.Serialize(new { Expected = image, Actual = _searchRows.Select(x => x.FullPath).ToArray() }));
            SearchResults.SelectedItem = _searchRows.Single(row => string.Equals(row.FullPath, image, StringComparison.OrdinalIgnoreCase));
            await WaitForUiAsync(() => !PreviewLoading.IsActive && SelectedPreview.Source is BitmapImage, "Image preview failed after indexed search.");
            checks.Add("Real Everything IPC: repeated all-drive name queries, folder scope, files/folders and preview.");
            int capture = 0;
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
            {
                RootLayout.RequestedTheme = theme;
                await Task.Delay(650);
                await SaveScreenshot(Path.Combine(output, $"search-{++capture}-{theme}.png"));
            }
            ImagesOnly.IsChecked = true;
            await RunSearchAsync();
            RequireUi(_searchRows.Count == 1 && string.Equals(_searchRows[0].FullPath, image, StringComparison.OrdinalIgnoreCase), "Index image-only filter failed.");
            checks.Add("Image filter through the default indexed-search action.");

            Navigation.SelectedItem = Navigation.MenuItems[1];
            OperationBox.SelectedIndex = 2;
            RequireUi(PdfOfficeMode.Visibility == Visibility.Visible && PdfOfficeMode.SelectedIndex == 0, "PDF layout must be the visible default.");
            OutputDirectory.Text = Path.Combine(output, "ui-pdf");
            RequireUi(AcceptInputPaths([Path.Combine(fixtures, "zh-en.pdf")]), "PDF fixture was rejected.");
            Run_Click(RunButton, new RoutedEventArgs());
            var deadline = Stopwatch.StartNew();
            while (_busy && deadline.Elapsed < TimeSpan.FromSeconds(45)) await Task.Delay(100);
            RequireUi(!_busy && Directory.Exists(OutputDirectory.Text) && Directory.GetFiles(OutputDirectory.Text, "*_layout.docx").Length > 0,
                "Actual UI PDF Word export did not finish: " + ResultText.Text + " / " + StatusInfo.Message);
            RequireUi(ResultText.Text.Contains("文字可编辑") && PdfOfficeMode.IsEnabled, "Conversion summary or control reset failed.");
            capture = 0;
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
            {
                RootLayout.RequestedTheme = theme;
                await Task.Delay(650);
                await SaveScreenshot(Path.Combine(output, $"pdf-{++capture}-{theme}.png"));
            }
            checks.Add("Actual UI PDF→DOCX uses layout default and displays conversion result; Light→Dark→Light captures.");
            await File.WriteAllTextAsync(Path.Combine(output, "ui-results.json"), JsonSerializer.Serialize(new
            { Passed = true, Version = "1.0.1", Checks = checks, IndexedAllDriveQueryMilliseconds = timings,
                NotCovered = "First-time UAC/index creation, clean PC, High Contrast and unrelated tools." }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "ui-results.json"), JsonSerializer.Serialize(new
            { Passed = false, Checks = checks, Error = ex.ToString(), IndexedAllDriveQueryMilliseconds = timings }, new JsonSerializerOptions { WriteIndented = true }));
            try { await SaveScreenshot(Path.Combine(output, "failure.png")); } catch { }
            return 1;
        }
    }
}
