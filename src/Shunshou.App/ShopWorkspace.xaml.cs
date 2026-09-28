using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Shunshou.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Shunshou.App;

public sealed partial class ShopWorkspace : UserControl
{
    private Task? _loadTask;
    private Uri? _simUri;
    private Uri? _membershipUri;
    private WriteableBitmap? _qrBitmap;
    private Flyout? _qrFlyout;
    private bool _openingLink;
    internal Func<Uri, Task<bool>>? LinkLauncherOverride { get; set; }
    internal bool IsReady => _qrBitmap is not null && _simUri is not null && _membershipUri is not null;
    internal bool QrIsOpen { get; private set; }
    internal bool HasError => ShopError.IsOpen;

    internal async Task SaveQrVerificationAsync(string path)
    {
        if (_qrBitmap is null) throw new InvalidOperationException("QR is not loaded.");
        using var file = File.Create(path);
        using var stream = file.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, 776, 776, 96, 96, _qrBitmap.PixelBuffer.ToArray());
        await encoder.FlushAsync();
    }

    public ShopWorkspace()
    {
        InitializeComponent();
        Unloaded += (_, _) => CloseQr();
    }

    public Task EnsureLoadedAsync() => _loadTask ??= LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            var data = await Task.Run(ShopResources.Load);
            using var input = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(input.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(data.QrImageBytes);
                await writer.StoreAsync();
            }
            input.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(input);
            if (decoder.PixelWidth != 776 || decoder.PixelHeight != 776)
                throw new InvalidDataException("Unexpected support image dimensions.");
            var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            var bitmap = new WriteableBitmap(776, 776);
            using (var buffer = bitmap.PixelBuffer.AsStream()) await buffer.WriteAsync(pixels.DetachPixelData());
            bitmap.Invalidate();
            _qrBitmap = bitmap;
            _simUri = data.SimStoreUri;
            _membershipUri = data.MembershipStoreUri;
            SimStoreButton.IsEnabled = MembershipStoreButton.IsEnabled = QrButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            App.LogException(ex, "shop-resources");
            ShopError.Message = "小店资源校验失败，请重新下载完整软件包。";
            ShopError.IsOpen = true;
        }
    }

    private async void SimStore_Click(object sender, RoutedEventArgs args) => await OpenStoreAsync(false);
    private async void MembershipStore_Click(object sender, RoutedEventArgs args) => await OpenStoreAsync(true);

    internal async Task OpenStoreAsync(bool membership)
    {
        if (_openingLink) return;
        await EnsureLoadedAsync();
        var uri = membership ? _membershipUri : _simUri;
        if (uri is null) return;
        _openingLink = true;
        try
        {
            bool opened = LinkLauncherOverride is { } launcher
                ? await launcher(uri) : await Windows.System.Launcher.LaunchUriAsync(uri);
            if (!opened) throw new InvalidOperationException("No browser accepted the store link.");
        }
        catch (Exception ex)
        {
            App.LogException(ex, "shop-launch");
            ShopError.Message = "未能打开店铺，请检查默认浏览器后重试。";
            ShopError.IsOpen = true;
        }
        finally { _openingLink = false; }
    }

    private async void Qr_Click(object sender, RoutedEventArgs args) => await ShowQrAsync();

    internal async Task ShowQrAsync()
    {
        await EnsureLoadedAsync();
        if (_qrBitmap is null || XamlRoot is null) return;
        double size = Math.Max(120, Math.Min(328, Math.Min(XamlRoot.Size.Width, XamlRoot.Size.Height) - 64));
        var image = new Image { Source = _qrBitmap, Width = size, Height = size };
        AutomationProperties.SetName(image, "微信客服二维码");
        _qrFlyout ??= new Flyout
        {
            Placement = FlyoutPlacementMode.Full,
            LightDismissOverlayMode = LightDismissOverlayMode.On
        };
        // Full placement centers the flyout but stretches its default presenter.
        // Bound the presenter itself to the QR so it cannot grow into a tall panel.
        _qrFlyout.FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter))
        {
            Setters =
            {
                new Setter(PaddingProperty, new Thickness(0)),
                new Setter(BorderThicknessProperty, new Thickness(0)),
                new Setter(CornerRadiusProperty, new CornerRadius(8)),
                new Setter(MinWidthProperty, 0d),
                new Setter(MinHeightProperty, 0d),
                new Setter(WidthProperty, size),
                new Setter(HeightProperty, size),
                new Setter(MaxWidthProperty, size),
                new Setter(MaxHeightProperty, size),
                new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Center),
                new Setter(VerticalContentAlignmentProperty, VerticalAlignment.Center)
            }
        };
        _qrFlyout.Content = image;
        _qrFlyout.Closed -= QrFlyout_Closed;
        _qrFlyout.Closed += QrFlyout_Closed;
        _qrFlyout.ShowAt(QrButton);
        QrIsOpen = true;
    }

    private void QrFlyout_Closed(object? sender, object args) { QrIsOpen = false; QrButton.Focus(FocusState.Programmatic); }
    public void CloseQr() { _qrFlyout?.Hide(); QrIsOpen = false; }

    private void Workspace_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool narrow = args.NewSize.Width < 560;
        StoreCards.ColumnSpacing = narrow ? 0 : 16;
        StoreCards.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(MembershipCard, narrow ? 0 : 1);
        Grid.SetRow(MembershipCard, narrow ? 1 : 0);
        CloseQr();
    }
}
