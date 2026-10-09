using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Shunshou.Core;
using Windows.System;

namespace Shunshou.App;

public sealed partial class MainWindow
{
    private readonly InputSelectionWorkspace _inputWorkspace = new();
    private string? _activeInputPath;
    private bool _selectingCategory;
    private bool _syncingInputLists;
    private bool _allowClose;
    private bool _closePromptOpen;
    private bool _minimizedToTray;
    private bool _suppressNextMinimizeTray;
    private bool _changingTrayState;
    private WindowMinimizeHook? _minimizeHook;
    private SystemTray? _tray;
    private Task _inputSyncTask = Task.CompletedTask;
    private string? _inputDraftMessage;

    private void InitializeWorkspaces(IntPtr hwnd)
    {
        OcrEditor.HostWindowHandle = Uninstaller.HostWindowHandle = Recovery.HostWindowHandle = hwnd;
        VideoTools.HostWindowHandle = hwnd;
        VideoTools.BusyChanged += (_, busy) => SetBusy(busy);
        RecordingTools.HostWindowHandle = hwnd;
        RecordingTools.BusyChanged += (_, busy) => SetBusy(busy);
        SubtitleTools.HostWindowHandle = WebPdfTools.HostWindowHandle = hwnd;
        TextDiffTools.HostWindowHandle = QrTools.HostWindowHandle = PdfPageTools.HostWindowHandle = hwnd;
        MarkdownTools.HostWindowHandle = hwnd;
        MarkdownTools.BusyChanged += (_, busy) => SetBusy(busy);
        PdfPageTools.BusyChanged += (_, busy) => SetBusy(busy);
        PdfPageTools.PickRequested += async (_, _) => await PickInputsForCurrentToolAsync();
        PdfReaderTools.HostWindowHandle = hwnd;
        PdfReaderTools.PickRequested += async (_, _) => await PickInputsForCurrentToolAsync();
        PdfReaderTools.ToolRequested += (_, operation) =>
        {
            string? path = PdfReaderTools.DocumentPath;
            if (path is null || _busy) return;
            NavigateToTool("pdf", operation);
            if (Operation == operation) AcceptInputPaths([path]);
        };
        ScreenshotTools.HostWindowHandle = hwnd;
        ScreenshotTools.BusyChanged += (_, busy) => SetBusy(busy);
        ScreenshotTools.CaptureRequested += (_, request) => request.PerformAsync = CaptureScreenshotRegionAsync;
        SubtitleTools.BusyChanged += (_, busy) => SetBusy(busy);
        WebPdfTools.BusyChanged += (_, busy) => SetBusy(busy);
        InputList.MaximumListHeight = 140;
        OcrInputList.MaximumListHeight = 100;
        foreach (var list in new[] { InputList, OcrInputList })
        {
            list.SelectionChanged += InputList_SelectionChanged;
            list.RemoveRequested += InputList_RemoveRequested;
            list.MoveRequested += InputList_MoveRequested;
        }
        OcrEditor.InputChanged += (_, path) =>
        {
            // A clear is completed by the host after SetInputAsync succeeds.
            if (path == null) return;
            if (!_inputs.Contains(path, StringComparer.OrdinalIgnoreCase)) _inputs.Add(path);
            _activeInputPath = path;
            UpdateSelection();
        };
        OcrEditor.BusyChanged += (_, busy) =>
        {
            if (!busy && _cancellation != null) return;
            SetBusy(busy);
            CancelButton.IsEnabled = busy && _cancellation != null;
        };
        Uninstaller.BusyChanged += (_, busy) => SetBusy(busy);
        Uninstaller.RequestElevation += (_, appId) => OpenAdministratorWindow(appId);
        Recovery.BusyChanged += (_, busy) => SetBusy(busy);
        Recovery.RequestElevation += (_, _) => OpenRecoveryAdministratorWindow();
        DiskCleanupTools.HostWindowHandle = LargeFilesTools.HostWindowHandle = DuplicateTools.HostWindowHandle = hwnd;
        StartupTools.HostWindowHandle = HijackTools.HostWindowHandle = NetworkTools.HostWindowHandle = hwnd;
        OfficeRescueTools.HostWindowHandle = ClipboardTools.HostWindowHandle = PasswordTools.HostWindowHandle = hwnd;
        DiskCleanupTools.BusyChanged += (_, busy) => SetBusy(busy);
        LargeFilesTools.BusyChanged += (_, busy) => SetBusy(busy);
        DuplicateTools.BusyChanged += (_, busy) => SetBusy(busy);
        StartupTools.BusyChanged += (_, busy) => SetBusy(busy);
        HijackTools.BusyChanged += (_, busy) => SetBusy(busy);
        NetworkTools.BusyChanged += (_, busy) => SetBusy(busy);
        OfficeRescueTools.BusyChanged += (_, busy) => SetBusy(busy);
        var paste = new KeyboardAccelerator { Key = VirtualKey.V, Modifiers = VirtualKeyModifiers.Control };
        paste.Invoked += async (_, args) =>
        {
            if (Operation != "图片提取文字" || _busy || !OcrEditor.CanHandlePasteShortcut()) return;
            args.Handled = true;
            try { await OcrEditor.PasteImageAsync(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { ShowError(ex.Message); }
        };
        RootLayout.KeyboardAccelerators.Add(paste);
        AppWindow.Closing += async (_, args) =>
        {
            if (_allowClose) return;
            if (RecordingTools.IsBusy)
            {
                args.Cancel = true;
                if (_closePromptOpen) return;
                _closePromptOpen = true;
                try
                {
                    var dialog = new ContentDialog { XamlRoot = RootLayout.XamlRoot, Title = "停止录制并退出？", Content = "当前录制会先保存到电脑。", PrimaryButtonText = "停止并退出", CloseButtonText = "继续录制", DefaultButton = ContentDialogButton.Close };
                    if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    {
                        await RecordingTools.StopAndSaveAsync();
                        if (!OcrEditor.HasUnsavedEdits || await OcrEditor.ConfirmDiscardEditsAsync()) { _allowClose = true; Close(); }
                    }
                }
                finally { _closePromptOpen = false; }
                return;
            }
            if (_busy) { args.Cancel = true; ShowStatus("正在处理", "请等待处理完成，或先取消当前任务。", InfoBarSeverity.Informational); return; }
            if (!OcrEditor.HasUnsavedEdits)
            {
                // No unsaved work — apply the close preference (minimize to tray or exit).
                var pref = ClosePreferences.Preference;
                if (pref is null)
                {
                    args.Cancel = true;
                    if (_closePromptOpen) return;
                    _closePromptOpen = true;
                    try
                    {
                        var checkBox = new CheckBox { Content = "不再提示，记住选择" };
                        var content = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = "关闭窗口时可以最小化到系统托盘，方便随时打开。", TextWrapping = TextWrapping.Wrap }, checkBox } };
                        var dialog = new ContentDialog
                        {
                            XamlRoot = RootLayout.XamlRoot, Title = "关闭窗口", Content = content,
                            PrimaryButtonText = "最小化到托盘", SecondaryButtonText = "直接退出",
                            DefaultButton = ContentDialogButton.Primary
                        };
                        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ms-appx:///ToolboxTheme.xaml") });
                        var result = await dialog.ShowAsync();
                        bool? choice = result == ContentDialogResult.Primary ? true : result == ContentDialogResult.Secondary ? false : null;
                        if (checkBox.IsChecked == true && choice is not null)
                            ClosePreferences.Save(choice);
                        if (choice == true) MinimizeToTray();
                        else if (choice == false) { _allowClose = true; Close(); }
                    }
                    finally { _closePromptOpen = false; }
                }
                else if (pref == true) { args.Cancel = true; MinimizeToTray(); }
                return;
            }
            args.Cancel = true;
            if (_closePromptOpen) return;
            _closePromptOpen = true;
            try { if (await OcrEditor.ConfirmDiscardEditsAsync()) { _allowClose = true; Close(); } }
            finally { _closePromptOpen = false; }
        };
        _minimizeHook = WindowMinimizeHook.Install(hwnd, OnWindowMinimized);
        // The tray icon is created once and stays for the whole application lifetime: whether the
        // main window is open, minimized to the taskbar, or hidden to the tray, the icon remains
        // available for the user to restore or exit the app at any time.
        _tray = new SystemTray(hwnd, "顺手工具箱", ActivateFromTray, () => { _allowClose = true; DispatcherQueue.TryEnqueue(Close); });
        _tray.Show();
        ClosePreferences.Load();
    }

    /// <summary>Minimizing the window moves it to the system tray, matching the close behavior.</summary>
    private void OnWindowMinimized()
    {
        // Re-enter the UI thread after WM_SIZE completes, avoiding nested window-state calls.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_suppressNextMinimizeTray) { _suppressNextMinimizeTray = false; return; }
            // Startup launches stay on the taskbar; the recorder deliberately minimizes there.
            // Programmatic tray show/restore also generates WM_SIZE messages; ignore those.
            if (_minimizedToTray || _changingTrayState || RecordingTools.IsBusy) return;
            MinimizeToTray();
        });
    }

    private void MinimizeToTray()
    {
        if (_minimizedToTray) return;
        _minimizedToTray = true;
        // The tray icon already exists; only the window needs to be hidden.
        AppWindow.Hide();
    }

    /// <summary>
    /// Tray left-click callback (runs on the tray's background thread): marshal all window work
    /// onto the UI thread. Works whether the window is hidden to the tray or just behind other
    /// windows — it always restores and activates the main window.
    /// </summary>
    private void ActivateFromTray() => DispatcherQueue.TryEnqueue(ActivateWindow);

    /// <summary>
    /// Called by single-instance activation when another launcher attempt is detected. Restores
    /// the main window from any state (hidden, minimized, or unfocused) and brings it to the
    /// foreground. Safe to call from any thread; window-state work is marshalled onto the UI queue.
    /// </summary>
    internal void ActivateFromExternal() => DispatcherQueue.TryEnqueue(ActivateWindow);

    private void ActivateWindow()
    {
        _minimizedToTray = false;
        _changingTrayState = true;
        if (_minimizeHook is not null) _minimizeHook.SuppressCallbacks = true;
        try
        {
            AppWindow.Show();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            // The window was hidden while minimized: force normal placement, otherwise
            // Show() presents it minimized and re-fires WM_SIZE/SIZE_MINIMIZED into the hook.
            WindowPlacement.Restore(hwnd);
            (this as Microsoft.UI.Xaml.Window).Activate();
            // Bring to foreground even when already visible but behind other windows.
            NativeWindowHelpers.SetForeground(hwnd);
        }
        finally
        {
            if (_minimizeHook is not null) _minimizeHook.SuppressCallbacks = false;
            _changingTrayState = false;
        }
        // The tray icon persists across minimize/restore cycles; do not destroy it here.
    }

    /// <summary>
    /// Hides the main window so it never appears in the snapshot, then lets the user drag a region on a
    /// full-screen overlay covering the whole virtual screen. Returns the cropped PNG path, or null when cancelled.
    /// </summary>
    private async Task<string?> CaptureScreenshotRegionAsync()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _suppressNextMinimizeTray = true;
        AppWindow.Hide();
        // Give the compositor a moment to actually remove the window from the screen.
        await Task.Delay(260);
        try
        {
            const int VirtualScreenX = 76, VirtualScreenY = 77, VirtualScreenWidth = 78, VirtualScreenHeight = 79;
            int left = RecordingNative.GetSystemMetrics(VirtualScreenX);
            int top = RecordingNative.GetSystemMetrics(VirtualScreenY);
            int width = RecordingNative.GetSystemMetrics(VirtualScreenWidth);
            int height = RecordingNative.GetSystemMetrics(VirtualScreenHeight);
            if (width < 16 || height < 16) throw new InvalidOperationException("没有可用的屏幕区域。");
            return await ScreenshotOverlay.SelectAsync(hwnd, left, top, width, height);
        }
        finally { ActivateWindow(); }
    }

    private void BuildOperationButtons()
    {
        OperationButtons.Children.Clear();
        OperationButtons.ColumnDefinitions.Clear();
        OperationButtons.RowDefinitions.Clear();
        string[] operations = Operations[_category];
        int columns = operations.Length == 4 || operations.Length > 6 ? 4 : Math.Min(3, operations.Length);
        for (int i = 0; i < columns; i++) OperationButtons.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < (operations.Length + columns - 1) / columns; i++) OperationButtons.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < operations.Length; i++)
        {
            var button = new ToggleButton
            {
                Content = new TextBlock { Text = operations[i], TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                Tag = i, HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(12, 8, 12, 8), IsChecked = i == OperationBox.SelectedIndex,
                Style = (Style)Application.Current.Resources["OperationTabStyle"]
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, operations[i]);
            button.Click += (_, _) =>
            {
                if (!_busy) OperationBox.SelectedIndex = (int)button.Tag;
                SyncOperationButtons();
            };
            Grid.SetColumn(button, i % columns);
            Grid.SetRow(button, i / columns);
            OperationButtons.Children.Add(button);
        }
    }

    private void SyncOperationButtons()
    {
        foreach (ToggleButton button in OperationButtons.Children) button.IsChecked = (int)button.Tag == OperationBox.SelectedIndex;
    }

    private void RestoreInputDraft()
    {
        if (_category is "software" or "text" or "system" or "office" || Operation is "误删恢复" or "链接下载视频" or "视频水印处理" or "网页转 PDF" or "下载视频字幕" or "屏幕录制" or "整理 PDF 页面" or "截图标注") return;
        var draft = _inputWorkspace.SwitchTo(CurrentInputTool, _inputs, _activeInputPath);
        _inputDraftMessage = draft.Message;
        _inputs.Clear();
        _inputs.AddRange(draft.Paths);
        _activeInputPath = draft.SelectedPath;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        if (_syncingInputLists) return;
        if (!_inputs.Contains(_activeInputPath ?? "", StringComparer.OrdinalIgnoreCase)) _activeInputPath = _inputs.FirstOrDefault();
        _syncingInputLists = true;
        try
        {
            SelectionSummary.Text = _inputs.Count == 0 ? "还没有添加文件" : $"已添加 {_inputs.Count} 项";
            SetVisible(InputDropHint, _inputs.Count == 0);
            InputList.AllowReorder = Operation == "合并 PDF";
            InputList.SetPaths(_inputs, _activeInputPath);
            OcrInputList.SetPaths(_inputs, _activeInputPath);
            _inputWorkspace.SaveCurrent(_inputs, _activeInputPath);
        }
        finally { _syncingInputLists = false; }
        if (Operation == "图片提取文字" && !StringComparer.OrdinalIgnoreCase.Equals(OcrEditor.InputPath, _activeInputPath))
            _inputSyncTask = SynchronizeOcrInputAsync(_activeInputPath);
        if (Operation == "整理 PDF 页面" && !StringComparer.OrdinalIgnoreCase.Equals(PdfPageTools.DocumentPath, _activeInputPath))
            _ = PdfPageTools.SetDocumentAsync(_activeInputPath);
        if (Operation == "PDF 阅读" && !StringComparer.OrdinalIgnoreCase.Equals(PdfReaderTools.DocumentPath, _activeInputPath))
            _ = PdfReaderTools.SetDocumentAsync(_activeInputPath);
    }

    private async Task SynchronizeOcrInputAsync(string? path)
    {
        try
        {
            if (await OcrEditor.SetInputAsync(path)) return;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex.Message); }
        _activeInputPath = OcrEditor.InputPath;
        _syncingInputLists = true;
        try { InputList.SelectPath(_activeInputPath); OcrInputList.SelectPath(_activeInputPath); }
        finally { _syncingInputLists = false; }
        _inputWorkspace.SaveCurrent(_inputs, _activeInputPath);
    }

    private void InputList_SelectionChanged(object? sender, InputPathEventArgs args)
    {
        if (_syncingInputLists || _busy) return;
        _activeInputPath = args.Path;
        UpdateSelection();
    }

    private async void InputList_RemoveRequested(object? sender, InputPathEventArgs args)
    {
        if (_busy || args.Path == null) return;
        if (StringComparer.OrdinalIgnoreCase.Equals(OcrEditor.InputPath, args.Path))
        {
            try { if (!await OcrEditor.SetInputAsync(null)) return; }
            catch (Exception ex) { ShowError(ex.Message); return; }
        }
        _inputs.RemoveAll(path => StringComparer.OrdinalIgnoreCase.Equals(path, args.Path));
        UpdateSelection();
    }

    private void InputList_MoveRequested(object? sender, InputMoveEventArgs args)
    {
        if (_busy || Operation != "合并 PDF") return;
        int index = _inputs.FindIndex(path => StringComparer.OrdinalIgnoreCase.Equals(path, args.Path));
        int target = index + args.Offset;
        if (index < 0 || target < 0 || target >= _inputs.Count) return;
        (_inputs[index], _inputs[target]) = (_inputs[target], _inputs[index]);
        UpdateSelection();
    }

    private async void Clear_Click(object sender, RoutedEventArgs args)
    {
        if (_busy) return;
        try
        {
            if (_category == "image" && !await OcrEditor.SetInputAsync(null)) return;
            _inputs.Clear(); _activeInputPath = null; UpdateSelection();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async Task RunOcrAsync()
    {
        await _inputSyncTask;
        if (OcrEditor.InputPath == null) { ShowError("请先添加需要识别的图片。"); return; }
        _cancellation = new CancellationTokenSource();
        SetBusy(true);
        TaskProgress.IsIndeterminate = true;
        ProgressText.Text = "正在识别图片中的文字…";
        try
        {
            string result = await OcrEditor.RunRecognitionAsync(_cancellation.Token);
            ProgressText.Text = string.IsNullOrWhiteSpace(result) ? "未识别到文字，可换一张更清晰的图片" : $"识别完成 · {result.Length:N0} 字";
            TaskProgress.Value = 100;
        }
        catch (OperationCanceledException) { ProgressText.Text = "已取消识别"; }
        catch (Exception ex) { ShowError(ex.Message); ProgressText.Text = "识别未完成"; }
        finally { TaskProgress.IsIndeterminate = false; SetBusy(false); _cancellation.Dispose(); _cancellation = null; }
    }

    private async Task LoadUninstallerAsync(string? selectId = null)
    {
        try { await Uninstaller.LoadAsync(selectId); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void OpenAdministratorWindow(string appId)
    {
        try
        {
            var start = new ProcessStartInfo(AppPaths.LauncherPath)
            { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppPaths.InstallationDirectory };
            start.ArgumentList.Add("--software");
            start.ArgumentList.Add("--select-app=" + appId);
            Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void OpenRecoveryAdministratorWindow()
    {
        try
        {
            var start = new ProcessStartInfo(AppPaths.LauncherPath)
            { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppPaths.InstallationDirectory };
            start.ArgumentList.Add("--recovery");
            Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { }
        catch (Exception ex) { ShowError(ex.Message); }
    }
}
