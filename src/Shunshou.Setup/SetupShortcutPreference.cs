using Shunshou.DesktopIntegration;

namespace Shunshou.Setup;

internal static class SetupShortcutPreference
{
    internal static IReadOnlyList<string> Apply(DesktopShortcutService shortcuts, string target, bool createRequested)
    {
        var notes = new List<string>();
        if (!createRequested)
        {
            // Persist the opt-out first: without this marker EnsureShortcut(false) may create a
            // missing link. A failure here must not fall through to any shortcut creation attempt.
            try { shortcuts.RecordOptOut(target); }
            catch { notes.Add("未能保存快捷方式偏好，现有桌面入口未调整。"); return notes; }
        }
        try
        {
            // A cleared checkbox prevents new links, while existing owned links still need their
            // entry point and icon repaired when an update changes the executable's filename.
            var shortcut = shortcuts.EnsureShortcut(target, explicitRequest: createRequested);
            if (shortcut.Status == ShortcutStatus.UnrelatedShortcut)
                notes.Add("桌面已有同名快捷方式，已保留原样。可从软件文件夹打开。");
            else if (shortcut.Status == ShortcutStatus.InvalidPackage)
                notes.Add("桌面快捷方式未调整，请从软件文件夹打开。");
        }
        catch (Exception ex) { notes.Add("桌面快捷方式未调整：" + ex.Message); }
        return notes;
    }
}
