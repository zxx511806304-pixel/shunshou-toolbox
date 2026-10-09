using Microsoft.UI.Xaml;
using Shunshou.Core;

namespace Shunshou.App;

public sealed partial class MainWindow
{
    private QuickLauncherWindow? _launcher;
    private QuickLauncherHotKeys? _launcherHotKeys;

    /// <summary>Registers the app-lifetime global shortcut; skipped for automated verification launches.</summary>
    private void InitializeQuickLauncher()
    {
        if (_appearanceVerificationLaunch) return;
        _launcherHotKeys = QuickLauncherHotKeys.TryCreate();
        if (_launcherHotKeys is null) return;
        _launcherHotKeys.ToggleRequested += (_, _) => DispatcherQueue.TryEnqueue(ToggleQuickLauncher);
    }

    /// <summary>Hotkey callback: a second press hides the box again, just like system launchers.</summary>
    internal void ToggleQuickLauncher()
    {
        if (_launcher is not null && _launcher.IsShown) { _launcher.HideWindow(); return; }
        _launcher ??= new QuickLauncherWindow(this, RootLayout.ActualTheme, _launcherHotKeys?.ShortcutLabel ?? "Alt + Space");
        _launcher.ShowAndFocus();
    }

    internal List<ToolSuggestion> LauncherToolMatches(string query) => BuildSuggestions(query).Take(6).ToList();

    /// <summary>Indexed file matches for the launcher; stays silent whenever the index is not ready yet.</summary>
    internal async Task<IReadOnlyList<FileSearchResult>> LauncherFileSearchAsync(string query, CancellationToken cancellation)
    {
        try
        {
            if (!_searchIndexConnected)
            {
                var status = await _indexedSearch.GetStatusAsync(cancellation);
                _searchIndexConnected = status.State == EverythingIndexState.Ready;
                if (!_searchIndexConnected) return [];
            }
            var roots = new FileService().GetLocalDrives().Select(drive => drive.RootPath).ToArray();
            if (roots.Length == 0) return [];
            var summary = await _indexedSearch.SearchAsync(roots, query, false, null, cancellation, maxResults: 8, batchSize: 8);
            return summary.Results;
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            App.LogException(ex, "quick-launcher-search");
            return [];
        }
    }

    /// <summary>Restores the main window from tray/minimized/background, then opens the chosen tool.</summary>
    internal void OpenToolFromLauncher(ToolSuggestion suggestion)
    {
        ActivateWindow();
        NavigateToTool(suggestion.Category, suggestion.Operation);
    }

    internal void OpenPathFromLauncher(string path) => OpenPath(path);

    private void DisposeQuickLauncher()
    {
        _launcherHotKeys?.Dispose();
        _launcherHotKeys = null;
        if (_launcher is not null)
        {
            _launcher.ForceClose();
            _launcher = null;
        }
    }
}
