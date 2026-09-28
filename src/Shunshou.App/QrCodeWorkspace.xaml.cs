using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Shunshou.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace Shunshou.App;

/// <summary>Creates and reads QR codes locally: no online service, no account, images stay on this computer.</summary>
public sealed partial class QrCodeWorkspace : UserControl
{
    private static readonly string TemporaryDirectory = Path.Combine(Path.GetTempPath(), "Shunshou", "qr");

    public QrCodeWorkspace()
    {
        InitializeComponent();
        LevelBox.ItemsSource = QrCodeService.ErrorLevels;
        LevelBox.SelectedIndex = 1;
        // Initial values are applied here so the markup only declares the allowed range.
        SizeBox.Value = 512;
        MarginBox.Value = 4;
    }

    public nint HostWindowHandle { get; set; }

    internal string LastGeneratedPath { get; private set; } = "";
    internal string DecodedText => ResultBox.Text;
    internal string LastGenerateMessage => GenerateStatus.Text;

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Notice.IsOpen = false;
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, SuggestedFileName = "二维码" };
            picker.FileTypeChoices.Add("PNG 图片", [".png"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, HostWindowHandle);
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            string path = QrCodeService.Generate(ContentBox.Text, (int)SizeBox.Value, LevelBox.SelectedItem as string ?? "M 约 15%",
                (int)MarginBox.Value, file.Path);
            LastGeneratedPath = path;
            GeneratedPreview.Source = await ImagePreview.FromFileAsync(file.Path, 360);
            GeneratedHint.Visibility = Visibility.Collapsed;
            GenerateStatus.Text = "已保存 · " + file.Name;
        }
        catch (Exception ex)
        {
            Notice.Title = "无法生成二维码";
            Notice.Message = ex.Message;
            Notice.IsOpen = true;
        }
    }

    private async void Pick_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            foreach (string extension in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif", ".tif", ".tiff" }) picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, HostWindowHandle);
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            await ShowAndDecodeAsync(file.Path);
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Bitmap)) { ShowError("剪贴板里没有图片。请先截图或复制二维码图片。"); return; }
            Directory.CreateDirectory(TemporaryDirectory);
            string path = Path.Combine(TemporaryDirectory, "qr-" + Guid.NewGuid().ToString("N") + ".png");
            var reference = await content.GetBitmapAsync();
            using var source = await reference.OpenReadAsync();
            await using (var target = File.Create(path)) await source.AsStreamForRead().CopyToAsync(target);
            await ShowAndDecodeAsync(path);
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    internal async Task ShowAndDecodeAsync(string path)
    {
        SourcePreview.Source = await ImagePreview.FromFileAsync(path, 360);
        SourceHint.Visibility = Visibility.Collapsed;
        Notice.IsOpen = false;
        try
        {
            var result = QrCodeService.Decode(path);
            ResultBox.Text = result.Text;
            CopyResultButton.IsEnabled = true;
            DecodeStatus.Text = "已识别 · " + result.Format;
            DetailText.Text = $"图片来源：{Path.GetFileName(path)} · {result.Width}×{result.Height}";
        }
        catch (Exception ex)
        {
            ResultBox.Text = string.Empty;
            CopyResultButton.IsEnabled = false;
            DecodeStatus.Text = "没有识别到内容。";
            DetailText.Text = "";
            ShowError(ex.Message);
        }
    }

    private void CopyResult_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ResultBox.Text)) return;
        try
        {
            var package = new DataPackage();
            package.SetText(ResultBox.Text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            DecodeStatus.Text = "已复制识别内容。";
        }
        catch (Exception) { ShowError("系统暂时拒绝了剪贴板写入，请稍后再试。"); }
    }

    private void ShowError(string message)
    {
        Notice.Title = "二维码";
        Notice.Message = message;
        Notice.Severity = InfoBarSeverity.Warning;
        Notice.IsOpen = true;
    }
}
