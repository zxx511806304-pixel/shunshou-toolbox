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
    private Task _inputSyncTask = Task.CompletedTask;
    private string? _inputDraftMessage;

    private void InitializeWorkspaces(IntPtr hwnd)
    {
        OcrEditor.HostWindowHandle = Uninstaller.HostWindowHandle = hwnd;
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
            if (_busy) { args.Cancel = true; ShowStatus("正在处理", "请等待处理完成，或先取消当前任务。", InfoBarSeverity.Informational); return; }
            if (!OcrEditor.HasUnsavedEdits) return;
            args.Cancel = true;
            if (_closePromptOpen) return;
            _closePromptOpen = true;
            try { if (await OcrEditor.ConfirmDiscardEditsAsync()) { _allowClose = true; Close(); } }
            finally { _closePromptOpen = false; }
        };
    }

    private void BuildOperationButtons()
    {
        OperationButtons.Children.Clear();
        OperationButtons.ColumnDefinitions.Clear();
        OperationButtons.RowDefinitions.Clear();
        string[] operations = Operations[_category];
        int columns = Math.Min(3, operations.Length);
        for (int i = 0; i < columns; i++) OperationButtons.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < (operations.Length + columns - 1) / columns; i++) OperationButtons.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < operations.Length; i++)
        {
            var button = new ToggleButton
            {
                Content = operations[i], Tag = i, HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(12, 8, 12, 8), IsChecked = i == OperationBox.SelectedIndex
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
        if (_category == "software") return;
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
            var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("无法找到程序路径。"))
            { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
            start.ArgumentList.Add("--software");
            start.ArgumentList.Add("--select-app=" + appId);
            Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { }
        catch (Exception ex) { ShowError(ex.Message); }
    }
}
