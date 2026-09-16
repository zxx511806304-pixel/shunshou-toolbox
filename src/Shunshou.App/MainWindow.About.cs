using Microsoft.UI.Xaml;

namespace Shunshou.App;

public sealed partial class MainWindow
{
    private async void About_Click(object sender, RoutedEventArgs e)
    {
        try { await ComponentAboutDialog.ShowAsync(RootLayout.XamlRoot); }
        catch (Exception ex) { ShowError(ex.Message); }
    }
}
