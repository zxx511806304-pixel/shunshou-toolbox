using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shunshou.Core;

namespace Shunshou.App;

public sealed partial class MainWindow
{
    private ToolUsagePreferences? _toolUsage;
    private ToolUsagePreferences ToolUsage => _toolUsage ??= ToolUsagePreferences.Load();

    /// <summary>Every tool in the window, used by the search box and the send-to menu.</summary>
    private static (string Category, string Title, string[] Operations)[] ToolCategories() =>
    [
        ("compression", "压缩与打包", ["按上传上限压缩", "无损 ZIP 打包", "解压压缩包"]),
        ("pdf", "PDF 处理", ["PDF 阅读", "PDF 转图片", "PDF 转可编辑 Word", "PDF 转可编辑 PPT", "整理 PDF 页面", "合并 PDF", "拆分 PDF", "PDF 加水印", "PDF 压缩", "Word 转 PDF", "网页转 PDF"]),
        ("image", "图片处理", ["图片格式转换", "图片裁剪", "图片提取文字"]),
        ("text", "文字工具", ["链接提取文字", "特殊字符库", "文本对比", "二维码", "Markdown 编辑"]),
        ("media", "音频与视频", ["音视频格式转换", "按目标大小压缩", "截取音视频片段", "提取音轨", "链接下载视频", "视频水印处理", "下载视频字幕", "屏幕录制", "截图标注"]),
        ("files", "文件搜索整理", ["按名称搜索", "误删恢复", "批量重命名", "撤销重命名"]),
        ("software", "软件卸载", ["管理已安装软件"]),
        ("system", "系统维护", ["C 盘清理", "大文件扫描", "重复文件清理", "启动项管理", "弹窗与劫持修复", "网络诊断"]),
        ("office", "办公助手", ["Office 文档急救", "剪贴板历史", "密码生成器"])
    ];

    /// <summary>Extra words people actually type, so search does not depend on the exact button label.</summary>
    private static readonly Dictionary<string, string> ToolKeywords = new(StringComparer.Ordinal)
    {
        ["图片裁剪"] = "裁剪 切图 裁图 头像 证件照 crop",
        ["图片格式转换"] = "转换 格式 jpg png webp 压缩图片",
        ["图片提取文字"] = "ocr 识别 截图 文字 图片转文字",
        ["链接提取文字"] = "网页 文章 正文 小说 章节 抓取 提取文字",
        ["特殊字符库"] = "特殊符号 字符 符号 勾 箭头 复制",
        ["文本对比"] = "对比 比较 差异 diff 找不同",
        ["二维码"] = "qr 二维码 生成 扫描 识别",
        ["Markdown 编辑"] = "markdown md 编辑 写作 预览",
        ["PDF 阅读"] = "pdf 阅读 查看 预览 打开",
        ["整理 PDF 页面"] = "pdf 页面 排序 旋转 删除页 空白页",
        ["合并 PDF"] = "pdf 合并 拼接",
        ["拆分 PDF"] = "pdf 拆分 分割",
        ["PDF 转图片"] = "pdf 转图片 png 逐页 长图 拼接",
        ["PDF 转可编辑 Word"] = "pdf word docx 可编辑",
        ["PDF 转可编辑 PPT"] = "pdf ppt pptx 可编辑",
        ["网页转 PDF"] = "网页 pdf 保存",
        ["PDF 加水印"] = "pdf 水印 加水印 文字 透明度",
        ["PDF 压缩"] = "pdf 压缩 减小 体积 瘦身",
        ["Word 转 PDF"] = "word docx 转 pdf 导出",
        ["按上传上限压缩"] = "压缩 上限 20mb 图片包",
        ["无损 ZIP 打包"] = "zip 打包 压缩",
        ["ZIP 解压"] = "解压 压缩包 zip",
        ["解压 7z / RAR"] = "解压 7z rar 压缩包",
        ["解压压缩包"] = "解压 压缩包 zip 7z rar tar cab",
        ["音视频格式转换"] = "视频 音频 转换 mp3 mp4",
        ["按目标大小压缩"] = "视频 压缩 目标大小",
        ["截取音视频片段"] = "截取 剪切 剪辑 片段 裁剪视频",
        ["提取音轨"] = "提取音频 音轨 转 mp3",
        ["链接下载视频"] = "下载 视频 链接",
        ["视频水印处理"] = "水印 去水印 修补",
        ["下载视频字幕"] = "字幕 srt 下载",
        ["屏幕录制"] = "录屏 录制 屏幕",
        ["截图标注"] = "截图 截屏 标注 马赛克 箭头 序号 screenshot snip",
        ["按名称搜索"] = "搜索 找文件 everything",
        ["误删恢复"] = "恢复 误删 找回 回收站",
        ["批量重命名"] = "改名 重命名 批量",
        ["撤销重命名"] = "撤销 改名 还原",
        ["管理已安装软件"] = "卸载 软件 删除程序",
        ["C 盘清理"] = "清理 c盘 空间 垃圾 缓存 瘦身 微信缓存 临时文件",
        ["大文件扫描"] = "大文件 占用 磁盘 空间 扫描 找出",
        ["重复文件清理"] = "重复 文件 查重 去重 清理 副本",
        ["启动项管理"] = "开机 启动项 自启动 开机慢 禁用",
        ["弹窗与劫持修复"] = "弹窗 广告 流氓软件 主页 劫持 浏览器 修复",
        ["网络诊断"] = "网络 断网 连不上 dns ping 诊断 断流",
        ["Office 文档急救"] = "office word excel ppt 崩溃 没保存 恢复 损坏 打不开 急救",
        ["剪贴板历史"] = "剪贴板 复制 历史 粘贴 记录",
        ["密码生成器"] = "密码 生成 随机 强密码 password"
    };

