using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shunshou.Core;
using Windows.Storage.Pickers;

namespace Shunshou.App;

/// <summary>Rescues unsaved Office/WPS documents and extracts text and media from damaged OOXML files. All processing is local.</summary>
public sealed partial class OfficeRescueWorkspace : UserControl
{
    private readonly OfficeRescueService _service = new();
    private readonly ObservableCollection<RescueRow> _rows = new();
    private string? _damagedFile;
    private string? _lastOutputFolder;

    public OfficeRescueWorkspace()
    {
        InitializeComponent();
        Results.ItemsSource = _rows;
    }

    public nint HostWindowHandle { get; set; }
    public event EventHandler<bool>? BusyChanged;

    internal sealed class RescueRow
    {
        public RescueRow(RescuableDocument document)
        {
            Document = document;
            Name = System.IO.Path.GetFileName(document.Path);
            ModifiedText = document.Modified.ToString("yyyy-MM-dd HH:mm");
            SizeText = FormatSize(document.Bytes);
        }

        public RescuableDocument Document { get; }
        public string App => Document.App;
        public string Name { get; }
        public string Path => Document.Path;
        public string ModifiedText { get; }
        public string SizeText { get; }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B"
    };

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ScanNotice.IsOpen = false;
        SetBusy(true);
        try
        {
            var found = await _service.ScanUnsavedAsync(default);
            _rows.Clear();
            foreach (var doc in found) _rows.Add(new RescueRow(doc));
            EmptyLabel.Text = "没有发现可恢复的自动保存文件";
            EmptyLabel.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ScanStatus.Text = _rows.Count == 0 ? "" : $"找到 {_rows.Count} 个可能未保存的文件，选中后可复制到安全位置";
        }
        catch (Exception ex)
        {
            ShowScanNotice("扫描失败", ex.Message, InfoBarSeverity.Warning);
        }
        finally { SetBusy(false); }
    }

    private void Results_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SaveButton.IsEnabled = Results.SelectedItem is RescueRow;

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Results.SelectedItem is not RescueRow row) return;
        try
        {
            ScanNotice.IsOpen = false;
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, HostWindowHandle);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            string saved = await OfficeRescueService.CopyToSafetyAsync(row.Document, folder.Path);
            ShowScanNotice("已保存副本", "已复制到：" + saved, InfoBarSeverity.Success);
            ScanStatus.Text = "已保存副本 · " + System.IO.Path.GetFileName(saved);
        }
        catch (Exception ex)
        {
            ShowScanNotice("保存失败", ex.Message, InfoBarSeverity.Warning);
        }
    }

    private async void Pick_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            foreach (string extension in new[] { ".docx", ".xlsx", ".pptx" }) picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, HostWindowHandle);
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            _damagedFile = file.Path;
            var info = new FileInfo(file.Path);
            SelectedFileLabel.Text = $"{file.Name} · {FormatSize(info.Length)} · {file.Path}";
            ExtractButton.IsEnabled = true;
            OpenOutputButton.Visibility = Visibility.Collapsed;
            ExtractNotice.IsOpen = false;
        }
        catch (Exception ex)
        {
            ShowExtractNotice("无法选择文档", ex.Message, InfoBarSeverity.Warning);
        }
    }

    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        if (_damagedFile is null) return;
        try
        {
            ExtractNotice.IsOpen = false;
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, HostWindowHandle);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            SetBusy(true);
            try
            {
                var result = await OfficeRescueService.ExtractDamagedDocumentAsync(_damagedFile, folder.Path, default);
                _lastOutputFolder = result.OutputFolder;
                ShowExtractNotice("提取完成",
                    $"取出 {result.TextBlocks} 段文字、{result.MediaFiles} 个媒体文件，保存在：{result.OutputFolder}",
                    InfoBarSeverity.Success);
                OpenOutputButton.Visibility = Visibility.Visible;
            }
            finally { SetBusy(false); }
        }
        catch (Exception ex)
        {
            ShowExtractNotice("提取失败", ex.Message, InfoBarSeverity.Warning);
        }
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutputFolder is null) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{_lastOutputFolder}\"", UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowExtractNotice("无法打开文件夹", ex.Message, InfoBarSeverity.Warning);
        }
    }

    private void SetBusy(bool busy)
    {
        ScanButton.IsEnabled = PickButton.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy && Results.SelectedItem is RescueRow;
        ExtractButton.IsEnabled = !busy && _damagedFile is not null;
        BusyChanged?.Invoke(this, busy);
    }

    private void ShowScanNotice(string title, string message, InfoBarSeverity severity)
    {
        ScanNotice.Title = title;
        ScanNotice.Message = message;
        ScanNotice.Severity = severity;
        ScanNotice.IsOpen = true;
    }

    private void ShowExtractNotice(string title, string message, InfoBarSeverity severity)
    {
        ExtractNotice.Title = title;
        ExtractNotice.Message = message;
        ExtractNotice.Severity = severity;
        ExtractNotice.IsOpen = true;
    }
}
