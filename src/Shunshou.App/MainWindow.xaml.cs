using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Shunshou.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace Shunshou.App;

public sealed partial class MainWindow : Window
{
    private readonly List<string> _inputs = [];
    private CancellationTokenSource? _cancellation;
    private bool _ready;
    private bool _busy;
    private string _category = "compression";
    private string? _lastOutputDirectory;
    private static readonly Dictionary<string, string[]> Operations =
        ToolCategories().ToDictionary(category => category.Category, category => category.Operations);

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    public MainWindow()
    {
        InitializeComponent();
        foreach (var item in Navigation.MenuItems.OfType<NavigationViewItem>())
            item.Icon = ToolIcons.Create((string)item.Tag switch { "compression" => "zip", "files" => "search", "software" => "uninstall", var kind => kind });
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleDragArea);
        InitializeAppearance();
        if (_appearanceVerificationLaunch) RefreshStartupMenu(); else InitializeStartupRegistration();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(hwnd) / 96d;
        var workArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        int windowWidth = Math.Min((int)(1220 * scale), Math.Max(640, workArea.Width - 40));
        int windowHeight = Math.Min((int)(860 * scale), Math.Max(480, workArea.Height - 40));
        AppWindow.Resize(new SizeInt32(windowWidth, windowHeight));
        AppWindow.Move(new PointInt32(workArea.X + (workArea.Width - windowWidth) / 2, workArea.Y + (workArea.Height - windowHeight) / 2));
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            presenter.PreferredMinimumWidth = Math.Min((int)(940 * scale), windowWidth);
        OutputDirectory.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ShunshouToolbox", "Output");
        _ready = true;
        InitializeSearch();
        InitializeFileDrop();
        InitializeWorkspaces(hwnd);
        InitializeQuickLauncher();
        Navigation.SelectedItem = Navigation.MenuItems[0];
        SelectCategory("compression");
        Closed += (_, _) => { _cancellation?.Cancel(); DisposeAppearance(); ShopArea.CloseQr(); DisposeSearch(); OcrEditor.Dispose(); Uninstaller.Dispose(); Recovery.Dispose(); VideoTools.Dispose(); SubtitleTools.Dispose(); WebPdfTools.Dispose(); MarkdownTools.Dispose(); RecordingTools.Dispose(); DisposeQuickLauncher(); _tray?.Dispose(); _minimizeHook?.Dispose(); };
    }

    private string Operation => _category == "shop" ? "" : OperationBox.SelectedItem as string ?? Operations[_category][0];

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_ready && args.SelectedItem is NavigationViewItem item && item.Tag is string key)
            SelectCategory(key);
    }

    private void SelectCategory(string category)
    {
        if (_busy) return;
        ShopArea.CloseQr();
        VideoTools.PausePreview();
        WebPdfTools.PausePreview();
        SetVisible(ShopArea, category == "shop");
        _selectingCategory = true;
        _category = category;
        if (category == "shop")
        {
            CategoryTitle.Text = "顺手小店";
            CategorySubtitle.Text = "手机卡、会员与生活优惠";
            foreach (var element in new UIElement[] { OperationCard, GeneralArea, SearchWorkspace, OcrArea, Uninstaller, Recovery, VideoTools, SubtitleTools, WebPdfTools, RecordingTools, CharacterLibrary, TextDiffTools, QrTools, MarkdownTools, PdfPageTools, PdfReaderTools, DiskCleanupTools, LargeFilesTools, DuplicateTools, StartupTools, HijackTools, NetworkTools, OfficeRescueTools, ClipboardTools, PasswordTools, RunFooter })
                SetVisible(element, false);
            UpdateConnectionStatus();
            StatusInfo.IsOpen = false;
            _selectingCategory = false;
            _ = ShopArea.EnsureLoadedAsync();
            return;
        }
        (CategoryTitle.Text, CategorySubtitle.Text) = category switch
        {
            "pdf" => ("PDF 处理", "从课件到办公资料，把页面变成你需要的样子。"),
            "image" => ("图片处理", "转换、缩放、裁剪图片，或识别图片里的文字。"),
            "text" => ("文字工具", "提取网页正文、整理特殊字符、比较文本与二维码。"),
            "media" => ("音频与视频", "下载、转换与编辑，让视频处理更顺手。"),
            "software" => ("软件卸载", "查找已安装的软件，卸载后按需检查关联文件。"),
            "files" => ("文件搜索整理", "从熟悉的名称开始，让资料更容易找到。"),
            "system" => ("系统维护", "清理空间、管理启动项、修复劫持，让电脑恢复轻快。"),
            "office" => ("办公助手", "找回没保存的文档、记住剪贴板、生成安全密码。"),
            _ => ("压缩与打包", "把文件处理到刚好能提交。复杂参数，交给工具箱。")
        };
        OperationBox.ItemsSource = Operations[category];
        OperationBox.SelectedIndex = 0;
        _selectingCategory = false;
        BuildOperationButtons();
        ConfigureOperation();
    }

    private void Operation_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_ready && !_selectingCategory && OperationBox.SelectedIndex >= 0)
        {
            ConfigureOperation();
        }
    }

    private void ConfigureOperation()
    {
        VideoTools.PausePreview();
        WebPdfTools.PausePreview();
        string op = Operation;
        RestoreInputDraft();
        SyncOperationButtons();
        bool software = _category == "software";
        bool maintenance = _category is "system" or "office";
        bool recovery = op == "误删恢复";
        bool ocr = op == "图片提取文字";
        bool videoTool = op is "链接下载视频" or "视频水印处理";
        bool subtitles = op == "下载视频字幕";
        bool webPdf = op is "网页转 PDF" or "链接提取文字";
        bool recording = op == "屏幕录制";
        bool characters = op == "特殊字符库";
        bool crop = op == "图片裁剪";
        bool trim = op == "截取音视频片段";
        bool extractAudio = op == "提取音轨";
        bool archive = op == "解压 7z / RAR";
        bool textDiff = op == "文本对比";
        bool qr = op == "二维码";
        bool pdfPages = op == "整理 PDF 页面";
        bool pdfRead = op == "PDF 阅读";
        bool screenshot = op == "截图标注";
        bool markdown = op == "Markdown 编辑";
        bool panelTool = characters || textDiff || qr || pdfPages || pdfRead || screenshot || markdown;
        SetVisible(RecordingTools, recording);
        if (recording) _ = RecordingTools.EnsureLoadedAsync();
        SetVisible(SubtitleTools, subtitles);
        SetVisible(WebPdfTools, webPdf);
        if (webPdf) WebPdfTools.SelectTextMode(op == "链接提取文字");
        SetVisible(CharacterLibrary, characters);
        if (characters) CharacterLibrary.EnsureLoaded();
        SetVisible(TextDiffTools, textDiff);
        SetVisible(QrTools, qr);
        SetVisible(PdfPageTools, pdfPages);
        if (pdfPages) _ = PdfPageTools.SetDocumentAsync(_activeInputPath);
        SetVisible(PdfReaderTools, pdfRead);
        if (pdfRead) _ = PdfReaderTools.SetDocumentAsync(_activeInputPath);
        SetVisible(ScreenshotTools, screenshot);
        SetVisible(MarkdownTools, markdown);
        UpdateConnectionStatus();
        SetVisible(VideoTools, videoTool);
        if (videoTool) VideoTools.SelectMode(op == "视频水印处理");
        SetVisible(OcrArea, ocr);
        SetVisible(Uninstaller, software);
        SetVisible(Recovery, recovery);
        SetVisible(DiskCleanupTools, op == "C 盘清理");
        SetVisible(LargeFilesTools, op == "大文件扫描");
        SetVisible(DuplicateTools, op == "重复文件清理");
        SetVisible(StartupTools, op == "启动项管理");
        SetVisible(HijackTools, op == "弹窗与劫持修复");
        SetVisible(NetworkTools, op == "网络诊断");
        SetVisible(OfficeRescueTools, op == "Office 文档急救");
        SetVisible(ClipboardTools, op == "剪贴板历史");
        SetVisible(PasswordTools, op == "密码生成器");
        SetVisible(OperationCard, !software);
        SetVisible(RunFooter, !software && !maintenance && !recovery && !videoTool && !subtitles && !webPdf && !recording && !panelTool);
        if (software) _ = LoadUninstallerAsync();
        bool target = op is "按上传上限压缩" or "按目标大小压缩";
        bool imageConvert = op == "图片格式转换";
        bool media = _category == "media";
        bool pdfImages = op == "PDF 转图片";
        SetVisible(TargetPanel, target);
        SetVisible(LossyPanel, op == "按上传上限压缩");
        SetVisible(FormatPanel, imageConvert || media);
        SetVisible(ImageQuality, op is "图片格式转换" or "图片裁剪");
        SetVisible(AdvancedPanel, imageConvert);
        SetVisible(CropPanel, crop);
        SetVisible(TrimPanel, trim);
        if (crop)
        {
            CropKindBox.ItemsSource ??= new[] { "按比例", "按像素尺寸" };
            CropRatioBox.ItemsSource ??= Shunshou.Core.CropOptions.RatioNames;
            CropAnchorBox.ItemsSource ??= Shunshou.Core.CropOptions.AnchorNames;
            if (CropKindBox.SelectedIndex < 0) CropKindBox.SelectedIndex = 0;
            if (CropRatioBox.SelectedIndex < 0) CropRatioBox.SelectedIndex = 0;
            if (CropAnchorBox.SelectedIndex < 0) CropAnchorBox.SelectedIndex = 0;
            if (double.IsNaN(CropWidthBox.Value)) CropWidthBox.Value = 800;
            if (double.IsNaN(CropHeightBox.Value)) CropHeightBox.Value = 800;
            SetVisible(CropPixelPanel, CropKindBox.SelectedIndex == 1);
        }
        else CropPreviewCard.Visibility = Visibility.Collapsed;
        if (trim)
        {
            TrimModeBox.ItemsSource ??= new[] { "无损复制（快，按关键帧对齐）", "精确模式（重新编码）" };
            if (TrimModeBox.SelectedIndex < 0) TrimModeBox.SelectedIndex = 0;
        }
        SetVisible(PdfPanel, pdfImages);
        SetVisible(WatermarkPanel, op == "PDF 加水印");
        SetVisible(PdfOfficeMode, op is "PDF 转可编辑 Word" or "PDF 转可编辑 PPT");
        SetVisible(SearchWorkspace, op == "按名称搜索");
        SetVisible(GeneralArea, op != "按名称搜索" && !maintenance && !ocr && !software && !recovery && !videoTool && !subtitles && !webPdf && !recording && !panelTool);
        ResetSearchView();
        SetVisible(RenamePanel, op == "批量重命名");
        SetVisible(OutputPanel, !maintenance && op is not "按名称搜索" and not "批量重命名" and not "撤销重命名" and not "特殊字符库" and not "文本对比" and not "二维码" and not "整理 PDF 页面" and not "PDF 阅读" and not "截图标注" and not "Markdown 编辑");
        SetVisible(AddFolderButton, _category == "compression" || op == "按名称搜索");
        SetVisible(AddFilesButton, !maintenance && op is not ("按名称搜索" or "特殊字符库" or "文本对比" or "二维码" or "整理 PDF 页面" or "截图标注" or "Markdown 编辑"));
        FeatureNote.IsOpen = false;
        if (imageConvert) FormatBox.ItemsSource = new[] { "jpg", "png", "webp", "bmp", "tiff" };
        else if (trim) FormatBox.ItemsSource = new[] { "mp4", "mp3", "m4a", "wav", "flac" };
        else if (extractAudio) FormatBox.ItemsSource = new[] { "mp3", "m4a", "wav", "flac" };
        else if (media) FormatBox.ItemsSource = op == "按目标大小压缩" ? new[] { "mp3", "mp4", "m4a" } : new[] { "mp3", "mp4", "wav", "flac", "m4a" };
        FormatBox.SelectedIndex = 0;
        (InputTitle.Text, InputHint.Text, OperationDescription.Text) = op switch
        {
            "按上传上限压缩" => ("拖入 ZIP 压缩包或图片文件夹", "优化 JPG、PNG、WebP，其他文件保留原样", "输入上传上限，调整包内图片，再核验整个压缩包的大小。"),
            "无损 ZIP 打包" => ("拖入文件或文件夹", "打包为 ZIP，完整保留文件内容", "无损打包适合整理传输，已经压缩过的图片和视频通常难以再次大幅缩小。"),
            "ZIP 解压" => ("拖入一个压缩包", "解压到新的文件夹，保留原压缩包", "展开 ZIP、7z、RAR 等压缩包，恢复目录与文件。"),
            "解压 7z / RAR" => ("拖入 .7z、.rar、.zip、.tar 或 .cab 压缩包", "解压到新的文件夹，原压缩包保留", "解压 ZIP 工具打不开的 7z、RAR 等压缩包；本版不创建 .7z 或 .rar。"),
            "解压压缩包" => ("拖入一个压缩包", "解压到新的文件夹，保留原压缩包", "支持 ZIP、7z、RAR、TAR、CAB 等常见格式，自动选择解压方式。"),
            "图片提取文字" => ("拖入截图或图片", "选择图片后识别，可修改、复制或保存文字", "拖入或粘贴图片，选中一张识别文字，可直接编辑和复制。"),
            "按名称搜索" => ("拖入文件或文件夹", "拖入文件夹可快速设置搜索范围", "按名称搜索本机磁盘，选择结果预览，双击打开。"),
            "误删恢复" => ("选择磁盘或镜像", "查找删除的文件", "从回收站、删除记录或文件内容中寻找资料，预览后复制恢复。"),
            "批量重命名" => ("拖入一批需要整理的文件", "执行前预览新名称，保留文件扩展名", "按前缀和递增序号统一命名，生成可供恢复的记录。"),
            "撤销重命名" => ("选择此前生成的重命名记录", "选择 rename-history 文件夹里的 JSON 记录", "根据本地记录恢复原文件名。文件内容或位置变化时会停止恢复。"),
            "图片格式转换" => ("拖入一张或多张图片", "支持批量转换；多帧图片逐帧导出", "转为常用图片格式，可按需调整尺寸与编码质量。"),
            "合并 PDF" => ("按顺序选择多份 PDF", "合并顺序与添加顺序相同", "把多份材料合成一个 PDF，生成新文件。"),
            "PDF 加水印" => ("拖入一份 PDF 文档", "填写水印文字与透明度，原 PDF 保留", "在每页中央添加 45° 斜向的半透明灰色水印文字，生成新文件。"),
            "PDF 压缩" => ("拖入一份 PDF 文档", "按 150 DPI 重排页面，原 PDF 保留", "把每页渲染为压缩图片后重建 PDF，扫描件和图片较多的文档缩小明显；文字将变为图片。若原文件已足够小，会保留原文件并提示。"),
            "Word 转 PDF" => ("拖入一份 .docx 文档", "保留段落、标题、加粗斜体、表格与图片", "在本地把 Word 文档排版为 PDF；复杂版式（页眉页脚、文本框、浮动对象）按近似方式处理。"),
            "链接下载视频" => ("", "", "粘贴链接，选择画质，保存到电脑。"),
            "视频水印处理" => ("", "", "框选画面区域，预览效果后导出新视频。"),
            "下载视频字幕" => ("", "", "提取网站提供的字幕，保存为字幕文件或纯文字。"),
            "屏幕录制" => ("", "", "选择屏幕或区域，录下演示、课程与操作过程。"),
            "截图标注" => ("", "", "框选屏幕区域后标注矩形、箭头、马赛克、序号或文字，复制或保存为 PNG。"),
            "网页转 PDF" => ("", "", "粘贴网址，预览网页，保存为 PDF。"),
            "链接提取文字" => ("", "", "打开文章或章节，提取正文，编辑并保存文字。"),
            "特殊字符库" => ("", "", "整理好的常用特殊字符，点击即可复制；可按名称或编码查找。"),
            "文本对比" => ("", "", "比较两份文本的差异，只在本机处理。"),
            "二维码" => ("", "", "生成或识别二维码，图片与内容都在本机处理。"),
            "Markdown 编辑" => ("", "", "轻量 Markdown 写作：编辑、预览、导出 PDF。"),
            "图片裁剪" => ("拖入一张或多张图片", "按比例或像素裁剪，批量使用同一设置", "先预览第一张的效果，确认后再对全部图片应用相同裁剪。"),
            "截取音视频片段" => ("拖入一个音频或视频文件", "填写起止时间，截取其中一段", "无损复制不重新编码，起止会落在关键帧；精确模式重新编码，可精确到填写的时间。"),
            "提取音轨" => ("拖入一个视频或音频文件", "取出音频轨道，保存为常用音频格式", "从视频中提取音轨，或把音频转成 MP3、M4A、WAV、FLAC。"),
            "整理 PDF 页面" => ("", "", "调整页序、删除或旋转页面，生成新的 PDF。"),
            "PDF 阅读" => ("", "", "在本机翻页阅读 PDF；底部可一键跳到合并、拆分、整理、加水印或压缩。"),
            "PDF 转图片" => ("拖入一份 PDF 文档", "可逐页导出，也可拼接为一张高清长图", "按设定 DPI 把 PDF 页面渲染为 PNG；也可把全部页面竖向拼接成一张高清长图，原 PDF 保留。"),
            "C 盘清理" => ("", "", "扫描缓存与临时文件，勾选后一键清理，释放 C 盘空间。"),
            "大文件扫描" => ("", "", "找出磁盘上最占空间的文件，按需打开位置或删除到回收站。"),
            "重复文件清理" => ("", "", "按内容找出完全相同的重复文件，保留一份，其余送入回收站。"),
            "启动项管理" => ("", "", "查看开机自动启动的项目，禁用拖慢开机的项，随时可恢复。"),
            "弹窗与劫持修复" => ("", "", "检查被篡改的浏览器快捷方式与主页，修复常见劫持。"),
            "网络诊断" => ("", "", "检查网卡、网关、DNS 与代理状态，一键修复常见网络问题。"),
            "Office 文档急救" => ("", "", "找回崩溃前未保存的 Word/Excel/PPT，或从损坏文档中提取内容。"),
            "剪贴板历史" => ("", "", "自动记录本次运行期间复制的文字、图片和文件，点击即可再次复制。"),
            "密码生成器" => ("", "", "在本机生成高强度随机密码，不保存、不上传。"),
            _ when _category == "pdf" => ("拖入一份 PDF 文档", "处理后保存为新文件，原 PDF 保留", "本地处理 PDF 页面，选择适合后续使用的输出方式。"),
            _ => ("拖入一个音频或视频文件", "转换为新文件，源文件保留", "转换常用音视频格式，或按提交要求调整大小。")
        };
        if (op is "PDF 转可编辑 Word" or "PDF 转可编辑 PPT")
            Note("版式与编辑", "尽量保留文字样式、位置和页面图形。扫描页使用文字识别，复杂版式请对照原文检查。");
        else if (media)
            Note("转换与质量", "转为 MP3、MP4、M4A 通常会损失质量；FLAC 可保存解码后的无损音频。目标大小过小时，工具会提示无法达标。");
        RunButton.Content = ocr ? "开始识别"
            : op == "按名称搜索" ? "搜索"
            : op == "批量重命名" ? "预览重命名"
            : op == "图片裁剪" ? "开始裁剪"
            : op == "截取音视频片段" ? "开始截取"
            : op == "提取音轨" ? "提取音轨"
            : archive ? "开始解压"
            : "开始处理";
        StatusInfo.IsOpen = false;
        ResultPanel.Visibility = Visibility.Collapsed;
        SetVisible(OpenOutputButton, op != "按名称搜索" && !ocr);
        TaskProgress.Value = 0;
        ProgressText.Text = "准备就绪 · 文件只在你的电脑处理";
        WorkspaceScroll.ChangeView(null, 0, null, true);
        if (_inputDraftMessage is { } message)
        {
            ShowStatus("已保留文件", message, InfoBarSeverity.Informational);
            _inputDraftMessage = null;
        }
    }

    private void Note(string title, string message)
    {
        FeatureNote.Title = title;
        FeatureNote.Message = message;
        FeatureNote.IsOpen = true;
    }

    private static void SetVisible(UIElement element, bool visible) => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private bool AllowsMultiple => InputSelectionPolicy.AllowsMultiple(CurrentInputTool);

    private async void AddFiles_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            foreach (var extension in FileFilters()) picker.FileTypeFilter.Add(extension);
            if (AllowsMultiple)
            {
                var files = await picker.PickMultipleFilesAsync();
                AddInputs(files.Select(file => file.Path));
            }
            else
            {
                var file = await picker.PickSingleFileAsync();
                if (file != null) AddInputs([file.Path]);
            }
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    /// <summary>Lets a workspace without its own input card ask the window to run the shared picker.</summary>
    internal Task PickInputsForCurrentToolAsync()
    {
        AddFiles_Click(this, new RoutedEventArgs());
        return Task.CompletedTask;
    }

    private string[] FileFilters() => InputSelectionPolicy.FileFilters(CurrentInputTool);

    private async Task<string?> PickFolder()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs args)
    {
        try { var path = await PickFolder(); if (path != null) AddInputs([path]); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void ChooseOutput_Click(object sender, RoutedEventArgs args)
    {
        try { var path = await PickFolder(); if (path != null) OutputDirectory.Text = path; }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void AddInputs(IEnumerable<string> paths) => AcceptInputPaths(paths);

    private sealed record UiResult(string Message, string? OutputDirectory = null, bool Warning = false);

    private async void Run_Click(object sender, RoutedEventArgs args)
    {
        if (_busy) return;
        if (Operation == "按名称搜索") { await RunSearchAsync(); return; }
        if (Operation == "图片提取文字") { await RunOcrAsync(); return; }
        if (_inputs.Count == 0) { ShowError("请先添加要处理的文件或文件夹。"); return; }
        string op = Operation;
        string[] inputs = _inputs.ToArray();
        string output = OutputDirectory.Text.Trim();
        string format = FormatBox.SelectedItem as string ?? "jpg";
        bool lossy = AllowLossy.IsChecked == true;
        bool resize = lossy && AllowResize.IsChecked == true;
        bool imagesOnly = ImagesOnly.IsChecked == true;
        long maxBytes = double.IsFinite(TargetSize.Value) && TargetSize.Value > 0 ? (long)(TargetSize.Value * 1_000_000) : 0;
        int dpi = double.IsFinite(PdfDpi.Value) ? (int)PdfDpi.Value : 150;
        uint quality = double.IsFinite(ImageQuality.Value) ? (uint)ImageQuality.Value : 90;
        uint? width = double.IsFinite(ImageWidth.Value) && ImageWidth.Value > 0 ? (uint)ImageWidth.Value : null;
        string query = SearchQuery.Text.Trim();
        string renamePrefix = RenamePrefix.Text;
        int renameStart = double.IsFinite(RenameStart.Value) ? (int)RenameStart.Value : 1;
        bool preservePdfLayout = PdfOfficeMode.SelectedIndex == 0;
        string watermarkText = WatermarkText.Text;
        double watermarkOpacity = double.IsFinite(WatermarkOpacity.Value) ? WatermarkOpacity.Value / 100.0 : 0.2;
        // Everything that reads a control must happen here, on the UI thread, before the worker starts.
        CropOptions cropOptions = CurrentCropOptions();
        bool copyStreams = TrimModeBox.SelectedIndex == 0;
        if (op is "按上传上限压缩" or "按目标大小压缩" && maxBytes <= 0) { ShowError("请填写有效的大小上限。"); return; }
        double clipStart = 0, clipEnd = 0;
        if (op == "截取音视频片段")
        {
            if (!TryReadClipTime(TrimStartBox.Text, out clipStart, out string clipMessage)) { ShowError(clipMessage); return; }
            if (!TryReadClipTime(TrimEndBox.Text, out clipEnd, out clipMessage)) { ShowError(clipMessage); return; }
            if (clipEnd <= clipStart) { ShowError("终点必须晚于起点。"); return; }
        }
        if (op == "按名称搜索" && query.Length == 0) { ShowError("请输入要查找的名称。"); return; }
        if (op == "合并 PDF" && inputs.Length < 2) { ShowError("合并 PDF 至少需要两份文件。"); return; }
        if (op == "PDF 加水印" && watermarkText.Trim().Length == 0) { ShowError("请填写水印文字。"); return; }
        if (op is not "按名称搜索" and not "批量重命名" and not "撤销重命名" && string.IsNullOrWhiteSpace(output)) { ShowError("请选择输出文件夹。"); return; }
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        SetBusy(true);
        StatusInfo.IsOpen = false;
        ResultPanel.Visibility = Visibility.Collapsed;
        OpenOutputButton.IsEnabled = false;
        TaskProgress.Value = 0;
        IProgress<ToolProgress> progress = new Progress<ToolProgress>(report =>
        {
            if (!_busy || token.IsCancellationRequested) return;
            TaskProgress.Value = Math.Clamp(report.Percent, 0, 100);
            ProgressText.Text = report.Message;
        });
        try
        {
            if (op == "批量重命名")
            {
                var service = new FileService();
                var preview = service.PreviewRename(inputs, renamePrefix, renameStart);
                var dialog = new ContentDialog
                {
                    XamlRoot = RootLayout.XamlRoot,
                    Title = $"确认重命名 {preview.Count} 个文件",
                    Content = new ScrollViewer { MaxHeight = 320, Content = new TextBlock { Text = string.Join("\n", preview.Select(item => $"{Path.GetFileName(item.SourcePath)} → {Path.GetFileName(item.TargetPath)}")), TextWrapping = TextWrapping.Wrap } },
                    PrimaryButtonText = "确认重命名",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) { ProgressText.Text = "已取消，文件名称未更改"; return; }
                string journal = await Task.Run(() => service.ApplyRenameAsync(preview, token), token);
                Complete(new UiResult($"已重命名 {preview.Count} 个文件。\n恢复记录：{journal}", Path.GetDirectoryName(journal)));
                return;
            }
            var result = await Task.Run(async () =>
            {
                var compression = new CompressionService();
                var pdf = new PdfService();
                switch (op)
                {
                    case "按上传上限压缩":
                        var compressed = await compression.CompressAsync(inputs[0], output, maxBytes, lossy, resize, progress, token);
                        return new UiResult($"原始：{Size(compressed.OriginalBytes)} → 输出：{Size(compressed.OutputBytes)}\n文件数：{compressed.FileCount} · {(compressed.ReachedTarget ? "已达到上传上限" : "未达到上传上限")}\n{compressed.Message}\n{compressed.OutputPath}", output, !compressed.ReachedTarget);
                    case "无损 ZIP 打包":
                        return Saved(await compression.CreateZipAsync(inputs[0], output, progress, token));
                    case "ZIP 解压":
                    case "解压 7z / RAR":
                    case "解压压缩包":
                        var archivePath = inputs[0];
                        var extension = Path.GetExtension(archivePath);
                        if (string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            try { return Saved(await compression.ExtractZipAsync(archivePath, output, progress, token)); }
                            catch (Exception) when (extension is ".zip") { /* fall through to 7z for encrypted/non-standard zips */ }
                        }
                        return Saved(await new SevenZipService().ExtractAsync(archivePath, output, progress, token));
                    case "PDF 转图片":
                        bool longImage = PdfModeBox.SelectedIndex == 1;
                        return SavedMany(await pdf.ExportImagesAsync(inputs[0], output, dpi, longImage, progress, token), output);
                    case "PDF 转可编辑 Word":
                    case "PDF 转可编辑 PPT":
                        string pdfReport = "";
                        var pdfProgress = new ImmediateToolProgress(report => { pdfReport = report.Message; progress.Report(report); });
                        var converted = Saved(await pdf.ExportEditableAsync(inputs[0], output, op.EndsWith("Word") ? "docx" : "pptx", pdfProgress, token, preservePdfLayout));
                        return converted with { Message = converted.Message + "\n" + pdfReport };
                    case "合并 PDF":
                        return Saved(await pdf.MergeAsync(inputs, output, progress, token));
                    case "拆分 PDF":
                        return SavedMany(await pdf.SplitAsync(inputs[0], output, progress, token), output);
                    case "PDF 加水印":
                        return Saved(await pdf.AddWatermarkAsync(inputs[0], output, watermarkText, watermarkOpacity, progress, token));
                    case "PDF 压缩":
                        return Saved(await pdf.CompressAsync(inputs[0], output, progress, token));
                    case "Word 转 PDF":
                        return Saved(await new WordToPdfService().ConvertAsync(inputs[0], output, progress, token));
                    case "图片格式转换":
                        return SavedMany(await new ImageService().ConvertAsync(inputs, output, format, width, quality, progress, token), output);
                    case "图片裁剪":
                        return SavedMany(await new ImageCropService().CropAsync(inputs, output, cropOptions, quality, progress, token), output);
                    case "音视频格式转换":
                    case "按目标大小压缩":
                        return Saved(await new MediaService().ConvertAsync(inputs[0], output, format, op == "按目标大小压缩" ? maxBytes : null, progress, token));
                    case "提取音轨":
                        return Saved(await new MediaService().ConvertAsync(inputs[0], output, format, null, progress, token));
                    case "截取音视频片段":
                        return Saved(await new MediaService().TrimAsync(inputs[0], output, clipStart, clipEnd, copyStreams, format, progress, token));
                    case "撤销重命名":
                        await new FileService().UndoRenameAsync(inputs[0], token);
                        return new UiResult("已根据记录恢复原文件名称。", Path.GetDirectoryName(inputs[0]));
                    default: throw new InvalidOperationException("请选择可用工具。");
                }
            }, token);
            Complete(result);
        }
        catch (OperationCanceledException) { ProgressText.Text = "已取消本次处理"; ShowStatus("处理已取消", "源文件保留。已完成的独立输出可能仍保留在输出目录。", InfoBarSeverity.Informational); }
        catch (Exception ex) { ProgressText.Text = "本次处理未完成"; ShowError(ex.Message); }
        finally { SetBusy(false); _cancellation.Dispose(); _cancellation = null; }
    }

    private static string Size(long bytes) => $"{bytes / 1_000_000d:0.00} MB";
    private static UiResult Saved(string path) => new($"已生成：{path}" + (File.Exists(path) ? $"\n实际大小：{Size(new FileInfo(path).Length)}" : ""), Directory.Exists(path) ? path : Path.GetDirectoryName(path));
    private static UiResult SavedMany(IReadOnlyList<string> paths, string output) => new($"已生成 {paths.Count} 个文件。\n" + string.Join("\n", paths.Take(15)) + (paths.Count > 15 ? "\n…" : ""), output);

    private void Complete(UiResult result)
    {
        TaskProgress.Value = 100;
        ProgressText.Text = result.Warning ? "处理结束 · 请检查结果" : "处理完成 · 请查看输出结果";
        ResultText.Text = result.Message;
        ResultPanel.Visibility = Visibility.Visible;
        _lastOutputDirectory = result.OutputDirectory;
        OpenOutputButton.IsEnabled = !string.IsNullOrWhiteSpace(_lastOutputDirectory);
        ShowStatus(result.Warning ? "请检查处理结果" : "处理完成", result.Warning ? "部分要求未满足，请查看详情后再提交。" : "可打开输出文件夹查看文件。", result.Warning ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RunButton.IsEnabled = !busy;
        OperationBox.IsEnabled = !busy;
        foreach (Control button in OperationButtons.Children) button.IsEnabled = !busy;
        InputList.IsReadOnly = OcrInputList.IsReadOnly = busy;
        OcrAddButton.IsEnabled = OcrClearButton.IsEnabled = !busy;
        Navigation.IsPaneToggleButtonVisible = false;
        foreach (var item in Navigation.MenuItems.OfType<NavigationViewItem>()) item.IsEnabled = !busy;
        AddFilesButton.IsEnabled = AddFolderButton.IsEnabled = ClearButton.IsEnabled = !busy;
        foreach (Control control in new Control[] { TargetSize, AllowLossy, AllowResize, FormatBox, ImageQuality, PdfDpi, SearchQuery, ImagesOnly, RenamePrefix, RenameStart, OutputDirectory, ImageWidth, WatermarkText, WatermarkOpacity })
            control.IsEnabled = !busy;
        AllowResize.IsEnabled = !busy && AllowLossy.IsChecked == true;
        SearchScopeBox.IsEnabled = ChooseSearchFolderButton.IsEnabled = PdfOfficeMode.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
    }

    private sealed class ImmediateToolProgress(Action<ToolProgress> report) : IProgress<ToolProgress>
    {
        public void Report(ToolProgress value) => report(value);
    }

    private void Lossy_Changed(object sender, RoutedEventArgs args)
    {
        if (!_ready) return;
        AllowResize.IsEnabled = !_busy && AllowLossy.IsChecked == true;
        if (AllowLossy.IsChecked != true) AllowResize.IsChecked = false;
    }

    private void Cancel_Click(object sender, RoutedEventArgs args) { _cancellation?.Cancel(); CancelButton.IsEnabled = false; ProgressText.Text = "正在取消…"; }
    private void ShowError(string message) => ShowStatus("暂时无法完成", message, InfoBarSeverity.Error);
    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        StatusInfo.Title = title; StatusInfo.Message = message; StatusInfo.Severity = severity; StatusInfo.IsOpen = true;
    }
    private void OpenOutput_Click(object sender, RoutedEventArgs args) { if (_lastOutputDirectory != null) OpenPath(_lastOutputDirectory); }
    private void OpenPath(string path) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { ShowError(ex.Message); } }

    private async void Root_Loaded(object sender, RoutedEventArgs args)
    {
        var commandArgs = Environment.GetCommandLineArgs();
        int searchOneButtonIndex = Array.IndexOf(commandArgs, "--verify-search-one-button");
        if (searchOneButtonIndex >= 0)
        {
            if (searchOneButtonIndex + 1 >= commandArgs.Length) { Environment.Exit(2); return; }
            int result = await VerifySearchOneButtonAsync(commandArgs[searchOneButtonIndex + 1]);
            if (result != 0) Environment.Exit(result); else Close();
            return;
        }
        int webToolsIndex = Array.IndexOf(commandArgs, "--verify-web-tools");
        if (webToolsIndex >= 0)
        {
            if (webToolsIndex + 1 >= commandArgs.Length) { Environment.Exit(2); return; }
            int result = await VerifyWebToolsAsync(commandArgs[webToolsIndex + 1], webToolsIndex + 2 < commandArgs.Length ? commandArgs[webToolsIndex + 2] : null);
            if (result != 0) Environment.Exit(result); else Close();
            return;
        }
        int v110Index = Array.IndexOf(commandArgs, "--verify-v110");
        if (v110Index >= 0)
        {
            if (v110Index + 1 >= commandArgs.Length) { Environment.Exit(2); return; }
            int result = await VerifyV110Async(commandArgs[v110Index + 1], v110Index + 2 < commandArgs.Length ? commandArgs[v110Index + 2] : null);
            if (result != 0) Environment.Exit(result); else Close();
            return;
        }
        int v120Index = Array.IndexOf(commandArgs, "--verify-v120");
        if (v120Index >= 0)
        {
            if (v120Index + 1 >= commandArgs.Length) { Environment.Exit(2); return; }
            int result = await VerifyV120Async(commandArgs[v120Index + 1]);
            if (result != 0) Environment.Exit(result); else Close();
            return;
        }
        int recordingVerifyIndex = Array.IndexOf(commandArgs, "--verify-recording-ui");
        if (recordingVerifyIndex >= 0)
        {
            if (recordingVerifyIndex + 1 >= commandArgs.Length) { Environment.Exit(2); return; }
            int result = await VerifyRecordingUiAsync(commandArgs[recordingVerifyIndex + 1]);
            if (result != 0) Environment.Exit(result); else Close();
            return;
        }
        int fixVerifyIndex = Array.IndexOf(commandArgs, "--verify-v101");
        if (fixVerifyIndex >= 0)
        {
            if (fixVerifyIndex + 2 >= commandArgs.Length) { Environment.Exit(2); return; }
            int result = await VerifyV101Async(commandArgs[fixVerifyIndex + 1], commandArgs[fixVerifyIndex + 2]);
            if (result != 0) Environment.Exit(result); else Close();
            return;
        }
        int releaseVerifyIndex = Array.IndexOf(commandArgs, "--verify-v100");
        if (releaseVerifyIndex >= 0)
        {
            if (releaseVerifyIndex + 1 >= commandArgs.Length) { Environment.Exit(2); return; }
            int result = await VerifyV100Async(commandArgs[releaseVerifyIndex + 1]);
            if (result != 0) Environment.Exit(result); else Close();
            return;
        }
        int designVerifyIndex = Array.IndexOf(commandArgs, "--verify-v040");
        if (designVerifyIndex >= 0)
        {
            if (designVerifyIndex + 1 >= commandArgs.Length) { Environment.Exit(2); return; }
            int result = await VerifyV040Async(commandArgs[designVerifyIndex + 1], commandArgs.Contains("--full"));
            if (result != 0) Environment.Exit(result); else Close();
            return;
        }
        int shopVerifyIndex = Array.IndexOf(commandArgs, "--verify-shop");
        if (shopVerifyIndex >= 0)
        {
            if (shopVerifyIndex + 1 >= commandArgs.Length) { Environment.Exit(2); return; }
            int result = await VerifyShopAsync(commandArgs[shopVerifyIndex + 1]);
            if (result != 0) Environment.Exit(result); else Application.Current.Exit();
            return;
        }
        if (commandArgs.Contains("--shop")) Navigation.SelectedItem = ShopNavigationItem;
        int verifyIndex = Array.IndexOf(commandArgs, "--verify-ui");
        if (verifyIndex >= 0)
        {
            if (verifyIndex + 2 >= commandArgs.Length) { Environment.Exit(2); return; }
            int verified = await VerifyUiAsync(commandArgs[verifyIndex + 1], commandArgs[verifyIndex + 2]);
            if (verified != 0) Environment.Exit(verified); else Application.Current.Exit();
            return;
        }
        if (commandArgs.Contains("--software"))
        {
            Navigation.SelectedItem = Navigation.MenuItems[6];
            string? selectedId = commandArgs.FirstOrDefault(x => x.StartsWith("--select-app=", StringComparison.Ordinal))?[13..];
            await LoadUninstallerAsync(selectedId);
        }
        if (commandArgs.Contains("--recovery"))
        {
            Navigation.SelectedItem = Navigation.MenuItems[5];
            SelectCategory("files");
            OperationBox.SelectedIndex = 1;
        }
        int dirIndex = Array.IndexOf(commandArgs, "--screenshot-dir");
        int fileIndex = Array.IndexOf(commandArgs, "--screenshot");
        if (dirIndex < 0 && fileIndex < 0) return;
        int themeIndex = Array.IndexOf(commandArgs, "--theme");
        if (themeIndex >= 0 && themeIndex + 1 < commandArgs.Length)
            RootLayout.RequestedTheme = commandArgs[themeIndex + 1].Equals("dark", StringComparison.OrdinalIgnoreCase) ? ElementTheme.Dark : ElementTheme.Light;
        int exitCode = 0;
        try
        {
            await Task.Delay(800);
            if (dirIndex >= 0 && dirIndex + 1 < commandArgs.Length)
            {
                string directory = Path.GetFullPath(commandArgs[dirIndex + 1]);
                Directory.CreateDirectory(directory);
                foreach (var item in Navigation.MenuItems.OfType<NavigationViewItem>())
                {
                    Navigation.SelectedItem = item;
                    SelectCategory((string)item.Tag);
                    await Task.Delay(700);
                    await SaveScreenshot(Path.Combine(directory, item.Tag + ".png"));
                    if (WorkspaceScroll.ScrollableHeight > 0)
                    {
                        WorkspaceScroll.ChangeView(null, WorkspaceScroll.ScrollableHeight, null, true);
                        await Task.Delay(180);
                        await SaveScreenshot(Path.Combine(directory, item.Tag + "-options.png"));
                        WorkspaceScroll.ChangeView(null, 0, null, true);
                    }
                }
                Navigation.SelectedItem = Navigation.MenuItems[2];
                SelectCategory("image");
                OperationBox.SelectedIndex = 1;
                await Task.Delay(700);
                await SaveScreenshot(Path.Combine(directory, "ocr.png"));
            }
            else if (fileIndex >= 0 && fileIndex + 1 < commandArgs.Length)
                await SaveScreenshot(Path.GetFullPath(commandArgs[fileIndex + 1]));
        }
        catch (Exception ex)
        {
            exitCode = 1;
            App.LogException(ex, "screenshot");
            string errorPath = Path.Combine(AppContext.BaseDirectory, "screenshot-error.txt");
            try { await File.WriteAllTextAsync(errorPath, ex.ToString()); } catch { }
        }
        finally { if (exitCode != 0) Environment.Exit(exitCode); else Application.Current.Exit(); }
    }

    private Task SaveScreenshot(string path) => SaveElementScreenshot(path, RootLayout);

    private async Task SaveElementScreenshot(string path, UIElement element)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var buffer = await bitmap.GetPixelsAsync();
        byte[] pixels = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
    }
}
