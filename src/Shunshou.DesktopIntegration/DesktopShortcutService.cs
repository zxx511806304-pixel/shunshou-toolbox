using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;

namespace Shunshou.DesktopIntegration;

public enum ShortcutStatus { Created, Updated, AlreadyCorrect, KeptDeleted, UnrelatedShortcut, InvalidPackage }
public sealed record ShortcutResult(ShortcutStatus Status, string? ShortcutPath);
public sealed record DesktopInitializationResult(ShortcutResult? Shortcut, IReadOnlyList<string> Warnings);
public sealed record ShortcutInfo(string TargetPath, string WorkingDirectory, string Description, string Arguments);

public sealed class DesktopShortcutService
{
    public const string ShortcutDescription = "顺手工具箱 · 本地处理 · 离线可用 [ShunshouToolbox.Desktop.v1]";
    private readonly string _desktopDirectory;
    private readonly IInstallationRegistration _registration;

    public DesktopShortcutService(string? desktopDirectory = null, IInstallationRegistration? registration = null)
    {
        _desktopDirectory = desktopDirectory ?? GetCurrentUserDesktop();
        _registration = registration ?? new InstallationRegistry();
    }

    public DesktopInitializationResult InitializeOnNormalLaunch(string directory)
    {
        var warnings = new List<string>();
        ShortcutResult? result = null;
        if (!PackageIdentity.IsValidDirectory(directory)) return new(new(ShortcutStatus.InvalidPackage, null), warnings);
        try { _registration.RememberDirectory(directory); }
        catch (Exception ex) { warnings.Add("记住软件目录失败：" + ex.Message); }
        try { result = EnsureShortcut(directory); }
        catch (Exception ex) { warnings.Add("创建桌面快捷方式失败：" + ex.Message); }
        return new(result, warnings);
    }

