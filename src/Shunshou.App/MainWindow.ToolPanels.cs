using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Shunshou.Core;

namespace Shunshou.App;

public sealed partial class MainWindow
{
    private static readonly string CroppedPreviewDirectory = Path.Combine(Path.GetTempPath(), "Shunshou", "crop-preview");

    internal CropOptions CurrentCropOptions() => new(
        CropKindBox.SelectedIndex == 1 ? CropKind.Pixels : CropKind.Ratio,
        CropRatioBox.SelectedItem as string ?? CropOptions.RatioNames[0],
        CropAnchorBox.SelectedItem as string ?? CropOptions.AnchorNames[0],
        (uint)Math.Max(16, double.IsFinite(CropWidthBox.Value) ? CropWidthBox.Value : 800),
        (uint)Math.Max(16, double.IsFinite(CropHeightBox.Value) ? CropHeightBox.Value : 800));

    private void Crop_Changed(object sender, SelectionChangedEventArgs e)
    {
        SetVisible(CropPixelPanel, CropKindBox.SelectedIndex == 1);
        CropPreviewCard.Visibility = Visibility.Collapsed;
    }

    private void Crop_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => CropPreviewCard.Visibility = Visibility.Collapsed;

    /// <summary>Shows what the current settings do to the first selected image; the export still covers every image.</summary>
    private async void CropPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string? source = _inputs.FirstOrDefault(File.Exists);
        if (source is null) { ShowError("请先添加要裁剪的图片。"); return; }
        CropPreviewButton.IsEnabled = false;
        CropPreviewHint.Text = "正在生成预览…";
        try
        {
            // Read the settings on the UI thread: touching XAML from the worker thread can deadlock.
            var options = CurrentCropOptions();
            Directory.CreateDirectory(CroppedPreviewDirectory);
            var files = await Task.Run(() => new ImageCropService().CropAsync([source], CroppedPreviewDirectory, options, 90, null, CancellationToken.None));
            CropPreviewImage.Source = await ImagePreview.FromFileAsync(files[0], 720);
            CropPreviewCard.Visibility = Visibility.Visible;
            var info = new FileInfo(files[0]);
            CropPreviewHint.Text = $"预览：{Path.GetFileName(source)} → {info.Name}（只预览这一张，导出时作用于全部图片）";
            try { File.Delete(files[0]); } catch (IOException) { }
        }
        catch (Exception ex)
        {
            CropPreviewCard.Visibility = Visibility.Collapsed;
            CropPreviewHint.Text = "预览失败。";
            ShowError(ex.Message);
        }
        finally { CropPreviewButton.IsEnabled = true; }
    }

    /// <summary>Accepts "00:01:20", "1:20", "80" or "80.5" as a position inside the media file.</summary>
    internal static bool TryReadClipTime(string? text, out double seconds, out string message)
    {
        bool ok = ClipTime.TryParse(text, out seconds);
        message = ok ? "" : "请按“时:分:秒”填写时间，例如 00:01:30，也可以直接填秒数。";
        return ok;
    }
}
