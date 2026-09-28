using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Shunshou.Core;
using Windows.ApplicationModel.DataTransfer;

namespace Shunshou.App;

public sealed partial class PasswordGeneratorWorkspace : UserControl
{
    public PasswordGeneratorWorkspace() => InitializeComponent();

    public nint HostWindowHandle { get; set; }

    internal string GeneratedPassword => ResultBox.Text;
    internal string StatusMessage => StatusText.Text;
    internal string StrengthLabel => StrengthText.Text;

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        Notice.IsOpen = false;
        try
        {
            int length = (int)LengthBox.Value;
            bool upper = UpperBox.IsChecked == true;
            bool lower = LowerBox.IsChecked == true;
            bool digits = DigitsBox.IsChecked == true;
            bool symbols = SymbolsBox.IsChecked == true;
            bool excludeAmbiguous = ExcludeAmbiguousBox.IsChecked == true;
            string password = PasswordGeneratorService.Generate(length, upper, lower, digits, symbols, excludeAmbiguous);
            ResultBox.Text = password;
            CopyButton.IsEnabled = true;
            var (bits, label) = PasswordGeneratorService.EstimateStrength(length, upper, lower, digits, symbols, excludeAmbiguous);
            StrengthBar.Value = Math.Min(100, bits);
            StrengthText.Text = $"{label}（{bits} 位）";
            StrengthBar.Foreground = label switch
            {
                "弱" => new SolidColorBrush(Colors.IndianRed),
                "中" => new SolidColorBrush(Colors.Goldenrod),
                _ => new SolidColorBrush(Colors.ForestGreen)
            };
            StatusText.Text = $"已生成 {length} 位密码。";
        }
        catch (ArgumentException ex)
        {
            ShowError("无法生成密码", ex.Message);
            ResultBox.Text = string.Empty;
            CopyButton.IsEnabled = false;
            StrengthBar.Value = 0;
            StrengthText.Text = "";
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ResultBox.Text)) return;
        try
        {
            var package = new DataPackage();
            package.SetText(ResultBox.Text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            StatusText.Text = "已复制，30 秒后建议清理剪贴板。";
        }
        catch (Exception)
        {
            ShowError("无法写入剪贴板", "系统暂时拒绝了剪贴板写入，请稍后再试。");
        }
    }

    private void ShowError(string title, string message)
    {
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = InfoBarSeverity.Warning;
        Notice.IsOpen = true;
    }
}
