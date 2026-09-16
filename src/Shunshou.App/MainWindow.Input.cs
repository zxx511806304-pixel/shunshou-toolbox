using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Shunshou.Core;
using Windows.ApplicationModel.DataTransfer;

namespace Shunshou.App;

public sealed partial class MainWindow
{
    private Border? _fileDropOverlay;
    private TextBlock? _fileDropCaption;
    private bool _receivingDrop;

    private InputTool CurrentInputTool => Operation switch
    {
        "按上传上限压缩" => InputTool.TargetZip,
        "无损 ZIP 打包" => InputTool.CreateZip,
        "ZIP 解压" => InputTool.ExtractZip,
        "合并 PDF" => InputTool.MergePdf,
        "图片格式转换" => InputTool.ImageConvert,
        "图片提取文字" => InputTool.Ocr,
        "按名称搜索" => InputTool.Search,
        "批量重命名" => InputTool.Rename,
        "撤销重命名" => InputTool.UndoRename,
        _ when _category == "pdf" => InputTool.Pdf,
        _ => InputTool.Media
    };

    private void InitializeFileDrop()
    {
        RootLayout.AllowDrop = true;
        // Handled events also reach us when the pointer is over a nested TextBox/ListView.
        RootLayout.AddHandler(UIElement.DragOverEvent, new DragEventHandler(Window_DragOver), true);
        RootLayout.AddHandler(UIElement.DragLeaveEvent, new DragEventHandler(Window_DragLeave), true);
        RootLayout.AddHandler(UIElement.DropEvent, new DragEventHandler(Window_Drop), true);
        _fileDropCaption = new TextBlock
        {
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _fileDropOverlay = new Border
        {
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(10),
            Padding = new Thickness(26),
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(24, 16, 24, 16),
                Child = _fileDropCaption
            }
        };
        Grid.SetRow(_fileDropOverlay, 1);
        Canvas.SetZIndex(_fileDropOverlay, 1000);
        RootLayout.Children.Add(_fileDropOverlay);
    }

    private bool AcceptInputPaths(IEnumerable<string> paths)
    {
        if (_category is "software" or "shop" || OcrEditor.IsBusy) return false;
        if (IsUrlOperation) return false;
        if (Operation == "视频水印处理")
        {
            if (_busy) return false;
            var files = paths.ToArray();
            if (files.Length != 1 || !File.Exists(files[0])) { ShowError("一次添加一个本地视频文件。"); return false; }
            _ = VideoTools.SetInputAsync(files[0]);
            return true;
        }
        if (Operation == "误删恢复")
        {
            if (_busy) return false;
            var input = paths.ToArray();
            if (input.Length != 1) { ShowError("一次选择一个磁盘镜像或备份文件夹。"); return false; }
            try { Recovery.SelectSource(input[0]); return true; }
            catch (Exception ex) { ShowError(ex.Message); return false; }
        }
        var result = InputSelectionPolicy.Select(CurrentInputTool, _inputs, paths, _busy);
        if (result.Applied)
        {
            _inputs.Clear();
            _inputs.AddRange(result.Paths);
            if (result.SearchDirectory != null)
                SelectSearchFolder(result.SearchDirectory, result.SearchFileName);
            UpdateSelection();
            if (result.Message == null) StatusInfo.IsOpen = false;
        }
        if (result.Message != null)
        {
            // Input feedback stays in place, including when the independent search workspace is visible.
            StatusInfo.Title = result.Applied ? "文件已添加" : "请检查添加的文件";
            StatusInfo.Message = result.Message;
            StatusInfo.Severity = result.Rejected.Count > 0 || !result.Applied ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
            StatusInfo.IsOpen = true;
        }
        return result.Applied;
    }

    private void ShowFileDropOverlay(bool show)
    {
        if (_fileDropOverlay == null || _fileDropCaption == null) return;
        if (show)
        {
            _fileDropCaption.Text = Operation == "按名称搜索" ? "松开以选择搜索范围" : $"松开以添加到「{Operation}」";
            var accent = (Brush)Application.Current.Resources["BrandBrush"];
            _fileDropOverlay.BorderBrush = accent;
            _fileDropCaption.Foreground = accent;
            ((Border)_fileDropOverlay.Child).Background = (Brush)Application.Current.Resources["SurfaceBrush"];
        }
        _fileDropOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Window_DragOver(object sender, DragEventArgs args)
    {
        bool canReceive = _category is not "software" and not "shop" && !IsUrlOperation && !OcrEditor.IsBusy && !_busy && !_receivingDrop && args.DataView.Contains(StandardDataFormats.StorageItems);
        args.AcceptedOperation = canReceive ? DataPackageOperation.Copy : DataPackageOperation.None;
        args.Handled = true;
        if (canReceive)
        {
            args.DragUIOverride.Caption = Operation == "按名称搜索" ? "选择搜索范围" : "添加文件";
            args.DragUIOverride.IsCaptionVisible = true;
        }
        ShowFileDropOverlay(canReceive);
    }

    private void Window_DragLeave(object sender, DragEventArgs args)
    {
        // Clear on cancellation too; a subsequent DragOver immediately restores it when crossing children.
        ShowFileDropOverlay(false);
    }

    private async void Window_Drop(object sender, DragEventArgs args)
    {
        args.Handled = true;
        args.AcceptedOperation = DataPackageOperation.None;
        ShowFileDropOverlay(false);
        if (_busy || _receivingDrop || !args.DataView.Contains(StandardDataFormats.StorageItems)) return;
        string operation = Operation;
        var deferral = args.GetDeferral();
        _receivingDrop = true;
        try
        {
            var items = await args.DataView.GetStorageItemsAsync();
            if (_busy || Operation != operation) return;
            if (AcceptInputPaths(items.Select(item => item.Path))) args.AcceptedOperation = DataPackageOperation.Copy;
        }
        catch (Exception ex)
        {
            StatusInfo.Title = "未能添加文件";
            StatusInfo.Message = ex.Message;
            StatusInfo.Severity = InfoBarSeverity.Error;
            StatusInfo.IsOpen = true;
        }
        finally
        {
            _receivingDrop = false;
            ShowFileDropOverlay(false);
            deferral.Complete();
        }
    }
}
