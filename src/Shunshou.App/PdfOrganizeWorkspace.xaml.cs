using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shunshou.Core;
using Windows.Storage.Pickers;

namespace Shunshou.App;

/// <summary>
/// Rearranges, rotates and drops pages of one local PDF and writes a new file. The source document is
/// never modified, and "looks blank" is only a hint the user confirms.
/// </summary>
public sealed partial class PdfOrganizeWorkspace : UserControl
{
    private sealed record PageRow(PdfPageInfo Info)
    {
        public bool Removed { get; set; }
        public int Rotation { get; set; } = Info.Rotation;
    }

    private readonly List<PageRow> _pages = [];
    private string? _document;
    private bool _busy;
    public nint HostWindowHandle { get; set; }
    public event EventHandler<bool>? BusyChanged;
    /// <summary>Raised when the user wants to pick a PDF; the window owns the file picker.</summary>
    public event EventHandler? PickRequested;

    private void Pick_Click(object sender, RoutedEventArgs e) => PickRequested?.Invoke(this, EventArgs.Empty);

    public PdfOrganizeWorkspace()
    {
        InitializeComponent();
        OutputBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ShunshouToolbox", "Output");
    }

    internal string? DocumentPath => _document;
    internal int KeptPages => _pages.Count(page => !page.Removed);
    internal int TotalPages => _pages.Count;
    internal string StatusMessage => StatusText.Text;
    internal string? LastOutput { get; private set; }
    internal IReadOnlyList<ListViewItem> PageItems() => PageList.Items.OfType<ListViewItem>().ToList();
    internal void SetOutputDirectory(string directory) => OutputBox.Text = directory;
    internal void RemoveSelected() => Remove_Click(this, new RoutedEventArgs());
    internal void SelectPage(int index)
    {
        PageList.SelectedItems.Clear();
        if (index >= 0 && index < PageList.Items.Count) PageList.SelectedItems.Add(PageList.Items[index]);
    }
    internal Task GenerateAsync()
        => GenerateCoreAsync();

