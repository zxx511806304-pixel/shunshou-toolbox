using System.Collections.ObjectModel;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Shunshou.Core;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Shunshou.App;

public sealed partial class RecoveryWorkspace : UserControl, IDisposable
{
    private readonly RecoveryService _service = new();
    private readonly ObservableCollection<RecoveryCandidate> _rows = [];
    private IReadOnlyList<RecoveryCandidate> _all = [];
    private string? _sourceFile;
    private string? _scanSource;
    private RecoveryCandidate? _preview;
    private CancellationTokenSource? _operation;
    private int _previewVersion;
    private bool _ready;
    public nint HostWindowHandle { get; set; }
    public event EventHandler<bool>? BusyChanged;
    public event EventHandler? RequestElevation;
    private RecoveryMode Mode => Enum.Parse<RecoveryMode>((string)((ComboBoxItem)ModeBox.SelectedItem).Tag);
    private string Source => _sourceFile ?? DriveBox.SelectedItem as string ?? "";

    public RecoveryWorkspace()
    {
        InitializeComponent();
        Results.ItemsSource = _rows;
        DriveBox.ItemsSource = RecoveryService.Drives();
        DriveBox.SelectedIndex = 0;
        ModeBox.SelectedIndex = 0;
        _ready = true;
        UpdateMode();
    }

