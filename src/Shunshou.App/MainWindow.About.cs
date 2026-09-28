using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Shunshou.App;

public sealed partial class MainWindow
{
    private void Feedback_Click(object sender, RoutedEventArgs e)
    {
        // Lowest-cost feedback channel: put the mailbox on the clipboard, no address shown in the UI.
        const string feedbackMail = "shunshoutoolbox@163.com";
        try
        {
            var package = new DataPackage();
            package.SetText(feedbackMail);
            Clipboard.SetContent(package);
            ShowStatus("反馈邮箱已复制", "邮箱地址已复制，请打开邮箱粘贴到收件人，写下你的意见或问题后发送。", InfoBarSeverity.Informational);
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        // Low-cost manual updates: open a permanent shared-folder page in the browser.
        // The app never downloads, patches or replaces itself.
        const string downloadPage = "https://pan.baidu.com/s/1PuKZzMN23TrQm2pWfbqdzw?pwd=avhc";
        try
        {
            bool launched = await Windows.System.Launcher.LaunchUriAsync(new Uri(downloadPage));
            if (launched)
                ShowStatus("已打开下载页", "网盘分享页已在浏览器中打开，提取码 avhc；下载最新版后按提示安装，用户数据会保留。", InfoBarSeverity.Informational);
            else
                ShowError("无法打开浏览器，请手动复制下载地址：" + downloadPage);
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        try { await ComponentAboutDialog.ShowAsync(RootLayout.XamlRoot); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void Notice_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = new StackPanel { Spacing = 12, MinWidth = 380, MaxWidth = 560 };
            foreach (var text in new[]
            {
                "顺手工具箱供个人学习与日常办公使用。",
                "请只下载、转换或处理你拥有权利或已获授权的内容（视频、字幕、网页、文档等）。",
                "下载与处理结果仅供个人学习使用，请勿传播或用于商业用途；由此产生的责任由使用者承担。",
                "所有文件均在你的电脑上本地处理，不会上传。"
            })
                content.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
            var dialog = new ContentDialog
            {
                Title = "使用须知", XamlRoot = RootLayout.XamlRoot, CloseButtonText = "我知道了",
                RequestedTheme = RootLayout.ActualTheme,
                Content = content
            };
            dialog.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ms-appx:///ToolboxTheme.xaml") });
            await dialog.ShowAsync();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
}
