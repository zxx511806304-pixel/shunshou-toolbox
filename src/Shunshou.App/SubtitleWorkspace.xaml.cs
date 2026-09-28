using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Shunshou.Core;
using Windows.Storage.Pickers;
using Windows.System;

namespace Shunshou.App;

public sealed partial class SubtitleWorkspace : UserControl, IDisposable
{
    public nint HostWindowHandle { get; set; }
    public event EventHandler<bool>? BusyChanged;
    private readonly SubtitleDownloadService _service = new();
    private CancellationTokenSource? _job;
    private SubtitleDownloadInfo? _info;
    private string? _output;
    private bool _disposed;

    public SubtitleWorkspace()
    {
        InitializeComponent();
        OutputBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ShunshouToolbox", "Output");
        if (!NoticePreferences.IsDismissed("subtitle")) NoticeBar.IsOpen = true;
    }

    private void Notice_Closed(InfoBar sender, InfoBarClosedEventArgs args) => NoticePreferences.Dismiss("subtitle");

    private void Url_Changed(object sender, TextChangedEventArgs e)
    {
        _info = null;
        if (StartButton is null) return;
        TrackBox.ItemsSource = null;
        VideoTitle.Text = "从视频网页读取已有字幕";
        TrackHint.Text = "保留时间轴，或导出纯文字。可选语言以网站提供的字幕为准。";
        StatusText.Text = "准备就绪";
        UpdateEnabled();
    }

    private async void Url_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await RunJobAsync((_, ct) => AnalyzeAsync(ct));
    }
    private async void Analyze_Click(object sender, RoutedEventArgs e) => await RunJobAsync((_, ct) => AnalyzeAsync(ct));
    private async Task AnalyzeAsync(CancellationToken ct)
    {
        StatusText.Text = "正在读取网页字幕…";
        var info = await _service.AnalyzeAsync(UrlBox.Text, ct);
        if (_disposed) return;
        _info = info;
        VideoTitle.Text = info.Title;
        TrackBox.ItemsSource = info.Tracks;
        TrackBox.SelectedIndex = info.Tracks.Count > 0 ? 0 : -1;
        TrackHint.Text = info.Tracks.Count == 0 ? "这个视频没有可下载的字幕。" : $"找到 {info.Tracks.Count} 条字幕" +
            (info.Tracks.Any(track => track.IsAutomatic) ? " · 自动字幕可能有识别误差。" : " · 选择语言后即可保存。");
        StatusText.Text = info.Tracks.Count == 0 ? "未找到字幕" : "请选择字幕语言和保存格式";
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await RunJobAsync(DownloadAsync);
    private async Task DownloadAsync(IProgress<ToolProgress> progress, CancellationToken ct)
    {
        if (_info is null || TrackBox.SelectedItem is not SubtitleTrack track) throw new InvalidOperationException("请先读取并选择字幕。");
        var output = await _service.DownloadAsync(_info, track.Key, (SubtitleOutputFormat)Math.Clamp(FormatBox.SelectedIndex, 0, 2), OutputBox.Text, progress, ct);
        if (_disposed) return;
        _output = output; OpenButton.IsEnabled = true;
        StatusText.Text = "已保存 · " + Path.GetFileName(output);
    }

    private void UpdateEnabled()
    {
        bool idle = _job is null;
        UrlBox.IsEnabled = AnalyzeButton.IsEnabled = FormatBox.IsEnabled = OutputBox.IsEnabled = BrowseButton.IsEnabled = idle;
        TrackBox.IsEnabled = StartButton.IsEnabled = idle && _info?.Tracks.Count > 0;
        CancelButton.IsEnabled = !idle;
    }
    private async Task RunJobAsync(Func<IProgress<ToolProgress>, CancellationToken, Task> action)
    {
        if (_job is not null || _disposed) return;
        using var job = new CancellationTokenSource();
        _job = job; UpdateEnabled(); BusyChanged?.Invoke(this, true);
        MessageBar.IsOpen = false; Progress.Value = 0; Progress.IsIndeterminate = true;
        var progress = new Progress<ToolProgress>(value =>
        {
            if (_disposed || _job != job || job.IsCancellationRequested) return;
            Progress.IsIndeterminate = false; Progress.Value = Math.Clamp(value.Percent, 0, 100); StatusText.Text = value.Message;
        });
        try { await action(progress, job.Token); }
        catch (OperationCanceledException) { if (!_disposed) StatusText.Text = "已取消"; }
        catch (Exception ex) { if (!_disposed) { StatusText.Text = "本次处理未完成"; ShowError(ex.Message); } }
        finally { _job = null; if (!_disposed) { Progress.IsIndeterminate = false; UpdateEnabled(); BusyChanged?.Invoke(this, false); } }
    }
    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, HostWindowHandle);
            var folder = await picker.PickSingleFolderAsync();
            if (!_disposed && folder is not null) OutputBox.Text = folder.Path;
        }
        catch (Exception ex) { if (!_disposed) ShowError(ex.Message); }
    }
    private void ShowError(string message) { MessageBar.Title = "暂时无法完成"; MessageBar.Message = message; MessageBar.Severity = InfoBarSeverity.Error; MessageBar.IsOpen = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) { _job?.Cancel(); CancelButton.IsEnabled = false; StatusText.Text = "正在取消…"; }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        try { if (_output is not null) Process.Start(new ProcessStartInfo(Path.GetDirectoryName(_output)!) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _job?.Cancel(); }

    internal async Task<int> VerifyAnalyzeAsync(string url)
    {
        UrlBox.Text = url;
        await RunJobAsync((_, ct) => AnalyzeAsync(ct));
        if (MessageBar.IsOpen) throw new InvalidOperationException(MessageBar.Message);
        return _info?.Tracks.Count ?? 0;
    }
    internal async Task<string> VerifyDownloadAsync(int trackIndex, SubtitleOutputFormat format, string directory)
    {
        TrackBox.SelectedIndex = trackIndex; FormatBox.SelectedIndex = (int)format; OutputBox.Text = directory;
        await RunJobAsync(DownloadAsync);
        if (MessageBar.IsOpen || _output is null) throw new InvalidOperationException(MessageBar.Message ?? "没有字幕输出");
        return _output;
    }
}
