using Microsoft.UI.Xaml;
using Windows.Media.Core;

namespace Shunshou.App;

public sealed partial class VideoWorkspace
{
    internal async Task PrepareCloseWhileAiAsync(string fixture, string output)
    {
        SelectMode(true);
        await VerifyLoadAsync(fixture);
        EngineBox.SelectedIndex = 2;
        ProviderBox.SelectedIndex = 1;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = RunJobAsync(async (progress, ct) =>
        {
            var forwarding = new Progress<Shunshou.Core.ToolProgress>(p =>
            {
                progress.Report(p);
                if (p.Percent > 5 && p.Percent < 85) started.TrySetResult();
            });
            await ProcessVideoAsync(output, false, forwarding, ct);
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Require(_job is not null, "Shutdown check needs an active AI task.");
        var processes = new List<object>();
        foreach (var name in new[] { "Shunshou.AiRunner", "ffmpeg" })
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
            {
                using (process)
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (path?.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase) == true)
                            processes.Add(new { process.Id, process.ProcessName, StartTicks = process.StartTime.ToUniversalTime().Ticks });
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
            }
        Require(processes.Count >= 2, "Shutdown check did not observe the packaged AI/codec processes.");
        await File.WriteAllTextAsync(Path.Combine(output, "closing-processes.json"), System.Text.Json.JsonSerializer.Serialize(processes));
    }

    internal void VerifySelectEngine(int index)
    {
        EngineBox.SelectedIndex = index;
        Require(EngineBox.Items.Count == 3, "Three processing engines must be selectable.");
        Require(ModeBox.Visibility == (index == 0 ? Visibility.Visible : Visibility.Collapsed), "Regular options overlapped AI options.");
        Require(ProviderBox.Visibility == (index == 0 ? Visibility.Collapsed : Visibility.Visible), "AI device choices were not shown.");
    }

    internal async Task VerifyAiWorkflowAsync(string fixture, string output)
    {
        SelectMode(true);
        await VerifyLoadAsync(fixture);
        ProviderBox.SelectedIndex = 1; // Deterministic UI routing check; provider acceleration is tested by the runner tests.
        foreach (int engine in new[] { 1, 2 })
        {
            VerifySelectEngine(engine);
            string? path = null;
            await RunJobAsync(async (progress, ct) =>
            {
                var result = await ProcessVideoAsync(output, true, progress, ct);
                Require(result.Detail.Contains("CPU"), "AI route did not return its actual provider.");
                await VerifyPreviewPlaybackAsync(result.Path, ct);
                path = result.Path;
            });
            Require(path is not null && File.Exists(path), $"AI engine {engine} failed to publish a real preview.");
            Require(_job is null && StartButton.IsEnabled && EngineBox.IsEnabled, "AI controls remained locked.");
        }
        ClearPreviewSource();
        ProviderBox.SelectedIndex = 0;
        VerifySelectEngine(0);
    }

    internal async Task VerifyLoadAsync(string fixture)
    {
        if (_input != Path.GetFullPath(fixture)) await SetInputAsync(fixture);
        Require(_inspection is not null && FrameImage.Source is not null && StartButton.IsEnabled, "Video frame failed to load.");
        Require(SelectionCanvas.Width == _inspection!.Width && SelectionCanvas.Height == _inspection.Height, "Preview coordinates differ from video pixels.");
        var region = Region;
        Require(region.Width >= 2 && region.Height >= 2 && region.X + region.Width <= _inspection.Width && region.Y + region.Height <= _inspection.Height, "Initial selection is out of bounds.");
    }

    internal async Task VerifyWorkflowAsync(string output)
    {
        SelectMode(true);
        ModeBox.SelectedIndex = 0;
        await RunJobAsync(async (progress, ct) =>
        {
            var preview = await _watermark.PreviewAsync(_input!, _scratch, SelectedMode, Region, progress, ct);
            // Exercise the same embedded playback used by the preview button, without external apps.
            await VerifyPreviewPlaybackAsync(preview, ct);
            _output = await _watermark.ExportAsync(_input!, output, SelectedMode, Region, progress, ct);
        });
        Require(_output is not null && File.Exists(_output), "UI video workflow did not publish an output.");
        Require(_job is null && StartButton.IsEnabled && CancelButton.IsEnabled == false, "UI controls remained locked after output.");
        SelectMode(false);
        UrlBox.Text = "file:///C:/private.mp4";
        await RunJobAsync(async (_, ct) => { _downloadInfo = await _download.AnalyzeAsync(UrlBox.Text, ct); });
        Require(MessageBar.IsOpen && _downloadInfo is null && !StartButton.IsEnabled && AnalyzeButton.IsEnabled, "Invalid URL error/state recovery failed.");
        UrlBox.Text = "";
        MessageBar.IsOpen = false;
    }

    private async Task VerifyPreviewPlaybackAsync(string path, CancellationToken ct)
    {
        PreviewPlayer.Visibility = Visibility.Visible;
        var player = PreviewPlayer.MediaPlayer;
        Require(player is not null, "Preview did not create a media player.");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Opened(Windows.Media.Playback.MediaPlayer _, object __) => ready.TrySetResult();
        void Failed(Windows.Media.Playback.MediaPlayer _, Windows.Media.Playback.MediaPlayerFailedEventArgs error)
            => ready.TrySetException(new InvalidOperationException(error.ErrorMessage));
        player!.MediaOpened += Opened;
        player.MediaFailed += Failed;
        try
        {
            SetPreviewSource(path);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            player.Pause();
            Require(player.PlaybackSession.NaturalVideoWidth > 0, "Preview player has no decoded video dimensions.");
        }
        finally { player.MediaOpened -= Opened; player.MediaFailed -= Failed; }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
