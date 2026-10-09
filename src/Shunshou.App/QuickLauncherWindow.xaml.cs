using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Shunshou.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;

namespace Shunshou.App;

/// <summary>One small always-on-top box reused for the whole session: tools, indexed files, arithmetic.</summary>
public sealed partial class QuickLauncherWindow : Window
{
    private readonly MainWindow _main;
    private readonly ObservableCollection<LauncherRow> _rows = [];
    private CancellationTokenSource? _fileSearch;
    private long _generation;
    private bool _allowClose;

    public QuickLauncherWindow(MainWindow main, ElementTheme theme, string shortcutLabel)
    {
        _main = main;
        InitializeComponent();
        Root.RequestedTheme = theme;
        Title = "顺手工具箱 · 快速启动";
        QueryBox.PlaceholderText = $"搜索工具、文件，或输入算式（{shortcutLabel} 呼出）";
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        Results.ItemsSource = _rows;
        UpdateEmpty();
        Activated += (_, args) => { if (args.WindowActivationState == WindowActivationState.Deactivated) HideWindow(); };
        AppWindow.Closing += (_, args) => { if (!_allowClose) { args.Cancel = true; HideWindow(); } };
    }

    public bool IsShown => AppWindow.IsVisible;

    public void ShowAndFocus()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(hwnd) / 96d;
        int width = (int)(560 * scale), height = (int)(420 * scale);
        GetCursorPos(out var cursor);
        var work = (DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest) ?? DisplayArea.Primary).WorkArea;
        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(work.X + (work.Width - width) / 2, work.Y + Math.Max(24, work.Height / 5)));
        _generation++;
        _fileSearch?.Cancel();
        QueryBox.Text = "";
        _rows.Clear();
        UpdateEmpty();
        AppWindow.Show();
        Activate();
        NativeWindowHelpers.SetForeground(hwnd);
        QueryBox.Focus(FocusState.Programmatic);
    }

    public void HideWindow()
    {
        _generation++;
        _fileSearch?.Cancel();
        if (AppWindow.IsVisible) AppWindow.Hide();
    }

    /// <summary>Real close, used when the main window shuts down.</summary>
    internal void ForceClose() { _allowClose = true; Close(); }

    private void UpdateEmpty()
    {
        EmptyHint.Text = QueryBox.Text.Trim().Length == 0
            ? "输入以搜索工具、文件，或输入算式（Esc 关闭）"
            : "没有匹配的工具；文件结果会在索引就绪后自动出现";
        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Query_TextChanged(object sender, TextChangedEventArgs args)
    {
        long generation = ++_generation;
        string query = QueryBox.Text.Trim();
        _rows.Clear();
        if (query.Length == 0)
        {
            _fileSearch?.Cancel();
            UpdateEmpty();
            return;
        }
        if (QuickCalculateService.TryEvaluate(query, out double value))
            _rows.Add(new LauncherRow("", "= " + QuickCalculateService.Format(value), "回车复制结果", "计算", new LauncherCalculation(value)));
        foreach (var tool in _main.LauncherToolMatches(query))
            _rows.Add(new LauncherRow("", tool.Operation, tool.Group, "工具", tool));
        UpdateEmpty();
        _fileSearch?.Cancel();
        var search = new CancellationTokenSource();
        _fileSearch = search;
        _ = AppendFilesAsync(generation, query, search);
    }

    /// <summary>Indexed file matches arrive after a short debounce so typing never spawns a query per keystroke.</summary>
    private async Task AppendFilesAsync(long generation, string query, CancellationTokenSource search)
    {
        try
        {
            await Task.Delay(260, search.Token);
            var files = await _main.LauncherFileSearchAsync(query, search.Token);
            if (generation != _generation || search.IsCancellationRequested) return;
            foreach (var file in files)
                _rows.Add(new LauncherRow(file.IsDirectory ? "" : "", file.Name, file.FullPath, file.IsDirectory ? "文件夹" : "文件", file));
            UpdateEmpty();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.LogException(ex, "quick-launcher-files"); }
    }

    private void ActivateRow(LauncherRow? row)
    {
        row ??= _rows.FirstOrDefault();
        if (row is null) return;
        HideWindow();
        switch (row.Payload)
        {
            case MainWindow.ToolSuggestion tool:
                _main.OpenToolFromLauncher(tool);
                break;
            case FileSearchResult file:
                _main.OpenPathFromLauncher(file.FullPath);
                break;
            case LauncherCalculation calculation:
                var package = new DataPackage();
                package.SetText(QuickCalculateService.Format(calculation.Value));
                Clipboard.SetContent(package);
                break;
        }
    }

    private void Query_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape) { args.Handled = true; HideWindow(); }
        else if (args.Key == VirtualKey.Enter) { args.Handled = true; ActivateRow(Results.SelectedItem as LauncherRow); }
        else if (args.Key == VirtualKey.Down && _rows.Count > 0)
        {
            args.Handled = true;
            Results.SelectedIndex = 0;
            Results.Focus(FocusState.Programmatic);
        }
    }

    private void Results_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape) { args.Handled = true; HideWindow(); }
        else if (args.Key == VirtualKey.Enter) { args.Handled = true; ActivateRow(Results.SelectedItem as LauncherRow); }
        else if (args.Key == VirtualKey.Up && Results.SelectedIndex <= 0) { args.Handled = true; QueryBox.Focus(FocusState.Programmatic); }
    }

    private void Results_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is LauncherRow row) ActivateRow(row);
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out RecordingNative.Point point);
}

internal sealed record LauncherRow(string Glyph, string Title, string Detail, string Kind, object Payload);
internal sealed record LauncherCalculation(double Value);
