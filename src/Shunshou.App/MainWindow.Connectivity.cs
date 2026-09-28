namespace Shunshou.App;

public sealed partial class MainWindow
{
    private bool IsUrlOperation => Operation is "链接下载视频" or "下载视频字幕" or "网页转 PDF" or "链接提取文字";

    private void UpdateConnectionStatus()
    {
        bool online = _category == "shop" || IsUrlOperation;
        ConnectionStatusText.Text = online ? "需联网" : "本地处理 · 离线可用";
        ConnectionStatusIcon.Glyph = online ? "\uE774" : "\uE73E";
    }
}