    internal async Task SetDocumentAsync(string? path)
    {
        _document = path;
        LastOutput = null;
        OpenButton.IsEnabled = false;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            _pages.Clear();
            DocumentTitle.Text = "先在左侧选择或拖入一份 PDF";
            DocumentDetail.Text = "整理页序、删除不要的页、旋转方向，然后生成新的 PDF；原文件保留。";
            Rebuild();
            return;
        }
        DocumentTitle.Text = Path.GetFileName(path);
        await RunAsync(async ct => await LoadPagesAsync(ct));
    }

    private async Task LoadPagesAsync(CancellationToken ct)
    {
        string path = _document ?? throw new InvalidOperationException("请先选择 PDF。");
        StatusText.Text = "正在读取页面…";
        var pages = await Task.Run(() => new PdfService().InspectPages(path, ct), ct);
        ct.ThrowIfCancellationRequested();
        _pages.Clear();
        foreach (var page in pages) _pages.Add(new PageRow(page));
        int blanks = _pages.Count(page => page.Info.LikelyBlank);
        DocumentDetail.Text = $"{pages.Count} 页" + (blanks > 0 ? $" · 其中 {blanks} 页没有可提取文字和图片（疑似空白）" : "") + " · 原文件保留";
        Rebuild();
        StatusText.Text = "已读取页面，可调整后生成新 PDF";
    }

    private void Rebuild()
    {
        PageList.Items.Clear();
        foreach (var row in _pages) PageList.Items.Add(BuildItem(row));
        EmptyHint.Visibility = _pages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool ready = _pages.Count > 0 && KeptPages > 0 && !_busy;
        GenerateButton.IsEnabled = ready;
        ReloadButton.IsEnabled = _document is not null && !_busy;
        bool hasSelection = PageList.SelectedItems.Count > 0 && !_busy;
        MoveUpButton.IsEnabled = MoveDownButton.IsEnabled = RemoveButton.IsEnabled = RotateButton.IsEnabled = hasSelection;
        RemoveBlankButton.IsEnabled = _pages.Any(page => page.Info.LikelyBlank && !page.Removed) && !_busy;
        RestoreButton.IsEnabled = _pages.Count > 0 && !_busy;
    }

    private ListViewItem BuildItem(PageRow row)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(6, 4, 6, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        string label = row.Removed ? $"第 {row.Info.Number} 页（已删除）" : $"第 {row.Info.Number} 页";
        var number = new TextBlock { Text = label, Style = (Style)Resources[row.Removed ? "PageRemovedStyle" : "PageNumberStyle"] };
        var rotation = new TextBlock { Text = row.Rotation == 0 ? "不旋转" : $"旋转 {row.Rotation}°", Style = (Style)Resources["PageNoteStyle"] };
        var note = new TextBlock
        {
            Text = row.Info.LikelyBlank ? "疑似空白页" : $"{row.Info.WidthPoints:F0} × {row.Info.HeightPoints:F0} pt",
            Style = (Style)Resources["PageNoteStyle"]
        };
        Grid.SetColumn(rotation, 1);
        Grid.SetColumn(note, 2);
        grid.Children.Add(number);
        grid.Children.Add(rotation);
        grid.Children.Add(note);
        var item = new ListViewItem { Content = grid, Tag = row };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, label + (row.Info.LikelyBlank ? "，疑似空白页" : ""));
        return item;
    }

    private List<PageRow> SelectedRows() => PageList.SelectedItems.OfType<ListViewItem>().Select(item => (PageRow)item.Tag!).ToList();

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int offset)
    {
        var selected = SelectedRows();
        if (selected.Count == 0) return;
        int first = _pages.IndexOf(selected[0]);
        int target = first + offset;
        if (first < 0 || target < 0 || target + selected.Count > _pages.Count) return;
        foreach (var row in selected) _pages.Remove(row);
        _pages.InsertRange(target, selected);
        Rebuild();
        Select(selected);
    }

    private void Select(IReadOnlyList<PageRow> rows)
    {
        PageList.SelectedItems.Clear();
        foreach (var item in PageList.Items.OfType<ListViewItem>())
            if (item.Tag is PageRow row && rows.Contains(row)) PageList.SelectedItems.Add(item);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedRows();
        if (selected.Count == 0) return;
        if (selected.Count >= KeptPages) { ShowNotice("不能删除全部页面，至少保留一页。", InfoBarSeverity.Warning); return; }
        foreach (var row in selected) row.Removed = true;
        Rebuild();
        StatusText.Text = $"已标记删除 {selected.Count} 页，仍保留 {KeptPages} 页";
    }

    private void RemoveBlank_Click(object sender, RoutedEventArgs e)
    {
        var blanks = _pages.Where(page => page.Info.LikelyBlank && !page.Removed).ToList();
        if (blanks.Count == 0) { ShowNotice("没有检测到疑似空白页。", InfoBarSeverity.Informational); return; }
        if (blanks.Count >= KeptPages) { ShowNotice("疑似空白页是全部页面，已取消操作，请手动确认。", InfoBarSeverity.Warning); return; }
        foreach (var row in blanks) row.Removed = true;
        Rebuild();
        ShowNotice($"已标记 {blanks.Count} 张疑似空白页，可在生成前恢复。", InfoBarSeverity.Informational);
    }

    private void Rotate_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedRows();
        if (selected.Count == 0) return;
        foreach (var row in selected) row.Rotation = (row.Rotation + 90) % 360;
        Rebuild();
        Select(selected);
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        await RunAsync(async ct => await LoadPagesAsync(ct));
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        await RunAsync(async ct => await LoadPagesAsync(ct));
    }

    private async void Generate_Click(object sender, RoutedEventArgs e) => await GenerateCoreAsync();

    private async Task GenerateCoreAsync()
    {
        string? path = _document;
        if (path is null) { ShowNotice("请先选择 PDF。", InfoBarSeverity.Warning); return; }
        var edits = _pages.Where(page => !page.Removed).Select(page => new PdfPageEdit(page.Info.Number, page.Rotation)).ToList();
        await RunAsync(async ct =>
        {
            string output = await new PdfService().RebuildAsync(path, OutputBox.Text, edits,
                new Progress<ToolProgress>(p => { Progress.Value = p.Percent; StatusText.Text = p.Message; }), ct);
            LastOutput = output;
            Progress.Value = 100;
            StatusText.Text = $"已生成 {edits.Count} 页 · {Path.GetFileName(output)}";
            OpenButton.IsEnabled = true;
            ShowNotice($"新 PDF 已保存到 {output}", InfoBarSeverity.Success);
        });
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_busy) return;
        _busy = true;
        BusyChanged?.Invoke(this, true);
        Rebuild();
        Notice.IsOpen = false;
        try { await action(CancellationToken.None); }
        catch (Exception ex)
        {
            StatusText.Text = "处理未完成";
            ShowNotice(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _busy = false;
            BusyChanged?.Invoke(this, false);
            Rebuild();
        }
    }

    private void ShowNotice(string message, InfoBarSeverity severity)
    {
        Notice.Title = "整理 PDF 页面";
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, HostWindowHandle);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) OutputBox.Text = folder.Path;
        }
        catch (Exception ex) { ShowNotice(ex.Message, InfoBarSeverity.Error); }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (LastOutput is null) return;
        try { Process.Start(new ProcessStartInfo(LastOutput) { UseShellExecute = true }); }
        catch (Exception ex) { ShowNotice(ex.Message, InfoBarSeverity.Error); }
    }
}