    /// <summary>Targets offered by “发送到…” in the search results; each one accepts files.</summary>
    private static readonly (string Category, string Operation, string Label)[] SendTargets =
    [
        ("compression", "按上传上限压缩", "按上传上限压缩"),
        ("compression", "无损 ZIP 打包", "打包成 ZIP"),
        ("compression", "解压压缩包", "解压压缩包"),
        ("image", "图片格式转换", "图片格式转换"),
        ("image", "图片裁剪", "图片裁剪"),
        ("image", "图片提取文字", "图片提取文字"),
        ("pdf", "合并 PDF", "合并 PDF"),
        ("pdf", "整理 PDF 页面", "整理 PDF 页面"),
        ("files", "批量重命名", "批量重命名")
    ];

    internal sealed record ToolSuggestion(string Category, string Operation, string Display, string Group);

    internal string ToolToken(string category, string operation) => category + "|" + operation;

    private void ToolSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        sender.ItemsSource = BuildSuggestions(sender.Text);
    }

    private void ToolSearch_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is ToolSuggestion suggestion) sender.Text = $"{suggestion.Operation} · {suggestion.Group}";
    }

    private void ToolSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        ToolSuggestion? suggestion = args.ChosenSuggestion as ToolSuggestion;
        suggestion ??= FirstMatch(args.QueryText);
        SubmitToolSuggestion(sender, suggestion);
    }

    internal ToolSuggestion? FirstMatch(string? query) => BuildSuggestions(query)
        .FirstOrDefault(item => item.Group != "最近使用");

    /// <summary>Shared by the search box and by verification, so both follow the same code path.</summary>
    internal void SubmitToolSuggestion(AutoSuggestBox sender, ToolSuggestion? suggestion)
    {
        if (suggestion is null)
        {
            ShowStatus("没有找到这个工具", "试试“裁剪”“二维码”“字幕”“恢复”等关键词。", InfoBarSeverity.Informational);
            return;
        }
        NavigateToTool(suggestion.Category, suggestion.Operation);
        sender.Text = string.Empty;
    }

    internal List<ToolSuggestion> BuildSuggestions(string? query)
    {
        var suggestions = new List<ToolSuggestion>();
        string text = (query ?? "").Trim();
        if (text.Length == 0)
        {
            foreach (string token in ToolUsage.Recent)
                if (FindTool(token) is { } recent)
                    suggestions.Add(new(recent.Category, recent.Operation, recent.Operation, "最近使用"));
            return suggestions;
        }
        foreach (var category in ToolCategories())
        {
            foreach (string operation in category.Operations)
            {
                string haystack = operation + " " + category.Title + " " + (ToolKeywords.TryGetValue(operation, out var keywords) ? keywords : "");
                if (haystack.Contains(text, StringComparison.OrdinalIgnoreCase))
                    suggestions.Add(new(category.Category, operation, operation, category.Title));
            }
        }
        return suggestions;
    }

    private (string Category, string Operation)? FindTool(string token)
    {
        string[] parts = token.Split('|');
        if (parts.Length != 2) return null;
        foreach (var category in ToolCategories())
            if (category.Category == parts[0] && category.Operations.Contains(parts[1])) return (parts[0], parts[1]);
        return null;
    }

    internal void NavigateToTool(string category, string operation)
    {
        if (_busy) return;
        var item = Navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(entry => (entry.Tag as string) == category);
        if (item is null) return;
        Navigation.SelectedItem = item;
        SelectCategory(category);
        int index = Array.IndexOf(Operations[category], operation);
        if (index >= 0) OperationBox.SelectedIndex = index;
        ToolUsage.RecordRecent(ToolToken(category, operation));
    }

    private void SendSearchItem_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var paths = SearchSelection();
        if (paths.Count == 0) return;
        var flyout = new MenuFlyout();
        foreach (var (category, operation, label) in SendTargets)
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => SendToTool(category, operation, paths);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(SendSearchItemButton);
    }

    /// <summary>Selected search results that other tools can accept; folders are left out.</summary>
    internal List<string> SearchSelection() => SearchResults.SelectedItems
        .OfType<SearchResultItem>()
        .Where(item => !item.IsDirectory)
        .Select(item => item.FullPath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private void SendToTool(string category, string operation, IReadOnlyList<string> paths)
    {
        NavigateToTool(category, operation);
        if (_category != category || Operation != operation) return;
        var compatible = InputSelectionPolicy.CompatiblePaths(CurrentInputTool, paths);
        int skipped = paths.Count - compatible.Count;
        if (compatible.Count == 0)
        {
            ShowStatus("无法发送", $"{Operation} 不接受这些文件的格式。", InfoBarSeverity.Warning);
            return;
        }
        if (!AcceptInputPaths(compatible))
        {
            ShowStatus("无法发送", $"{Operation} 没有接收这些文件，请检查格式或数量。", InfoBarSeverity.Warning);
            return;
        }
        ShowStatus("已发送到 " + Operation, skipped == 0
            ? $"已加入 {compatible.Count} 个文件，确认设置后即可开始。"
            : $"已加入 {compatible.Count} 个文件，跳过 {skipped} 个格式不符的项目。", InfoBarSeverity.Informational);
    }
}
