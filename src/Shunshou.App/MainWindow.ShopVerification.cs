using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Shunshou.App;

public sealed partial class MainWindow
{
    // Small, opt-in check for this release; no document, media, disk or uninstall operations.
    private async Task<int> VerifyShopAsync(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        try
        {
            Navigation.SelectedItem = ShopNavigationItem;
            await ShopArea.EnsureLoadedAsync();
            await Task.Delay(200);
            RequireUi(ShopArea.IsReady && CategoryTitle.Text == "顺手小店" && RunFooter.Visibility == Visibility.Collapsed
                && GeneralArea.Visibility == Visibility.Collapsed && OperationCard.Visibility == Visibility.Collapsed,
                "Store page or its protected resources did not load.");
            RequireUi(!AcceptInputPaths([Path.Combine(output, "not-an-input.txt")]), "Shop accepted a file drop.");
            checks.Add("Store navigation, encrypted resource load and independent page layout; file inputs remain inactive.");

            var destinations = new List<Uri>();
            ShopArea.LinkLauncherOverride = uri => { destinations.Add(uri); return Task.FromResult(true); };
            await ShopArea.OpenStoreAsync(false);
            await ShopArea.OpenStoreAsync(true);
            var expected = Shunshou.Core.ShopResources.Load();
            RequireUi(destinations.Count == 2 && destinations[0] == expected.SimStoreUri
                && destinations[1] == expected.MembershipStoreUri && destinations[0] != destinations[1], "Store destinations changed.");
            checks.Add("Both launch handlers preserve the exact store URLs; browser boundary intercepted, no web purchase or network request.");

            RootLayout.RequestedTheme = ElementTheme.Light;
            await Task.Delay(150);
            await SaveScreenshot(Path.Combine(output, "shop-light.png"));
            ThemeToggle_Click(ThemeToggleButton, new RoutedEventArgs());
            await Task.Delay(150);
            RequireUi(RootLayout.RequestedTheme == ElementTheme.Dark, "Theme button did not switch to dark.");
            await SaveScreenshot(Path.Combine(output, "shop-dark.png"));
            ThemeToggle_Click(ThemeToggleButton, new RoutedEventArgs());
            await Task.Delay(150);
            RequireUi(RootLayout.RequestedTheme == ElementTheme.Light, "Theme button did not switch to light.");
            checks.Add("Title-bar button toggles both themes without navigating or clearing work.");

            await ShopArea.ShowQrAsync();
            await Task.Delay(200);
            var popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(RootLayout.XamlRoot);
            var qrPopup = popups.FirstOrDefault(p => Descendants(p.Child).OfType<Image>()
                .Any(i => i.Source is Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap { PixelWidth: 776, PixelHeight: 776 }));
            RequireUi(ShopArea.QrIsOpen && qrPopup is not null,
                "QR flyout did not display the cropped original image.");
            var presenter = Descendants(qrPopup!.Child).OfType<FlyoutPresenter>().Single();
            RequireUi(presenter.Content is Image, "QR flyout contains content beyond the QR image.");
            var qrImage = (Image)presenter.Content;
            RequireUi(Math.Abs(presenter.ActualWidth - qrImage.ActualWidth) < 2
                && Math.Abs(presenter.ActualHeight - qrImage.ActualHeight) < 2
                && qrImage.ActualWidth > 100, "QR presenter has unwanted empty space.");
            await File.WriteAllTextAsync(Path.Combine(output, "qr-layout.json"), JsonSerializer.Serialize(new
            {
                PresenterWidth = presenter.ActualWidth, PresenterHeight = presenter.ActualHeight,
                ImageWidth = qrImage.ActualWidth, ImageHeight = qrImage.ActualHeight
            }));
            await ShopArea.SaveQrVerificationAsync(Path.Combine(output, "support-qr.png"));
            ShopArea.CloseQr();
            RequireUi(!ShopArea.QrIsOpen, "QR flyout did not close.");
            checks.Add("QR flyout contains only the original 776px square crop, with quiet border, and closes correctly.");

            ShopArea.LinkLauncherOverride = _ => Task.FromResult(false);
            await ShopArea.OpenStoreAsync(false);
            RequireUi(ShopArea.HasError, "Browser launch failure was swallowed.");
            ShopArea.LinkLauncherOverride = null;
            Navigation.SelectedItem = Navigation.MenuItems[0];
            RequireUi(GeneralArea.Visibility == Visibility.Visible && RunFooter.Visibility == Visibility.Visible
                && ShopArea.Visibility == Visibility.Collapsed, "Returning to the existing tool page failed.");
            SetBusy(true); SetBusy(false);
            checks.Add("Browser failure is visible; return navigation and separator-safe busy state work. Existing conversion functions were not rerun.");

            await File.WriteAllTextAsync(Path.Combine(output, "shop-verification.json"), JsonSerializer.Serialize(
                new { Passed = true, Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "shop-verification.json"), JsonSerializer.Serialize(
                new { Passed = false, Checks = checks, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            try { await SaveScreenshot(Path.Combine(output, "failure.png")); } catch { }
            return 1;
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
