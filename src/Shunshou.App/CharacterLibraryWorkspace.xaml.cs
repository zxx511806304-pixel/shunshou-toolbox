using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Shunshou.Core;
using Windows.ApplicationModel.DataTransfer;

namespace Shunshou.App;

/// <summary>
/// The curated special-character library from the project, kept in the app: ordered by daily
/// office use, one click copies the symbol, and the search box filters by character, name or code.
/// </summary>
public sealed partial class CharacterLibraryWorkspace : UserControl
{
    private const double TileWidth = 52;
    private const double TileHeight = 48;
    private const double TileSpacing = 6;
    private readonly Dictionary<string, Button> _tiles = new(StringComparer.Ordinal);
    private readonly List<(Grid Host, List<Button> Tiles)> _grids = [];
    private CharacterLibrary? _library;
    private IReadOnlyList<CharacterGroup> _visibleGroups = [];
    private bool _rendering;
    private int _groupNumber;
    private double _lastLayoutWidth;

    public CharacterLibraryWorkspace() => InitializeComponent();

    public string? LastCopiedSymbol { get; private set; }
    public string? LastCopiedDescription { get; private set; }

    public void EnsureLoaded()
    {
        if (_library is not null) return;
        try
        {
            _library = CharacterLibrary.Load();
            Notice.IsOpen = false;
            RenderGroups(_library.Filter(SearchBox.Text));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                      System.Text.Json.JsonException or ArgumentException or NotSupportedException)
        {
            _library = null;
            GroupHost.Children.Clear();
            CountText.Text = "";
            Notice.Title = "字符库不可用";
            Notice.Message = "没有读到随包提供的字符数据，其他工具不受影响。" + ex.Message;
            Notice.IsOpen = true;
        }
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (_rendering || _library is null) return;
        RenderGroups(_library.Filter(SearchBox.Text));
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        if (SearchBox.Text.Length == 0) { if (_library is not null) RenderGroups(_library.Filter(null)); return; }
        SearchBox.Text = string.Empty;
    }

    private void GroupScroll_SizeChanged(object sender, SizeChangedEventArgs e) => RelayOut(e.NewSize.Width);

