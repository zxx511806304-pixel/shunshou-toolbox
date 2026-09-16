using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Shunshou.Core;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Pickers;

namespace Shunshou.App;

public sealed partial class VideoWorkspace : UserControl, IDisposable
{
    public nint HostWindowHandle { get; set; }
    public event EventHandler<bool>? BusyChanged;
    private readonly VideoDownloadService _download = new();
    private readonly VideoWatermarkService _watermark = new();
    private readonly VideoAiService _lightAi = new();
    private readonly VideoAiService _deepAi = new(model: VideoAiModel.Sttn);
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "Shunshou", "video-preview", Guid.NewGuid().ToString("N"));
    private CancellationTokenSource? _job;
    private VideoDownloadInfo? _downloadInfo;
    private VideoInspection? _inspection;
    private string? _input, _output;
    private MediaSource? _previewSource;
    private bool _watermarkMode, _syncingRegion, _dragging, _disposed;
    private Point _anchor;
    private sealed record QualityChoice(string Label, int? Height) { public override string ToString() => Label; }

    public VideoWorkspace()
    {
        InitializeComponent();
        var player = new MediaPlayer { AutoPlay = false };
        player.MediaFailed += (_, error) => DispatcherQueue.TryEnqueue(() => { if (!_disposed) ShowError("预览播放失败：" + error.ErrorMessage); });
        PreviewPlayer.SetMediaPlayer(player);
        OutputBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ShunshouToolbox", "Output");
        ModeBox.SelectedIndex = 0;
        EngineBox.SelectedIndex = ProviderBox.SelectedIndex = 0;
    }

    public void SelectMode(bool watermark)
    {
        _watermarkMode = watermark;
        DownloadArea.Visibility = watermark ? Visibility.Collapsed : Visibility.Visible;
        WatermarkArea.Visibility = watermark ? Visibility.Visible : Visibility.Collapsed;
        StartButton.Content = watermark ? "导出视频" : "开始下载";
        PreviewPlayer.MediaPlayer?.Pause();
        UpdateEnabled();
    }

    public void PausePreview() => PreviewPlayer.MediaPlayer?.Pause();

    private void UpdateEnabled()
    {
        bool idle = _job is null;
        UrlBox.IsEnabled = AnalyzeButton.IsEnabled = PickVideoButton.IsEnabled = OutputBox.IsEnabled = OutputBrowseButton.IsEnabled = idle;
        ModeBox.IsEnabled = RegionX.IsEnabled = RegionY.IsEnabled = RegionWidth.IsEnabled = RegionHeight.IsEnabled = idle;
        EngineBox.IsEnabled = ProviderBox.IsEnabled = idle;
        SelectionCanvas.IsHitTestVisible = idle;
        QualityBox.IsEnabled = idle && _downloadInfo is not null;
        PreviewButton.IsEnabled = EditRegionButton.IsEnabled = idle && _inspection is not null;
        StartButton.IsEnabled = idle && (_watermarkMode ? _inspection is not null : _downloadInfo is not null);
        CancelButton.IsEnabled = !idle;
    }

    private void Url_Changed(object sender, TextChangedEventArgs e)
    {
        _downloadInfo = null;
        if (StartButton is null) return;
        VideoTitle.Text = "支持公开视频链接";
        VideoDetail.Text = "下载后可继续转换格式、压缩或处理水印。";
        QualityBox.ItemsSource = null;
        UpdateEnabled();
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e) => await RunJobAsync(async (progress, ct) =>
    {
        StatusText.Text = "正在解析视频信息…";
        var info = await _download.AnalyzeAsync(UrlBox.Text.Trim(), ct);
        _downloadInfo = info;
        VideoTitle.Text = info.Title;
        VideoDetail.Text = info.Duration is > 0 ? $"时长 {TimeSpan.FromSeconds(info.Duration.Value):hh\\:mm\\:ss}" : "请选择下载画质";
        QualityBox.ItemsSource = new[] { new QualityChoice("最佳可用画质", null) }.Concat(info.AvailableHeights.Select(h => new QualityChoice($"最高 {h}p", h))).ToArray();
        QualityBox.SelectedIndex = 0;
        StatusText.Text = "解析完成";
    });

    private async void PickVideo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            foreach (var extension in new[] { ".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".ts" }) picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, HostWindowHandle);
            var file = await picker.PickSingleFileAsync();
            if (file is not null) await SetInputAsync(file.Path);
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    public async Task SetInputAsync(string path)
    {
        if (_job is not null) return;
        await RunJobAsync(async (progress, ct) =>
        {
            Directory.CreateDirectory(_scratch);
            var info = await _watermark.InspectAsync(path, Path.Combine(_scratch, Guid.NewGuid().ToString("N") + ".png"), ct);
            ClearPreviewSource();
            PreviewPlayer.Visibility = Visibility.Collapsed;
            _inspection = info; _input = Path.GetFullPath(path);
            SelectionCanvas.Width = FrameImage.Width = info.Width;
            SelectionCanvas.Height = FrameImage.Height = info.Height;
            FrameImage.Source = new BitmapImage(new Uri(info.PreviewPath));
            InputName.Text = Path.GetFileName(path);
            ToolTipService.SetToolTip(InputName, _input);
            int inset = info.Width >= 8 && info.Height >= 8 ? 2 : 0;
            SetRegion(new(inset, inset, Math.Max(2, info.Width / 5), Math.Clamp(info.Height / 8, 2, 128)));
            FrameView.Visibility = Visibility.Visible; EmptyPreview.Visibility = Visibility.Collapsed;
            StatusText.Text = $"{info.Width} × {info.Height} · {TimeSpan.FromSeconds(info.Duration):hh\\:mm\\:ss}";
        });
    }

    private VideoWatermarkMode SelectedMode => (VideoWatermarkMode)Math.Clamp(ModeBox.SelectedIndex, 0, 3);
    private VideoAiProvider SelectedProvider => (VideoAiProvider)Math.Clamp(ProviderBox.SelectedIndex, 0, 2);
    private void Engine_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (EngineHint is null) return;
        bool ai = EngineBox.SelectedIndex > 0;
        ProviderBox.Visibility = ai ? Visibility.Visible : Visibility.Collapsed;
        ModeBox.Visibility = ModeHint.Visibility = ai ? Visibility.Collapsed : Visibility.Visible;
        EngineHint.Text = EngineBox.SelectedIndex switch
        {
            1 => "轻量模型逐帧补全选区，适合小块水印；运动背景可能闪烁，请先预览。",
            2 => "结合相邻画面补全选区，适合更复杂的场景；处理时间与资源占用更高。",
            _ => "模糊、遮盖、裁剪或邻域修补，选择适合画面的处理方式。"
        };
        EditRegion_Click(this, new RoutedEventArgs());
    }
    private VideoRegion Region
    {
        get
        {
            if (new[] { RegionX.Value, RegionY.Value, RegionWidth.Value, RegionHeight.Value }.Any(v => !double.IsFinite(v)))
                throw new InvalidOperationException("请填写完整的选区坐标和尺寸。");
            return new((int)RegionX.Value, (int)RegionY.Value, (int)RegionWidth.Value, (int)RegionHeight.Value);
        }
    }
    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ModeHint is null) return;
        ModeHint.Text = SelectedMode switch
        {
            VideoWatermarkMode.Crop => "框选需要保留的画面，区域外的内容将被裁掉。",
            VideoWatermarkMode.Repair => "适合小块固定水印；复杂背景可能留痕，请先预览。",
            VideoWatermarkMode.Cover => "用纯色覆盖选区，适合遮挡标记或隐私信息。",
            _ => "将选区模糊处理；固定水印建议略多框入一点边缘。"
        };
        EditRegion_Click(this, new RoutedEventArgs());
    }

    private void SetRegion(VideoRegion region)
    {
        _syncingRegion = true;
        RegionX.Value = region.X; RegionY.Value = region.Y; RegionWidth.Value = region.Width; RegionHeight.Value = region.Height;
        _syncingRegion = false;
        DrawRegion();
    }

    private void Region_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_syncingRegion || _inspection is null) return;
        var values = new[] { RegionX.Value, RegionY.Value, RegionWidth.Value, RegionHeight.Value };
        if (values.Any(v => !double.IsFinite(v))) return;
        int x = (int)Math.Clamp(values[0], 0, _inspection.Width - 2), y = (int)Math.Clamp(values[1], 0, _inspection.Height - 2);
        SetRegion(new(x, y, (int)Math.Clamp(values[2], 2, _inspection.Width - x), (int)Math.Clamp(values[3], 2, _inspection.Height - y)));
        EditRegion_Click(this, new RoutedEventArgs());
    }

    private void DrawRegion()
    {
        if (_inspection is null) return;
        var r = Region;
        Canvas.SetLeft(SelectionBorder, r.X); Canvas.SetTop(SelectionBorder, r.Y);
        SelectionBorder.Width = r.Width; SelectionBorder.Height = r.Height;
        SelectionBorder.BorderThickness = new Thickness(Math.Max(2, _inspection.Width / 300d));
    }
    private Point ClampedPoint(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(SelectionCanvas).Position;
        return new(Math.Clamp(p.X, 0, _inspection!.Width), Math.Clamp(p.Y, 0, _inspection.Height));
    }
    private void Region_Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (_inspection is null || _job is not null || !e.GetCurrentPoint(SelectionCanvas).Properties.IsLeftButtonPressed) return;
        _anchor = ClampedPoint(e); _dragging = SelectionCanvas.CapturePointer(e.Pointer); e.Handled = true;
    }
    private void Region_Moved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var p = ClampedPoint(e);
        int x = Math.Min(_inspection!.Width - 2, (int)Math.Min(_anchor.X, p.X));
        int y = Math.Min(_inspection.Height - 2, (int)Math.Min(_anchor.Y, p.Y));
        SetRegion(new(x, y, Math.Clamp((int)Math.Abs(p.X - _anchor.X), 2, _inspection.Width - x), Math.Clamp((int)Math.Abs(p.Y - _anchor.Y), 2, _inspection.Height - y)));
        e.Handled = true;
    }
    private void Region_Released(object sender, PointerRoutedEventArgs e) { Region_Moved(sender, e); _dragging = false; SelectionCanvas.ReleasePointerCapture(e.Pointer); }
    private void Region_CaptureLost(object sender, PointerRoutedEventArgs e) => _dragging = false;
    private void EditRegion_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewPlayer is null) return;
        PreviewPlayer.MediaPlayer?.Pause(); PreviewPlayer.Visibility = Visibility.Collapsed;
        FrameView.Visibility = _inspection is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Preview_Click(object sender, RoutedEventArgs e) => await RunJobAsync(async (progress, ct) =>
    {
        if (_input is null) throw new InvalidOperationException("请先选择视频。");
        var result = await ProcessVideoAsync(_scratch, true, progress, ct);
        SetPreviewSource(result.Path);
        PreviewPlayer.Visibility = Visibility.Visible; FrameView.Visibility = Visibility.Collapsed;
        StatusText.Text = "预览已生成 · 点击播放检查效果" + result.Detail;
    });

    private async void Start_Click(object sender, RoutedEventArgs e) => await RunJobAsync(async (progress, ct) =>
    {
        if (string.IsNullOrWhiteSpace(OutputBox.Text)) throw new InvalidOperationException("请选择保存文件夹。");
        string detail = "";
        if (_watermarkMode)
        {
            var result = await ProcessVideoAsync(OutputBox.Text, false, progress, ct);
            _output = result.Path; detail = result.Detail;
        }
        else
            _output = await _download.DownloadAsync(_downloadInfo?.Url ?? throw new InvalidOperationException("请先解析链接。"), OutputBox.Text, (QualityBox.SelectedItem as QualityChoice)?.Height, progress, ct);
        Progress.Value = 100; OpenButton.IsEnabled = true;
        StatusText.Text = "已完成" + detail + " · " + Path.GetFileName(_output);
        MessageBar.Title = "文件已保存"; MessageBar.Message = _output + (detail.Length > 0 ? "\n运行设备：" + detail.TrimStart(' ', '·') : ""); MessageBar.Severity = InfoBarSeverity.Success; MessageBar.IsOpen = true;
    });

    private async Task<(string Path, string Detail)> ProcessVideoAsync(string directory, bool preview,
        IProgress<ToolProgress> progress, CancellationToken ct)
    {
        var input = _input ?? throw new InvalidOperationException("请先选择视频。");
        if (EngineBox.SelectedIndex == 0)
            return (preview
                ? await _watermark.PreviewAsync(input, directory, SelectedMode, Region, progress, ct)
                : await _watermark.ExportAsync(input, directory, SelectedMode, Region, progress, ct), "");
        var service = EngineBox.SelectedIndex == 1 ? _lightAi : _deepAi;
        var result = preview
            ? await service.PreviewAsync(input, directory, Region, SelectedProvider, progress, ct)
            : await service.ExportAsync(input, directory, Region, SelectedProvider, progress, ct);
        if (result.UsedFallback)
        {
            MessageBar.Title = "已使用 CPU 完成";
            MessageBar.Message = "显卡加速未能运行，已自动切换 CPU。";
            MessageBar.Severity = InfoBarSeverity.Informational; MessageBar.IsOpen = true;
        }
        return (result.OutputPath, " · " + result.Provider + (result.UsedFallback ? "（显卡不可用，已自动回退）" : ""));
    }

    private async Task RunJobAsync(Func<IProgress<ToolProgress>, CancellationToken, Task> action)
    {
        if (_job is not null || _disposed) return;
        using var job = new CancellationTokenSource();
        _job = job; UpdateEnabled(); BusyChanged?.Invoke(this, true);
        MessageBar.IsOpen = false; Progress.Value = 0; Progress.IsIndeterminate = true;
        var progress = new Progress<ToolProgress>(p => { if (_job != job || job.IsCancellationRequested) return; Progress.IsIndeterminate = false; Progress.Value = Math.Clamp(p.Percent, 0, 100); StatusText.Text = p.Message; });
        try { await action(progress, job.Token); }
        catch (OperationCanceledException) { if (!_disposed) StatusText.Text = "已取消"; }
        catch (Exception ex) { if (!_disposed) { StatusText.Text = "本次处理未完成"; ShowError(ex.Message); } }
        finally { _job = null; if (!_disposed) { Progress.IsIndeterminate = false; UpdateEnabled(); BusyChanged?.Invoke(this, false); } }
    }
    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        try { var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, HostWindowHandle); var folder = await picker.PickSingleFolderAsync(); if (folder is not null) OutputBox.Text = folder.Path; }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private void ShowError(string message) { MessageBar.Title = "暂时无法完成"; MessageBar.Message = message; MessageBar.Severity = InfoBarSeverity.Error; MessageBar.IsOpen = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) { _job?.Cancel(); CancelButton.IsEnabled = false; StatusText.Text = "正在取消…"; }
    private void Open_Click(object sender, RoutedEventArgs e) { try { if (_output is not null) Process.Start(new ProcessStartInfo(Path.GetDirectoryName(_output)!) { UseShellExecute = true }); } catch (Exception ex) { ShowError(ex.Message); } }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _job?.Cancel();
        var player = PreviewPlayer.MediaPlayer;
        player?.Pause();
        ClearPreviewSource();
        PreviewPlayer.SetMediaPlayer(null);
        player?.Dispose();
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Shunshou", "video-preview"));
        if (Path.GetDirectoryName(Path.GetFullPath(_scratch)) == parent && Guid.TryParseExact(Path.GetFileName(_scratch), "N", out _))
            try { if (Directory.Exists(_scratch)) Directory.Delete(_scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void ClearPreviewSource()
    {
        PreviewPlayer.MediaPlayer?.Pause();
        PreviewPlayer.Source = null;
        _previewSource?.Dispose();
        _previewSource = null;
    }

    private void SetPreviewSource(string path)
    {
        ClearPreviewSource();
        _previewSource = MediaSource.CreateFromUri(new Uri(path));
        PreviewPlayer.Source = _previewSource;
    }
}