    public void RecordOptOut(string directory)
    {
        if (!PackageIdentity.IsValidDirectory(directory)) throw new IOException("所选目录不是完整的顺手工具箱目录。");
        var dataDirectory = Path.Combine(Path.GetFullPath(directory), "data");
        Directory.CreateDirectory(dataDirectory);
        if ((File.GetAttributes(dataDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("快捷方式记录目录不能是链接。");
        var markerPath = Path.Combine(dataDirectory, "desktop-shortcut.json");
        if (File.Exists(markerPath)) return;
        try
        {
            using var marker = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(marker, new { Version = 1, AttemptedUtc = DateTimeOffset.UtcNow, OptedOut = true });
            marker.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(markerPath)) { }
    }

    public ShortcutResult EnsureShortcut(string directory, bool explicitRequest = false)
    {
        if (!PackageIdentity.IsValidDirectory(directory)) return new(ShortcutStatus.InvalidPackage, null);
        directory = Path.GetFullPath(directory);
        var target = Path.Combine(directory, PackageIdentity.ExecutableName);
        var dataDirectory = Path.Combine(directory, "data");
        var markerPath = Path.Combine(dataDirectory, "desktop-shortcut.json");
        Directory.CreateDirectory(dataDirectory);
        if ((File.GetAttributes(dataDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("快捷方式记录目录不能是链接。");
        if (File.Exists(markerPath) && (File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("快捷方式记录不能是链接。");

        // Persist the first attempt before creating the shortcut. A deleted link is a user choice;
        // later normal launches never turn that choice into another automatic creation attempt.
        var alreadyAttempted = File.Exists(markerPath);
        if (!alreadyAttempted)
        {
            try
            {
                using var marker = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                JsonSerializer.Serialize(marker, new { Version = 1, AttemptedUtc = DateTimeOffset.UtcNow, Directory = directory });
                marker.Flush(flushToDisk: true);
            }
            catch (IOException) when (File.Exists(markerPath)) { alreadyAttempted = true; }
        }

        Directory.CreateDirectory(_desktopDirectory);
        var shortcutPath = Path.Combine(_desktopDirectory, PackageIdentity.ProductName + ".lnk");
        var exists = File.Exists(shortcutPath);
        if (!exists && alreadyAttempted && !explicitRequest) return new(ShortcutStatus.KeptDeleted, shortcutPath);
        if (exists && (File.GetAttributes(shortcutPath) & FileAttributes.ReparsePoint) != 0)
            return new(ShortcutStatus.UnrelatedShortcut, shortcutPath);

        if (!exists)
        {
            var stagedPath = Path.Combine(_desktopDirectory, ".shunshou-shortcut-" + Guid.NewGuid().ToString("N") + ".lnk");
            try
            {
                WriteShortcut(stagedPath, target, directory, ShortcutDescription);
                try { File.Move(stagedPath, shortcutPath, overwrite: false); }
                catch (IOException) when (File.Exists(shortcutPath)) { return new(ShortcutStatus.UnrelatedShortcut, shortcutPath); }
                return new(ShortcutStatus.Created, shortcutPath);
            }
            finally { if (File.Exists(stagedPath)) File.Delete(stagedPath); }
        }

        // Hold the existing link exclusively while inspecting and updating it. Another shortcut with
        // the same name cannot replace it between the ownership check and the write.
        using var existing = new FileStream(shortcutPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (existing.Length > 1024 * 1024) return new(ShortcutStatus.UnrelatedShortcut, shortcutPath);
        var snapshotPath = Path.Combine(_desktopDirectory, ".shunshou-shortcut-" + Guid.NewGuid().ToString("N") + ".lnk");
        try
        {
            using (var snapshot = new FileStream(snapshotPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) existing.CopyTo(snapshot);
            ShortcutInfo current;
            try { current = ReadShortcut(snapshotPath); }
            catch (COMException) { return new(ShortcutStatus.UnrelatedShortcut, shortcutPath); }
            if (current.Description != ShortcutDescription || !string.IsNullOrEmpty(current.Arguments) ||
                !string.Equals(Path.GetFileName(current.TargetPath), PackageIdentity.ExecutableName, StringComparison.OrdinalIgnoreCase))
                return new(ShortcutStatus.UnrelatedShortcut, shortcutPath);
            if (string.Equals(Path.GetFullPath(current.TargetPath), target, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFullPath(current.WorkingDirectory), directory, StringComparison.OrdinalIgnoreCase))
                return new(ShortcutStatus.AlreadyCorrect, shortcutPath);
            WriteShortcut(snapshotPath, target, directory, ShortcutDescription);
            using var replacement = File.OpenRead(snapshotPath);
            existing.Position = 0;
            replacement.CopyTo(existing);
            existing.SetLength(existing.Position);
            existing.Flush(flushToDisk: true);
            return new(ShortcutStatus.Updated, shortcutPath);
        }
        finally { if (File.Exists(snapshotPath)) File.Delete(snapshotPath); }
    }

    public static ShortcutInfo ReadShortcut(string path)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(path, 0);
            var target = new StringBuilder(32768);
            var working = new StringBuilder(32768);
            var description = new StringBuilder(2048);
            var arguments = new StringBuilder(32768);
            link.GetPath(target, target.Capacity, nint.Zero, 4);
            link.GetWorkingDirectory(working, working.Capacity);
            link.GetDescription(description, description.Capacity);
            link.GetArguments(arguments, arguments.Capacity);
            return new(target.ToString(), working.ToString(), description.ToString(), arguments.ToString());
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    private static void WriteShortcut(string path, string target, string workingDirectory, string description)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(workingDirectory);
            link.SetArguments("");
            link.SetDescription(description);
            link.SetIconLocation(target, 0);
            link.SetShowCmd(1);
            ((IPersistFile)link).Save(path, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    private static string GetCurrentUserDesktop()
    {
        var desktopId = new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
        Marshal.ThrowExceptionForHR(SHGetKnownFolderPath(ref desktopId, 0, nint.Zero, out var path));
        try { return Marshal.PtrToStringUni(path) ?? throw new IOException("无法找到当前用户的桌面目录。"); }
        finally { Marshal.FreeCoTaskMem(path); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, nint token, out nint path);
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, nint findData, uint flags);
        void GetIDList(out nint itemIdList);
        void SetIDList(nint itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int capacity);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int capacity);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int capacity);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(nint window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