    private void Character_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CharacterEntry entry) return;
        if (!TryCopy(entry))
        {
            Notice.Title = "无法写入剪贴板";
            Notice.Message = "系统暂时拒绝了剪贴板写入，请稍后再点一次，或直接选中“当前字符”手动复制。";
            Notice.IsOpen = true;
            return;
        }
        Notice.IsOpen = false;
        CurrentCharBox.Text = entry.Value;
        LastCopiedSymbol = entry.Value;
        LastCopiedDescription = $"{entry.Name} {entry.Code}";
        StatusText.Text = $"已复制：{entry.Name} {entry.Value} {entry.Code}";
    }

    private static bool TryCopy(CharacterEntry entry)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(entry.Value);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            return true;
        }
        catch (Exception) { return false; }
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        var symbols = _visibleGroups.SelectMany(group => group.Entries)
            .Select(entry => entry.Value).Where(value => value.Length > 0).Distinct().ToArray();
        if (symbols.Length == 0)
        {
            Notice.Title = "没有可复制的字符";
            Notice.Message = "当前筛选没有匹配的字符，请先调整查找关键词。";
            Notice.IsOpen = true;
            return;
        }
        // Keep the clipboard payload reasonable: join up to 2000 symbols without separators.
        var text = string.Concat(symbols.Take(2000));
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception)
        {
            Notice.Title = "无法写入剪贴板";
            Notice.Message = "系统暂时拒绝了剪贴板写入，请稍后再试一次。";
            Notice.IsOpen = true;
            return;
        }
        Notice.IsOpen = false;
        StatusText.Text = $"已复制当前显示的 {symbols.Length} 个字符（连续无分隔）。";
    }

    private void RenderGroups(IReadOnlyList<CharacterGroup> groups)
    {
        _rendering = true;
        _visibleGroups = groups;
        try
        {
            GroupHost.Children.Clear();
            _tiles.Clear();
            _grids.Clear();
            _groupNumber = 0;
            foreach (var group in groups) GroupHost.Children.Add(BuildGroup(group));
            EmptyHint.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            int visible = groups.Sum(group => group.Entries.Count);
            CountText.Text = groups.Count == 0 || _library is null
                ? ""
                : $"显示 {groups.Count} 组 · {visible} 个字符；字符库共 {_library.GroupCount} 组 · {_library.CharacterCount} 个字符。";
            RelayOut(GroupScroll.ActualWidth > 0 ? GroupScroll.ActualWidth : _lastLayoutWidth, force: true);
        }
        finally { _rendering = false; }
    }

    private FrameworkElement BuildGroup(CharacterGroup group)
    {
        var panel = new StackPanel { Spacing = 8 };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { Text = $"{++_groupNumber:00} · {group.Title}", Style = (Style)Resources["CharacterGroupTitleStyle"] };
        var level = new TextBlock { Text = group.Level, Style = (Style)Resources["CharacterGroupLevelStyle"], Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(level, 1);
        header.Children.Add(title);
        header.Children.Add(level);
        panel.Children.Add(header);
        var grid = new Grid { ColumnSpacing = TileSpacing, RowSpacing = TileSpacing };
        var tiles = new List<Button>();
        foreach (var entry in group.Entries)
        {
            var tile = CreateTile(entry);
            // The first group that offers a symbol keeps the shortcut, matching the common-to-rare order.
            _tiles.TryAdd(entry.Value, tile);
            tiles.Add(tile);
            grid.Children.Add(tile);
        }
        _grids.Add((grid, tiles));
        panel.Children.Add(grid);
        return panel;
    }

    private Button CreateTile(CharacterEntry entry)
    {
        var chrome = new Border { Style = (Style)Resources["CharacterTileChromeStyle"] };
        chrome.Child = new TextBlock { Text = entry.Display, Style = (Style)Resources["CharacterTileGlyphStyle"] };
        var button = new Button
        {
            Tag = entry,
            Width = TileWidth,
            Height = TileHeight,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Content = chrome
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, entry.AutomationName);
        ToolTipService.SetToolTip(button, entry.Tooltip);
        button.Click += Character_Click;
        return button;
    }

    /// <summary>Re-flows the tiles into as many columns as the current width allows.</summary>
    private void RelayOut(double availableWidth, bool force = false)
    {
        if (availableWidth <= 0) return;
        if (!force && Math.Abs(availableWidth - _lastLayoutWidth) < 1) return;
        _lastLayoutWidth = availableWidth;
        double usable = Math.Max(TileWidth, availableWidth - 4);
        int columns = Math.Max(1, (int)((usable + TileSpacing) / (TileWidth + TileSpacing)));
        foreach (var (grid, tiles) in _grids)
        {
            if (grid.ColumnDefinitions.Count != columns)
            {
                grid.ColumnDefinitions.Clear();
                for (int column = 0; column < columns; column++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }
            for (int index = 0; index < tiles.Count; index++)
            {
                Grid.SetColumn(tiles[index], index % columns);
                Grid.SetRow(tiles[index], index / columns);
            }
            int rows = (tiles.Count + columns - 1) / columns;
            while (grid.RowDefinitions.Count < rows) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            while (grid.RowDefinitions.Count > rows) grid.RowDefinitions.RemoveAt(grid.RowDefinitions.Count - 1);
        }
    }

    /// <summary>Returns the on-screen tile for a symbol so verification can drive the real click path.</summary>
    internal Button? GetTile(string symbol) => _tiles.TryGetValue(symbol, out var button) ? button : null;

    /// <summary>Clicks a real tile through the automation peer, exactly like a keyboard or mouse user would.</summary>
    internal bool InvokeTile(string symbol)
    {
        if (GetTile(symbol) is not { } tile) return false;
        var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(tile);
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer).Invoke();
        return true;
    }

    internal (int Groups, int Characters) LibrarySize => _library is null ? (0, 0) : (_library.GroupCount, _library.CharacterCount);
    internal int VisibleGroupCount => _grids.Count;
    internal int VisibleCharacterCount => _grids.Sum(grid => grid.Tiles.Count);
    internal int FirstGroupColumns => _grids.Count > 0 ? _grids[0].Host.ColumnDefinitions.Count : 0;
    internal bool IsTileVisible(string symbol) => _tiles.ContainsKey(symbol);
    internal string CountSummary => CountText.Text;
    internal string StatusMessage => StatusText.Text;
    internal string CurrentCharacter => CurrentCharBox.Text;
    /// <summary>Used by verification to drive the same filter path as typing in the search box.</summary>
    internal void ApplySearch(string? query)
    {
        SearchBox.Text = query ?? string.Empty;
        if (_library is not null) RenderGroups(_library.Filter(query));
    }
    internal string GroupHeadings => string.Join("；", GroupHost.Children.OfType<StackPanel>()
        .Select(panel => panel.Children.OfType<Grid>().FirstOrDefault()?.Children.OfType<TextBlock>().FirstOrDefault()?.Text)
        .Where(text => !string.IsNullOrEmpty(text)));
}
