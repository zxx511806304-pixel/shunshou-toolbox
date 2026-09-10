using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shunshou.Core;

namespace Shunshou.App;

public sealed partial class UninstallerWorkspace : UserControl, IDisposable
{
    private UninstallService _service = new();
    private readonly ObservableCollection<InstalledAppRow> _visibleApps = [];
    private readonly ObservableCollection<LeftoverRow> _leftovers = [];
    private IReadOnlyList<InstalledApplication> _allApps = [];
    private InstalledApplication? _snapshot;
    private LeftoverScan? _scan;
    private CancellationTokenSource? _operation;
    private bool _busy;
    private bool _loaded;
    private bool _updatingList;
    private bool _disposed;
    private bool _canUninstall;
    private bool _uninstallFinished;
    private bool _waitingForUninstaller;
    private bool? _administratorOverride;
    private Task? _loadTask;
    private string? _pendingSelectId;

    public nint HostWindowHandle { get; set; }
    public event EventHandler<bool>? BusyChanged;
    public event EventHandler<string>? RequestElevation;
    internal Func<string, string, string, Task<bool>>? ConfirmOverride { get; set; }
    private bool IsAdministrator => _administratorOverride ?? UninstallService.IsAdministrator;

    public UninstallerWorkspace()
    {
        InitializeComponent();
        Applications.ItemsSource = _visibleApps;
        Candidates.ItemsSource = _leftovers;
    }

    public Task LoadAsync(string? selectId = null)
    {
        if (_disposed) return Task.CompletedTask;
        if (selectId != null) _pendingSelectId = selectId;
        if (_loadTask is { IsCompleted: false }) return _loadTask;
        if (_busy) return Task.CompletedTask;
        if (_loaded) { ApplyPendingSelection(); return Task.CompletedTask; }
        _loadTask = WithBusyAsync("正在读取已安装的软件…", async ct =>
        {
            _allApps = await _service.ListAsync(ct);
            _loaded = true;
            if (_pendingSelectId == null) ApplyFilter(_snapshot?.Id);
            else ApplyPendingSelection();
            ProgressText.Text = $"已读取 {_allApps.Count:N0} 个软件";
        });
        return _loadTask;
    }

    private void ApplyPendingSelection()
    {
        if (!_loaded || _pendingSelectId is not { } id) return;
        _pendingSelectId = null;
        SearchBox.Text = "";
        ApplyFilter(id);
        if (_allApps.All(app => app.Id != id))
            ShowStatus("列表已更新", "所选软件已不在已安装列表中。", InfoBarSeverity.Informational);
    }