    public void SelectSource(string path)
    {
        if (_operation != null) return;
        ResetResults();
        if (Directory.Exists(path) && !RecoveryService.IsDrive(path)) ModeBox.SelectedIndex = 4;
        else if (File.Exists(path) && Path.GetExtension(path).ToLowerInvariant() is ".img" or ".dd" or ".raw") ModeBox.SelectedIndex = 0;
        else throw new InvalidOperationException("请添加磁盘镜像（img、dd、raw）或已有备份文件夹。");
        _sourceFile = Path.GetFullPath(path);
        SourceLabel.Text = _sourceFile;
    }

    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        bool keepImage = Mode is RecoveryMode.Combined or RecoveryMode.FileRecords or RecoveryMode.DeepScan &&
            _sourceFile is { } path && Path.GetExtension(path).ToLowerInvariant() is ".img" or ".dd" or ".raw" && !Directory.Exists(path);
        if (!keepImage) _sourceFile = null;
        ResetResults(); UpdateMode();
    }
    private void Drive_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) { _sourceFile = null; ResetResults(); SourceLabel.Text = Source; } }
    private void ResetResults()
    {
        _all = []; _scanSource = null; ApplyFilter();
        EmptyLabel.Text = "选择来源，开始查找";
        ProgressText.Text = "准备就绪";
        Notice.IsOpen = false;
    }
    private void UpdateMode()
    {
        bool backup = Mode == RecoveryMode.BackupFolder;
        DriveBox.IsEnabled = !backup;
        SourceButton.Visibility = Mode == RecoveryMode.RecycleBin ? Visibility.Collapsed : Visibility.Visible;
        SourceButton.Content = backup ? "选择备份文件夹" : "选择镜像";
        SourceLabel.Text = backup ? "选择此前保存的备份文件夹" : Source;
        ModeHint.Text = Mode switch
        {
            RecoveryMode.Combined => "依次查找回收站、删除记录和文件内容。建议从其他磁盘运行工具箱，候选文件保存到所选位置。",
            RecoveryMode.RecycleBin => "查找当前用户的回收站。恢复时复制文件，保留回收站中的原件。",
            RecoveryMode.FileRecords => "读取 NTFS 删除记录并保存候选文件，随后按原名筛选、预览。保存位置需在另一个磁盘。",
            RecoveryMode.DeepScan => "扫描整个分区，可能找到现有文件及失去原名的文件。候选文件直接保存到所选位置，可按格式筛选。",
            _ => "读取已有备份中的文件，不会新建备份或更改原备份。"
        };
        ScanButton.Content = Mode is RecoveryMode.RecycleBin or RecoveryMode.BackupFolder ? "开始查找" : "开始扫描";
    }

    private async void ChooseSource_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Mode == RecoveryMode.BackupFolder)
            {
                string? path = await PickFolderAsync();
                if (path != null) { ResetResults(); _sourceFile = path; SourceLabel.Text = path; }
            }
            else
            {
                var picker = new FileOpenPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, HostWindowHandle);
                foreach (var extension in new[] { ".img", ".dd", ".raw" }) picker.FileTypeFilter.Add(extension);
                var file = await picker.PickSingleFileAsync();
                if (file != null) { ResetResults(); _sourceFile = file.Path; SourceLabel.Text = file.Path; }
            }
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, HostWindowHandle);
        return (await picker.PickSingleFolderAsync())?.Path;
    }
    private async void ChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        try { if (await PickFolderAsync() is { } path) OutputFolder.Text = path; }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_operation != null) return;
        AppPaths.DisableLogging();
        if (RecoveryService.IsDrive(Source) && Mode is RecoveryMode.Combined or RecoveryMode.FileRecords or RecoveryMode.DeepScan && !RecoveryService.IsAdministrator)
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "需要管理员权限", Content = "读取磁盘删除记录需要管理员权限。将打开顺手工具箱的管理员窗口，请在新窗口选择磁盘和保存位置。", PrimaryButtonText = "打开管理员窗口", CloseButtonText = "取消" };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) RequestElevation?.Invoke(this, EventArgs.Empty);
            return;
        }
        string source = Source;
        var request = new RecoveryRequest(source, OutputFolder.Text, Mode);
        await WithBusyAsync(async ct =>
        {
            _all = []; _scanSource = null; ApplyFilter();
            var result = await _service.ScanAsync(request, new Progress<RecoveryProgress>(p => ProgressText.Text = p.Message), ct);
            _all = result.Files; _scanSource = source; ApplyFilter();
            ProgressText.Text = result.Message;
            if (result.SessionDirectory != null)
            {
                Notice.Title = "候选文件已保存"; Notice.Message = result.SessionDirectory;
                Notice.Severity = InfoBarSeverity.Informational; Notice.IsOpen = true;
            }
        });
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e) { if (_ready) ApplyFilter(); }
    private void ApplyFilter()
    {
        _rows.Clear();
        foreach (var row in RecoveryService.Filter(_all, NameFilter.Text, TypeFilter.Text)) _rows.Add(row);
        CountLabel.Text = $"找到的文件 · {_rows.Count:N0} / {_all.Count:N0}";
        EmptyLabel.Text = _all.Count == 0 ? "没有找到文件，可换一种方式查找" : "没有符合筛选条件的文件";
        EmptyLabel.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.IsEnabled = false;
    }
    private void SelectAll_Click(object sender, RoutedEventArgs e) { if (_operation == null) Results.SelectAll(); }
    private async void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        CopyButton.IsEnabled = _operation == null && Results.SelectedItems.Count > 0;
        _preview = e.AddedItems.OfType<RecoveryCandidate>().LastOrDefault() ?? Results.SelectedItems.OfType<RecoveryCandidate>().LastOrDefault();
        int version = ++_previewVersion;
        PreviewImage.Source = null; PreviewText.Text = ""; PreviewText.Visibility = Visibility.Collapsed;
        OcrButton.IsEnabled = false;
        PreviewName.Text = _preview?.Name ?? "选中一个文件查看";
        PreviewStatus.Text = "";
        if (_preview is not { } file) return;
        try
        {
            if (file.Extension is "jpg" or "jpeg" or "png" or "bmp" or "gif" or "tif" or "tiff")
            {
                RecoveryService.RejectReparseAncestors(file.StoredPath);
                using var stream = await (await StorageFile.GetFileFromPathAsync(file.StoredPath)).OpenReadAsync();
                var bitmap = new BitmapImage { DecodePixelWidth = 480 };
                await bitmap.SetSourceAsync(stream);
                if (version != _previewVersion) return;
                PreviewImage.Source = bitmap;
                OcrButton.IsEnabled = _operation == null && file.Extension is "jpg" or "jpeg" or "png" or "bmp" or "gif";
                PreviewStatus.Text = "图片可以预览，请检查实际内容是否完整。";
            }
            else if (file.Extension is "txt" or "csv" or "md" or "json" or "log" or "xml")
            {
                RecoveryService.RejectReparseAncestors(file.StoredPath);
                using var reader = new StreamReader(file.StoredPath, Encoding.UTF8, true);
                var buffer = new char[12000]; int read = await reader.ReadAsync(buffer);
                if (version != _previewVersion) return;
                PreviewText.Text = new string(buffer, 0, read); PreviewText.Visibility = Visibility.Visible;
                PreviewStatus.Text = "文字预览（最多 12,000 字），尚未验证完整文件。";
            }
            else PreviewStatus.Text = file.Detail + "。可恢复后使用对应软件检查内容。";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { if (version == _previewVersion) PreviewStatus.Text = "暂时无法预览，文件可能不完整或当前格式不支持预览。"; }
    }

    private async void ReadImageText_Click(object sender, RoutedEventArgs e)
    {
        if (_preview is not { } file) return;
        if (file.Extension is not ("jpg" or "jpeg" or "png" or "bmp" or "gif")) return;
        AppPaths.DisableLogging();
        int version = _previewVersion;
        await WithBusyAsync(async ct =>
        {
            ProgressText.Text = "正在识别图中文字…";
            string text = await new OcrService().RecognizeAsync(file.StoredPath, ct);
            if (version != _previewVersion) return;
            PreviewText.Text = text; PreviewText.Visibility = Visibility.Visible;
            ProgressText.Text = string.IsNullOrWhiteSpace(text) ? "未识别到文字" : "识别完成，可选中文字复制";
        });
    }

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "exe", "dll", "bat", "cmd", "ps1", "vbs", "js", "msi", "scr", "com" };

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        var selected = Results.SelectedItems.OfType<RecoveryCandidate>().ToArray();
        if (selected.Length == 0 || _scanSource == null) return;
        await WithBusyAsync(async ct =>
        {
            ProgressText.Text = "正在复制恢复选中的文件…";
            var outputs = await RecoveryService.CopySelectedAsync(selected, _scanSource, OutputFolder.Text, ct);
            ProgressText.Text = $"已恢复 {outputs.Count:N0} 个文件，原件保留。";
            int executables = selected.Count(f => ExecutableExtensions.Contains(f.Extension));
            if (executables > 0)
            {
                Notice.Title = "包含可执行文件";
                Notice.Message = $"本次恢复的选中文件中有 {executables:N0} 个可执行文件（exe/dll/bat 等）。删除已久的可执行文件可能来自不再信任的软件，请确认来源后再运行。";
                Notice.Severity = InfoBarSeverity.Warning;
                Notice.IsOpen = true;
            }
        });
    }

    private async Task WithBusyAsync(Func<CancellationToken, Task> work)
    {
        if (_operation != null) return;
        _operation = new(); SetBusy(true); Notice.IsOpen = false;
        try { await work(_operation.Token); }
        catch (OperationCanceledException) { ProgressText.Text = "已停止"; }
        catch (Exception ex) { ShowError(ex.Message); ProgressText.Text = "未完成"; }
        finally { _operation.Dispose(); _operation = null; SetBusy(false); }
    }
    private void SetBusy(bool busy)
    {
        ModeBox.IsEnabled = SourceButton.IsEnabled = OutputButton.IsEnabled = OutputFolder.IsEnabled = ScanButton.IsEnabled = !busy;
        NameFilter.IsEnabled = TypeFilter.IsEnabled = !busy;
        DriveBox.IsEnabled = !busy && Mode != RecoveryMode.BackupFolder;
        Results.IsEnabled = !busy; CancelButton.IsEnabled = busy; Progress.IsIndeterminate = busy;
        CopyButton.IsEnabled = !busy && Results.SelectedItems.Count > 0;
        OcrButton.IsEnabled = !busy && PreviewImage.Source != null && _preview?.Extension is "jpg" or "jpeg" or "png" or "bmp" or "gif";
        BusyChanged?.Invoke(this, busy);
    }
    private void ShowError(string text) { Notice.Title = "请检查"; Notice.Message = text; Notice.Severity = InfoBarSeverity.Warning; Notice.IsOpen = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    public void Dispose() { _operation?.Cancel(); ++_previewVersion; }
}
