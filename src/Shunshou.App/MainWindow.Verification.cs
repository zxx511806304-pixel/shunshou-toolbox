using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Shunshou.App;

public sealed partial class MainWindow
{
    // Explicit developer switch: all fixtures and screenshots stay below the supplied output folder.
    private async Task<int> VerifyUiAsync(string fixtures, string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        try
        {
            RootLayout.RequestedTheme = ElementTheme.Light;
            await Task.Delay(600);
            RequireUi(OutputDirectory.Text == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "ShunshouToolbox", "Output"), "Default output path is not the approved English directory");
            await SaveScreenshot(Path.Combine(output, "home-branding.png"));
            checks.Add("English default output directory and initial window branding");
            string previewFixtures = Path.Combine(output, "generated-previews");
            checks.AddRange(await SearchPreviewVerification.RunAsync(DispatcherQueue, previewFixtures));
            string source = Path.Combine(output, "search-source");
            Directory.CreateDirectory(source);
            string landscape = Path.Combine(source, "资料-横图.png");
            string portrait = Path.Combine(source, "资料-竖图.png");
            string webp = Path.Combine(source, "资料-WebP.webp");
            File.Copy(Path.Combine(previewFixtures, "preview-landscape.png"), landscape, true);
            File.Copy(Path.Combine(previewFixtures, "preview-portrait.png"), portrait, true);
            File.Copy(Path.Combine(previewFixtures, "preview-landscape.webp"), webp, true);
            await File.WriteAllTextAsync(Path.Combine(source, "资料-清单.txt"), "Only generated fixture files are used.");
            Directory.CreateDirectory(Path.Combine(source, "资料-文件夹"));

            Navigation.SelectedItem = Navigation.MenuItems[2];
            SelectCategory("image");
            RequireUi(RootLayout.AllowDrop, "Window does not accept file drops");
            RequireUi(AcceptInputPaths([landscape, webp]) && _inputs.Count == 2, "Batch input was not reflected in the real UI");
            RequireUi(!AcceptInputPaths([Path.Combine(Path.GetFullPath(fixtures), "zh-en.pdf")]) && _inputs.Count == 2,
                "Wrong input type replaced the accepted image selection");
            StatusInfo.IsOpen = false;
            ShowFileDropOverlay(true);
            await Task.Delay(180);
            RequireUi(_fileDropOverlay?.Visibility == Visibility.Visible, "Drag overlay was not visible");
            await SaveScreenshot(Path.Combine(output, "drag-overlay.png"));
            ShowFileDropOverlay(false);
            await SaveScreenshot(Path.Combine(output, "input-selection.png"));
            checks.Add("Window drop registration, visible overlay and shared picker/drop input acceptance preserve valid multi-selection");

            RequireUi(OperationBox.Visibility == Visibility.Collapsed && OperationButtons.Children.Count == 2,
                "Image tools are still hidden in a dropdown");
            OperationBox.SelectedIndex = 1;
            await _inputSyncTask;
            RequireUi(_inputs.Count == 2 && OcrEditor.InputPath == landscape && OcrArea.Visibility == Visibility.Visible,
                "Switching to OCR lost the image batch or active image");
            OcrInputList.SelectPath(webp);
            await _inputSyncTask;
            RequireUi(OcrEditor.InputPath == webp && _inputs.Count == 2, "OCR image list selection did not select the recognized image");
            OperationBox.SelectedIndex = 0;
            RequireUi(_inputs.Count == 2 && InputList.SelectedPath == webp, "Returning to image conversion lost selected files");
            InputList_RemoveRequested(InputList, new InputPathEventArgs(landscape));
            RequireUi(_inputs.Count == 1 && File.Exists(landscape), "Removing a queued item changed the source file");
            OperationBox.SelectedIndex = 1;
            await _inputSyncTask;
            checks.Add("Visible tool buttons, shared OCR/image-conversion batch, active-image selection and file-only queue removal");
            checks.AddRange(await OcrWorkspaceVerification.RunAsync(OcrEditor,
                Path.Combine(Path.GetFullPath(fixtures), "ocr-zh-en.png"), Path.Combine(output, "ocr"), SaveScreenshot));
            RootLayout.RequestedTheme = ElementTheme.Dark;
            await Task.Delay(200);
            await SaveScreenshot(Path.Combine(output, "ocr-dark.png"));
            RootLayout.RequestedTheme = ElementTheme.Light;

            Navigation.SelectedItem = Navigation.MenuItems[4];
            SelectCategory("files");
            RequireUi(SearchScopeBox.SelectedItem is SearchScope { Root: null, IsFolder: false }, "Default search scope is not all local drives");
            RequireUi(AcceptInputPaths([landscape]) && SearchQuery.Text == Path.GetFileName(landscape)
                && _selectedSearchFolder == source, "File input did not choose its containing search folder and name");
            RequireUi(AcceptInputPaths([source]), "Folder input was rejected for search");
            SearchQuery.Text = "资料";
            ImagesOnly.IsChecked = false;
            await RunSearchAsync();
            RequireUi(_searchRows.Count == 5 && !_busy && !TaskProgress.IsIndeterminate, "Search results or final UI state are incorrect");
            SearchResults.UpdateLayout();
            var landscapeItem = _searchRows.Single(x => x.FullPath == landscape);
            SearchResults.SelectedItem = landscapeItem;
            await WaitForUiAsync(() => !PreviewLoading.IsActive && SelectedPreview.Source is BitmapImage, "Selected image preview did not finish");
            await WaitForUiAsync(() => _searchRows.Where(x => x.FullPath.EndsWith(".png") || x.FullPath.EndsWith(".webp")).All(x => x.Thumbnail != null),
                "Visible image thumbnails were not loaded");
            RequireUi(PreviewPath.Text == landscape && SelectedPreview.Source is BitmapImage { PixelWidth: > 0 }, "Selected preview is not connected to its result");
            await SaveScreenshot(Path.Combine(output, "search-results.png"));
            checks.Add("All-drive default, dropped file/folder search scope, actual generated-file search, visible thumbnails and selected preview");

            SearchResults.SelectedItem = _searchRows.Single(x => x.FullPath == webp);
            SearchResults.SelectedItem = _searchRows.Single(x => x.FullPath == portrait);
            await WaitForUiAsync(() => !PreviewLoading.IsActive && SelectedPreview.Source is BitmapImage, "Changed selection did not finish");
            RequireUi(PreviewPath.Text == portrait && SelectedPreview.Source is BitmapImage image && image.PixelHeight > image.PixelWidth,
                "Late preview completion overwrote the latest selection");
            ImagesOnly.IsChecked = true;
            await RunSearchAsync();
            RequireUi(_searchRows.Count == 3 && _searchRows.All(x => !x.IsDirectory), "Image filter retained non-images");
            SearchResults.SelectedItem = _searchRows.Single(x => x.FullPath == portrait);
            await WaitForUiAsync(() => !PreviewLoading.IsActive && SelectedPreview.Source is BitmapImage, "Filtered result preview failed");
            RootLayout.RequestedTheme = ElementTheme.Dark;
            await Task.Delay(200);
            await SaveScreenshot(Path.Combine(output, "search-results-dark.png"));
            checks.Add("Selection cancellation, image-only results and dark-theme result rendering");
            SearchResults.SelectedIndex = -1;
            RequireUi(SelectedPreview.Source == null && !OpenSearchItemButton.IsEnabled && !RevealSearchItemButton.IsEnabled
                && !PreviewLoading.IsActive, "Clearing selection retained the old preview controls");
            checks.Add("Clearing result selection clears preview and disables file actions");

            var scrollFixtures = Path.Combine(output, "scroll-source");
            Directory.CreateDirectory(scrollFixtures);
            for (var i = 0; i < 160; i++) File.Copy(landscape, Path.Combine(scrollFixtures, $"滚动-{i:D3}.png"), true);
            SelectSearchFolder(scrollFixtures, "滚动");
            await RunSearchAsync();
            RequireUi(_searchRows.Count == 160, "Scroll fixture count is incorrect");
            foreach (int index in new[] { 0, 40, 80, 120, 159 })
            {
                SearchResults.ScrollIntoView(_searchRows[index]);
                SearchResults.UpdateLayout();
                await WaitForUiAsync(() => _searchRows[index].Thumbnail != null, "Visible recycled row did not receive its thumbnail");
                await Task.Delay(100);
                RequireUi(_searchRows.Count(x => x.Thumbnail != null) <= 32, "Offscreen decoded thumbnails accumulated while scrolling");
            }
            checks.Add("160-image virtualized list: scrolling releases offscreen decoded thumbnails and recycled rows load correct previews");
            RootLayout.RequestedTheme = ElementTheme.Light;
            Navigation.SelectedItem = Navigation.MenuItems[5];
            await Uninstaller.LoadAsync();
            await Uninstaller.VerifyFixtureAsync(Path.Combine(output, "software-fixtures"));
            await Task.Delay(200);
            await SaveScreenshot(Path.Combine(output, "software.png"));
            RootLayout.RequestedTheme = ElementTheme.Dark;
            await Task.Delay(200);
            await SaveScreenshot(Path.Combine(output, "software-dark.png"));
            checks.Add("Uninstaller real UI: search/details, absent uninstaller, retained removed-app snapshot, cancel/confirm fake uninstall, selected-only fixture cleanup and hash-verified backup restore; no real uninstall or machine-registry writes");
            await File.WriteAllTextAsync(Path.Combine(output, "ui-verification.json"), JsonSerializer.Serialize(new
                { Passed = true, BaseDirectory = AppContext.BaseDirectory, Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "ui-verification.json"), JsonSerializer.Serialize(new
                { Passed = false, Checks = checks, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            App.LogException(ex, "ui-verification");
            return 1;
        }
    }

    private static void RequireUi(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task WaitForUiAsync(Func<bool> ready, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(80);
        RequireUi(ready(), message);
    }
}