    public void CancelOperation() => _operation?.Cancel();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs args)
    {
        if (Applications != null && !_busy) ApplyFilter(_snapshot?.Id);
    }

    private void ApplyFilter(string? selectId)
    {
        string query = SearchBox.Text.Trim();
        _updatingList = true;
        try
        {
            _visibleApps.Clear();
            foreach (var app in _allApps)
                if (query.Length == 0 || app.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) || app.Publisher.Contains(query, StringComparison.OrdinalIgnoreCase))
                    _visibleApps.Add(new InstalledAppRow(app));
            Applications.SelectedItem = _visibleApps.FirstOrDefault(row => row.Application.Id == selectId);
            ApplicationCount.Text = $"已安装软件 · {_visibleApps.Count:N0}";
        }
        finally { _updatingList = false; }
        if (Applications.SelectedItem is InstalledAppRow selected) SelectApplication(selected.Application);
        // Preserve the application snapshot after its uninstaller removes the registry entry.
    }

    private void Applications_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updatingList || _busy) return;
        if (Applications.SelectedItem is InstalledAppRow selected) SelectApplication(selected.Application);
        else
        {
            _snapshot = null;
            _scan = null;
            _canUninstall = _uninstallFinished = false;
            _leftovers.Clear();
            AppName.Text = "选择要管理的软件";
            AppDetail.Text = InstallLocation.Text = EntryStatus.Text = "";
            ScanCount.Text = "检查后在这里选择需要清理的条目";
            IncludeRegistration.IsChecked = false;
            UpdateEnabledState();
        }
    }

    private void SelectApplication(InstalledApplication app)
    {
        if (_snapshot == app) return;
        _snapshot = app;
        _scan = null;
        _uninstallFinished = false;
        _leftovers.Clear();
        IncludeRegistration.IsChecked = false;
        AppName.Text = app.DisplayName;
        AppDetail.Text = string.Join(" · ", new[] { app.Publisher, app.Version, InstalledAppRow.Size(app.EstimatedSizeBytes), app.Kind == InstalledApplicationKind.Store ? "Windows 应用" : "桌面软件" }.Where(value => value.Length > 0));
        InstallLocation.Text = app.InstallLocation ?? "";
        ToolTipService.SetToolTip(InstallLocation, app.InstallLocation ?? "");
        ToolTipService.SetToolTip(AppName, app.DisplayName);
        _canUninstall = app.Kind == InstalledApplicationKind.Store;
        string entry = "通过 Windows 卸载当前用户的应用。";
        if (app.Kind == InstalledApplicationKind.Desktop)
        {
            try
            {
                var command = UninstallService.ParseUninstallCommand(app.UninstallCommand ?? "");
                _canUninstall = File.Exists(command.ExecutablePath);
                entry = _canUninstall ? "启动软件自带的卸载程序。" : "卸载程序已不存在，可检查相关文件和设置。";
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or NotSupportedException)
            {
                _canUninstall = false;
                entry = "卸载入口不可用，可检查相关文件和设置。";
            }
        }
        EntryStatus.Text = entry;
        ScanCount.Text = "检查后在这里选择需要清理的条目";
        Status.IsOpen = false;
        UpdateEnabledState();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs args)
    {
        if (_busy) return;
        _loaded = false;
        await LoadAsync();
    }

    private async void Admin_Click(object sender, RoutedEventArgs args)
    {
        if (_snapshot is not { } app || _busy) return;
        await WithBusyAsync("准备管理员窗口…", _ => AskForElevationAsync(app));
    }

    private async Task AskForElevationAsync(InstalledApplication app)
    {
        if (RequestElevation == null) { ShowStatus("需要管理员权限", "请以管理员身份打开顺手工具箱，再选择这个软件。", InfoBarSeverity.Warning); return; }
        bool approved = await ConfirmAsync("以管理员身份打开", $"将以管理员身份打开顺手工具箱，并选中“{app.DisplayName}”。\n\n新窗口中由你继续选择操作。", "管理员打开");
        if (approved) RequestElevation.Invoke(this, app.Id);
        else ProgressText.Text = "已取消管理员打开。";
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs args)
        => await UninstallSelectedAsync();

    private async Task UninstallSelectedAsync()
    {
        if (_busy || !_canUninstall || _snapshot is not { } app) return;
        if (app.RequiresElevation && !IsAdministrator) { await WithBusyAsync("准备管理员窗口…", _ => AskForElevationAsync(app)); return; }
        await WithBusyAsync("准备卸载…", async ct =>
        {
            if (!await ConfirmAsync($"卸载 {app.DisplayName}", app.Kind == InstalledApplicationKind.Store
                    ? "将通过 Windows 卸载这个应用。应用中的本地数据可能一并删除，请先保存需要的内容。"
                    : "将启动软件自带的卸载程序，请按卸载窗口中的步骤操作。完成后可继续检查残留文件与设置。", "开始卸载")) { ProgressText.Text = "已取消，软件保持不变。"; return; }
            _waitingForUninstaller = true;
            CancelButton.Content = "停止等待";
            ProgressText.Text = "正在等待软件卸载结束…";
            var result = await _service.UninstallAsync(app, ct);
            _waitingForUninstaller = false;
            _uninstallFinished = !result.StillInstalled;
            _canUninstall = result.StillInstalled;
            _scan = null;
            _leftovers.Clear();
            EntryStatus.Text = result.Message;
            _allApps = await _service.ListAsync(ct);
            ApplyFilter(app.Id);
            ShowStatus(result.StillInstalled ? "请检查卸载状态" : "卸载程序已结束", result.Message,
                result.StillInstalled ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            ProgressText.Text = result.StillInstalled ? "确认卸载窗口后可刷新列表。" : "可以继续扫描残留。";
        });
    }

    private async void Scan_Click(object sender, RoutedEventArgs args)
    {
        if (_busy || _snapshot is not { } app) return;
        await ScanAsync(app);
    }

    private Task ScanAsync(InstalledApplication app) => WithBusyAsync("正在检查关联文件与设置…", async ct =>
    {
        var scan = await _service.ScanLeftoversAsync(app, IncludeRegistration.IsChecked == true, ct);
        _scan = scan;
        _leftovers.Clear();
        foreach (var item in scan.Items)
        {
            var row = new LeftoverRow(item);
            row.PropertyChanged += Leftover_Changed;
            _leftovers.Add(row);
        }
        ScanCount.Text = scan.Items.Count == 0 ? "没有发现可清理的关联条目" : $"找到 {scan.Items.Count:N0} 项 · 请勾选要清理的条目";
        ProgressText.Text = $"检查完成 · {scan.Items.Count:N0} 项候选，尚未选择";
        if (scan.SkippedReasons.Count > 0)
            ShowStatus("检查完成", string.Join("\n", scan.SkippedReasons.Take(3)) + (scan.SkippedReasons.Count > 3 ? $"\n另外跳过 {scan.SkippedReasons.Count - 3} 项。" : ""), InfoBarSeverity.Informational);
        else if (!_uninstallFinished && scan.Items.Count > 0)
            ShowStatus("这些文件可能仍在使用", "这是与所选软件关联的文件和设置。请确认已完成卸载，或明确知道哪些内容需要清理。", InfoBarSeverity.Informational);
    });

    private void Leftover_Changed(object? sender, PropertyChangedEventArgs args) => UpdateEnabledState();

    private async void Cleanup_Click(object sender, RoutedEventArgs args)
        => await CleanupSelectedAsync();

    private async Task CleanupSelectedAsync()
    {
        if (_busy || _scan is not { } scan) return;
        var selected = _leftovers.Where(row => row.IsSelected).ToArray();
        if (selected.Length == 0) return;
        if (selected.Any(row => row.Item.RequiresElevation) && !IsAdministrator)
        {
            ShowStatus("需要管理员权限", "尚未清理任何条目。请使用管理员窗口重新检查后选择清理。", InfoBarSeverity.Warning);
            return;
        }
        await WithBusyAsync("准备备份和清理…", async ct =>
        {
            string paths = string.Join("\n", selected.Select(row => row.Path));
            if (!await ConfirmAsync($"备份并清理 {selected.Length} 项", "将先完整备份并校验，再删除下面勾选的文件或设置。目录可能包含软件配置和个人数据。\n\n" + paths, "备份并清理")) { ProgressText.Text = "已取消，未清理任何条目。"; return; }
            var result = await _service.CleanupSelectedAsync(scan, selected.Select(row => row.Item.Id), ct);
            _scan = null;
            _leftovers.Clear();
            ScanCount.Text = "清理结束，重新检查可查看剩余条目";
            ShowStatus(result.Errors.Count == 0 ? "清理完成" : "部分条目未清理",
                $"已清理 {result.RemovedCount} 项。\n备份记录：{result.BackupManifestPath}" + (result.Errors.Count == 0 ? "" : "\n" + string.Join("\n", result.Errors.Take(3))),
                result.Errors.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
            ProgressText.Text = "备份可通过“恢复备份”找回。";
            _allApps = await _service.ListAsync(ct);
            ApplyFilter(_snapshot?.Id);
        });
    }

    private async void Restore_Click(object sender, RoutedEventArgs args)
    {
        if (_busy) return;
        await WithBusyAsync("正在读取备份…", async ct =>
        {
            var manifests = _service.ListBackupManifests();
            if (manifests.Count == 0) { ShowStatus("没有备份记录", "完成备份清理后，备份会出现在这里。", InfoBarSeverity.Informational); return; }
            var choices = manifests.Select(path => new BackupChoice(path)).ToArray();
            var list = new ListView { ItemsSource = choices, MaxHeight = 280, SelectionMode = ListViewSelectionMode.Single };
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock { Text = "选择顺手工具箱保存的一份备份。已有文件和设置不会被覆盖。", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(list);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "恢复备份", Content = content,
                PrimaryButtonText = "恢复所选备份", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
                IsPrimaryButtonEnabled = false
            };
            list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem != null;
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || list.SelectedItem is not BackupChoice choice) { ProgressText.Text = "已取消恢复备份。"; return; }
            ct.ThrowIfCancellationRequested();
            await RestoreManifestAsync(choice.Path, ct);
        });
    }

    private async Task RestoreManifestAsync(string manifest, CancellationToken ct)
    {
        ProgressText.Text = "正在恢复备份…";
        var result = await _service.RestoreBackupAsync(manifest, ct);
        ShowStatus(result.Errors.Count == 0 ? "恢复完成" : "部分条目未恢复", $"已恢复 {result.RestoredCount} 项。" +
            (result.Errors.Count == 0 ? "" : "\n" + string.Join("\n", result.Errors.Take(3))), result.Errors.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        ProgressText.Text = "备份恢复操作已结束。";
        _allApps = await _service.ListAsync(ct);
        ApplyFilter(_snapshot?.Id);
    }

    private async Task<bool> ConfirmAsync(string title, string content, string primary)
    {
        if (ConfirmOverride != null) return await ConfirmOverride(title, content, primary);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = title,
            Content = new ScrollViewer { MaxHeight = 340, Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } },
            PrimaryButtonText = primary, CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task WithBusyAsync(string message, Func<CancellationToken, Task> action)
    {
        if (_busy || _disposed) return;
        _operation = new CancellationTokenSource();
        SetBusy(true);
        Status.IsOpen = false;
        ProgressText.Text = message;
        try { await action(_operation.Token); }
        catch (OperationCanceledException)
        {
            ShowStatus("操作已停止", _waitingForUninstaller ? "已停止等待。软件的卸载程序可能仍在运行，请检查卸载窗口。" : "本次操作已停止，请检查已完成的结果或备份记录。", InfoBarSeverity.Informational);
            ProgressText.Text = "操作已停止。";
        }
        catch (Exception ex) { ShowStatus("本次操作未完成", ex.Message, InfoBarSeverity.Error); ProgressText.Text = "请检查提示后重试。"; }
        finally
        {
            _waitingForUninstaller = false;
            SetBusy(false);
            _operation.Dispose();
            _operation = null;
            ApplyPendingSelection();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Progress.IsIndeterminate = busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Content = "取消";
        UpdateEnabledState();
        BusyChanged?.Invoke(this, busy);
    }

    private void UpdateEnabledState()
    {
        SearchBox.IsEnabled = RefreshButton.IsEnabled = RestoreButton.IsEnabled = Applications.IsEnabled = Candidates.IsEnabled = !_busy;
        UninstallButton.IsEnabled = !_busy && _snapshot != null && _canUninstall;
        ScanButton.IsEnabled = !_busy && _snapshot?.Kind == InstalledApplicationKind.Desktop;
        ScanButton.Visibility = _snapshot?.Kind == InstalledApplicationKind.Store ? Visibility.Collapsed : Visibility.Visible;
        CandidateWorkspace.Visibility = CleanupFooter.Visibility = _snapshot?.Kind == InstalledApplicationKind.Store ? Visibility.Collapsed : Visibility.Visible;
        ScanButton.Content = _uninstallFinished ? "扫描残留" : "检查关联文件";
        IncludeRegistration.IsEnabled = !_busy && _snapshot?.Kind == InstalledApplicationKind.Desktop;
        IncludeRegistration.Visibility = _snapshot?.Kind == InstalledApplicationKind.Desktop ? Visibility.Visible : Visibility.Collapsed;
        bool needsAdministrator = _snapshot?.RequiresElevation == true || _leftovers.Any(row => row.Item.RequiresElevation);
        bool snapshotStillListed = _snapshot != null && _allApps.Any(app => app.Id == _snapshot.Id);
        AdminButton.Visibility = !IsAdministrator && needsAdministrator && snapshotStillListed && _snapshot?.Kind == InstalledApplicationKind.Desktop ? Visibility.Visible : Visibility.Collapsed;
        AdminButton.IsEnabled = !_busy && _snapshot != null;
        int selected = _leftovers.Count(row => row.IsSelected);
        long bytes = _leftovers.Where(row => row.IsSelected).Sum(row => row.Item.SizeBytes);
        SelectedCount.Text = selected == 0 ? "尚未选择条目" : $"已选 {selected} 项 · {(bytes == 0 ? "0 字节" : InstalledAppRow.Size(bytes))}";
        CleanupButton.IsEnabled = !_busy && _scan != null && selected > 0;
    }

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        Status.Title = title; Status.Message = message; Status.Severity = severity; Status.IsOpen = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs args) => CancelOperation();

    public void Dispose()
    {
        _disposed = true;
        _operation?.Cancel();
    }

    private sealed class BackupChoice
    {
        public string Path { get; }
        private readonly string _label;
        public BackupChoice(string path)
        {
            Path = path;
            string name = "备份";
            DateTime created = DateTime.MinValue;
            try
            {
                created = File.GetLastWriteTime(Path);
                if (new FileInfo(Path).Length < 2_000_000)
                {
                    using var envelope = JsonDocument.Parse(File.ReadAllText(Path));
                    using var payload = JsonDocument.Parse(Convert.FromBase64String(envelope.RootElement.GetProperty("Payload").GetString()!));
                    name = payload.RootElement.GetProperty("Application").GetProperty("DisplayName").GetString() ?? name;
                }
            }
            catch { }
            _label = $"{created:yyyy-MM-dd HH:mm} · {name}";
        }
        public override string ToString() => _label;
    }
}

public sealed class InstalledAppRow(InstalledApplication application)
{
    public InstalledApplication Application { get; } = application;
    public string Name => Application.DisplayName;
    public string Publisher => string.IsNullOrWhiteSpace(Application.Publisher) ? "未提供发布者" : Application.Publisher;
    public string Detail => string.Join(" · ", new[] { Application.Version, Size(Application.EstimatedSizeBytes) }.Where(value => value.Length > 0));
    public static string Size(long bytes) => bytes <= 0 ? "" : bytes < 1000 ? $"{bytes} 字节" : bytes < 1_000_000 ? $"{bytes / 1000d:0.#} KB" : bytes < 1_000_000_000 ? $"{bytes / 1_000_000d:0.##} MB" : $"{bytes / 1_000_000_000d:0.##} GB";
}

public sealed class LeftoverRow(LeftoverItem item) : INotifyPropertyChanged
{
    public LeftoverItem Item { get; } = item;
    public string Path => Item.Path;
    public string Reason => Item.Reason;
    public string SelectionLabel => "选择清理 " + Item.Path;
    public string Detail => (Item.Kind == LeftoverKind.Directory ? "目录" : "注册表设置") + " · " + (Item.SizeBytes == 0 ? "0 字节" : InstalledAppRow.Size(Item.SizeBytes)) + (Item.RequiresElevation ? " · 需要管理员权限" : "");
    private bool _selected;
    public bool IsSelected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
