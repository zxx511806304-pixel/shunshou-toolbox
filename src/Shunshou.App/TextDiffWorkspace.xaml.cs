using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shunshou.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace Shunshou.App;

/// <summary>Compares two texts line by line on this computer and never uploads either side.</summary>
public sealed partial class TextDiffWorkspace : UserControl
{
    private const int MaxDisplayedRows = 1_000;
    private DiffResult? _result;

    public TextDiffWorkspace() => InitializeComponent();

    public nint HostWindowHandle { get; set; }

    internal string Summary => SummaryText.Text;
    internal int DisplayedRows => DiffList.Items.Count;
    internal bool CanExport => SaveButton.IsEnabled;
    internal void SetTexts(string left, string right) { LeftText.Text = left; RightText.Text = right; }
    internal void SetShowSame(bool value)
    {
        ShowSame.IsChecked = value;
        if (_result is not null) Render();
    }

    private void Compare_Click(object sender, RoutedEventArgs e) => Compare();

    internal void Compare()
    {
        try
        {
            Notice.IsOpen = false;
            _result = TextDiffService.Compare(LeftText.Text, RightText.Text, IgnoreWhitespace.IsChecked == true, IgnoreCase.IsChecked == true);
            Render();
        }
        catch (Exception ex)
        {
            _result = null;
            DiffList.Items.Clear();
            ResultTitle.Text = "对比结果";
            SummaryText.Text = "没有完成对比。";
            Notice.Title = "无法对比";
            Notice.Message = ex.Message;
            Notice.IsOpen = true;
        }
    }

    private void Render()
    {
        var result = _result!;
        DiffList.Items.Clear();
        bool showSame = ShowSame.IsChecked == true;
        int displayed = 0;
        foreach (var line in result.Lines)
        {
            if (!showSame && line.Kind == DiffKind.Same) continue;
            if (displayed >= MaxDisplayedRows) break;
            DiffList.Items.Add(BuildRow(line));
            displayed++;
        }
        ResultTitle.Text = result.Identical ? "对比结果 · 两份完全一致" : "对比结果 · 有差异";
        string approximate = result.Approximate ? "（内容过多，已按整块替换处理）" : "";
        string truncated = displayed >= MaxDisplayedRows ? $"；界面仅显示前 {MaxDisplayedRows:N0} 行，复制与保存仍包含全部差异" : "";
        SummaryText.Text = result.Identical
            ? $"两份文本完全一致，共 {result.Same:N0} 行。"
            : $"新增 {result.Added:N0} 行，删除 {result.Removed:N0} 行，相同 {result.Same:N0} 行{approximate}{truncated}。";
        EmptyHint.Visibility = displayed == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = result.Identical
            ? "两份文本没有差异。打开“同时显示相同行”可以逐行核对。"
            : "勾选“同时显示相同行”可以查看完整上下文。";
        bool hasOutput = !result.Identical;
        CopyDifferencesButton.IsEnabled = CopyFullButton.IsEnabled = SaveButton.IsEnabled = hasOutput;
        CopyButton.IsEnabled = hasOutput;
    }

    private UIElement BuildRow(DiffLine line)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(4, 2, 4, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var marker = new TextBlock
        {
            Text = line.Kind switch { DiffKind.Added => "＋", DiffKind.Removed => "－", _ => "　" },
            Style = (Style)Resources["DiffSameStyle"],
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        var left = new TextBlock { Text = line.Left?.ToString("N0") ?? "", Style = (Style)Resources["DiffNumberStyle"] };
        var right = new TextBlock { Text = line.Right?.ToString("N0") ?? "", Style = (Style)Resources["DiffNumberStyle"] };
        var text = new TextBlock
        {
            Text = line.Text.Length == 0 ? " " : line.Text,
            Style = (Style)Resources[line.Kind switch
            {
                DiffKind.Added => "DiffAddedStyle",
                DiffKind.Removed => "DiffRemovedStyle",
                _ => "DiffSameStyle"
            }]
        };
        Grid.SetColumn(left, 1);
        Grid.SetColumn(right, 2);
        Grid.SetColumn(text, 3);
        grid.Children.Add(marker);
        grid.Children.Add(left);
        grid.Children.Add(right);
        grid.Children.Add(text);
        return grid;
    }

    private void ShowSame_Changed(object sender, RoutedEventArgs e)
    {
        if (_result is not null) Render();
    }

    private void Swap_Click(object sender, RoutedEventArgs e)
    {
        (LeftText.Text, RightText.Text) = (RightText.Text, LeftText.Text);
        if (_result is not null) Compare();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        LeftText.Text = RightText.Text = string.Empty;
        _result = null;
        DiffList.Items.Clear();
        EmptyHint.Visibility = Visibility.Visible;
        EmptyHint.Text = "还没有对比结果。两份文本完全一致时，这里会显示“没有差异”。";
        ResultTitle.Text = "对比结果";
        SummaryText.Text = "粘贴两份文本后点“开始对比”，只在本机比较，不上传内容。";
        CopyDifferencesButton.IsEnabled = CopyFullButton.IsEnabled = SaveButton.IsEnabled = CopyButton.IsEnabled = false;
        Notice.IsOpen = false;
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyDifferences();

    private void CopyDiffs_Click(object sender, RoutedEventArgs e) => CopyDifferences();

    private void CopyFull_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null) return;
        CopyText(BuildReport(includeSame: true));
    }

    private void CopyDifferences()
    {
        if (_result is null || _result.Identical) return;
        CopyText(BuildReport(includeSame: false));
    }

    private void CopyText(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            SummaryText.Text = $"已复制对比结果（{text.Length:N0} 字）。";
        }
        catch (Exception)
        {
            Notice.Title = "无法写入剪贴板";
            Notice.Message = "系统暂时拒绝了剪贴板写入，请稍后再试。";
            Notice.IsOpen = true;
        }
    }

    private string BuildReport(bool includeSame)
    {
        var result = _result!;
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"文本对比：新增 {result.Added} 行，删除 {result.Removed} 行，相同 {result.Same} 行");
        builder.AppendLine();
        foreach (var line in result.Lines)
        {
            if (!includeSame && line.Kind == DiffKind.Same) continue;
            string marker = line.Kind switch { DiffKind.Added => "+", DiffKind.Removed => "-", _ => " " };
            builder.Append(marker).Append(' ').AppendLine(line.Text);
        }
        return builder.ToString();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null || _result.Identical) return;
        try
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = "文本对比" };
            picker.FileTypeChoices.Add("文本文件", [".txt"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, HostWindowHandle);
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await Windows.Storage.FileIO.WriteTextAsync(file, BuildReport(includeSame: true), Windows.Storage.Streams.UnicodeEncoding.Utf8);
            SummaryText.Text = "已保存 · " + file.Name;
        }
        catch (Exception ex)
        {
            Notice.Title = "保存失败";
            Notice.Message = ex.Message;
            Notice.IsOpen = true;
        }
    }
}
