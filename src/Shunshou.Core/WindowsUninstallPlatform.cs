using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using Microsoft.Win32;
using Windows.Management.Deployment;

namespace Shunshou.Core;

public sealed class WindowsUninstallPlatform : IUninstallPlatform
{
    public const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    public Task<IReadOnlyList<InstalledApplication>> ListAsync(CancellationToken ct) => Task.Run<IReadOnlyList<InstalledApplication>>(() =>
    {
        var found = new List<InstalledApplication>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in Environment.Is64BitOperatingSystem
                     ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : [RegistryView.Registry32])
        {
            ct.ThrowIfCancellationRequested();
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(UninstallRoot);
            if (uninstall is null) continue;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var key = uninstall.OpenSubKey(name);
                    if (key is null || Convert.ToString(key.GetValue("SystemComponent")) == "1" ||
                        key.GetValue("ParentKeyName") is not null || key.GetValue("ReleaseType") is not null) continue;
                    var title = Text(key, "DisplayName");
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    var identity = new RegistryIdentity(hive, view, UninstallRoot + "\\" + name);
                    var size = long.TryParse(Convert.ToString(key.GetValue("EstimatedSize")), out var kb) && kb > 0 && kb < long.MaxValue / 1024
                        ? kb * 1024 : 0;
                    var install = CleanPath(Text(key, "InstallLocation"));
                    var icon = CleanIcon(Text(key, "DisplayIcon"));
                    var command = Text(key, "UninstallString");
                    found.Add(new(identity.ToString(), title, Text(key, "DisplayVersion"), Text(key, "Publisher"), size,
                        icon, install, command, hive == RegistryHive.LocalMachine || UninstallService.PathRequiresElevation(install),
                        InstalledApplicationKind.Desktop, identity));
                }
                catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException) { }
            }
        }
        // Current-user Main packages only: no all-users operation, frameworks or resource-only packages.
        // WinRT can reject package enumeration on damaged/managed installations; desktop results remain usable.
        try
        {
            var manager = new PackageManager();
            foreach (var package in manager.FindPackagesForUserWithPackageTypes(string.Empty, PackageTypes.Main))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (package.IsFramework || package.IsResourcePackage || package.IsBundle ||
                        package.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.System) continue;
                    var id = package.Id;
                    var v = id.Version;
                    var title = Optional(() => package.DisplayName) ?? id.Name;
                    if (string.IsNullOrWhiteSpace(title) || title.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) title = id.Name;
                    var location = Optional(() => package.InstalledLocation?.Path);
                    string? icon = null;
                    try { if (package.Logo?.IsFile == true) icon = package.Logo.LocalPath; } catch { }
                    found.Add(new("store:" + id.FullName, title, $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}",
                        Optional(() => package.PublisherDisplayName) ?? id.Publisher, 0, icon, location, null, false, InstalledApplicationKind.Store,
                        PackageFullName: id.FullName));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        // HKCU has some shared 32/64 views. Collapse only the same key/content, not different products with a same title.
        return found.GroupBy(a => a.Kind == InstalledApplicationKind.Store ? a.Id :
                $"{a.RegistryIdentity!.Hive}|{a.RegistryIdentity.SubKey}|{a.DisplayName}|{a.Version}|{a.UninstallCommand}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()).OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }, ct);

    public async Task<int?> RunUninstallerAsync(UninstallCommand command, bool elevated, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(command.ExecutablePath)) throw new FileNotFoundException("卸载程序已不存在，可以检查该软件的残留。", command.ExecutablePath);
        var start = new ProcessStartInfo(command.ExecutablePath)
        {
            UseShellExecute = elevated,
            WorkingDirectory = Path.GetDirectoryName(command.ExecutablePath)!,
            WindowStyle = ProcessWindowStyle.Normal
        };
        // ShellExecute is only used for the system UAC verb, never cmd.exe or an interpolated shell command.
        if (elevated) start.Verb = "runas";
        foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动软件的卸载程序。");
        // Cancelling waits does not kill an installer while it may be modifying the system.
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode;
    }

    public async Task RemoveStorePackageAsync(string packageFullName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var manager = new PackageManager();
        var package = manager.FindPackagesForUserWithPackageTypes(string.Empty, PackageTypes.Main)
            .FirstOrDefault(p => p.Id.FullName == packageFullName);
        if (package is null) return;
        if (package.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.System || package.IsFramework || package.IsResourcePackage)
            throw new InvalidOperationException("此 Windows 系统组件不支持卸载。");
        var result = await manager.RemovePackageAsync(packageFullName, RemovalOptions.None).AsTask(ct).ConfigureAwait(false);
        if (result.ExtendedErrorCode is not null) throw new IOException("Windows 应用卸载未完成：" + result.ErrorText, result.ExtendedErrorCode);
    }

    public RegistrySnapshot? ReadRegistry(RegistryIdentity identity)
    {
        UninstallService.ValidateRegistryIdentity(identity);
        using var root = RegistryKey.OpenBaseKey(identity.Hive, identity.View);
        EnsureNoRegistryLinks(root, identity.SubKey, identity.View);
        using var key = root.OpenSubKey(identity.SubKey);
        if (key is null) return null;
        var count = 0;
        return ReadNode(key, 0, ref count, identity.View);
    }

    private static RegistrySnapshot ReadNode(RegistryKey key, int depth, ref int count, RegistryView view)
    {
        if (depth > 40 || ++count > 10_000) throw new IOException("注册表项过大，已跳过清理。");
        if (key.GetValueNames().Contains("SymbolicLinkValue", StringComparer.OrdinalIgnoreCase))
            throw new IOException("跳过注册表链接。");
        var values = key.GetValueNames().Order(StringComparer.Ordinal).Select(name =>
        {
            var kind = key.GetValueKind(name);
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var data = kind switch
            {
                RegistryValueKind.String or RegistryValueKind.ExpandString => (string?)value ?? "",
                RegistryValueKind.MultiString => JsonSerializer.Serialize((string[]?)value ?? []),
                RegistryValueKind.DWord => ((int)value!).ToString(CultureInfo.InvariantCulture),
                RegistryValueKind.QWord => ((long)value!).ToString(CultureInfo.InvariantCulture),
                RegistryValueKind.Binary or RegistryValueKind.None => Convert.ToBase64String((byte[]?)value ?? []),
                _ => throw new IOException("不支持备份该注册表值类型。")
            };
            return new RegistryValueSnapshot(name, kind, data);
        }).ToArray();
        var children = new SortedDictionary<string, RegistrySnapshot>(StringComparer.Ordinal);
        foreach (var name in key.GetSubKeyNames().Order(StringComparer.Ordinal))
        {
            EnsureNoRegistryLinks(key, name, view);
            using var child = key.OpenSubKey(name) ?? throw new IOException("注册表项在扫描时发生变化。");
            children.Add(name, ReadNode(child, depth + 1, ref count, view));
        }
        return new(values, children);
    }

    public void DeleteRegistry(RegistryIdentity identity)
    {
        UninstallService.ValidateRegistryIdentity(identity);
        using var root = RegistryKey.OpenBaseKey(identity.Hive, identity.View);
        EnsureNoRegistryLinks(root, identity.SubKey, identity.View);
        root.DeleteSubKeyTree(identity.SubKey, throwOnMissingSubKey: true);
    }

    public void RestoreRegistry(RegistryIdentity identity, RegistrySnapshot snapshot)
    {
        UninstallService.ValidateRegistryIdentity(identity);
        using var root = RegistryKey.OpenBaseKey(identity.Hive, identity.View);
        EnsureNoRegistryLinks(root, identity.SubKey, identity.View);
        using (var exists = root.OpenSubKey(identity.SubKey))
            if (exists is not null) throw new IOException("原注册表项已经存在，未覆盖。");
        using var key = root.CreateSubKey(identity.SubKey, writable: true) ?? throw new IOException("无法恢复注册表项。");
        WriteNode(key, snapshot, 0);
    }

    private static void WriteNode(RegistryKey key, RegistrySnapshot snapshot, int depth)
    {
        if (depth > 40) throw new IOException("备份注册表层级无效。");
        foreach (var value in snapshot.Values)
        {
            object data = value.Kind switch
            {
                RegistryValueKind.String or RegistryValueKind.ExpandString => value.Data,
                RegistryValueKind.MultiString => JsonSerializer.Deserialize<string[]>(value.Data)!,
                RegistryValueKind.DWord => int.Parse(value.Data, CultureInfo.InvariantCulture),
                RegistryValueKind.QWord => long.Parse(value.Data, CultureInfo.InvariantCulture),
                RegistryValueKind.Binary or RegistryValueKind.None => Convert.FromBase64String(value.Data),
                _ => throw new IOException("备份注册表值类型无效。")
            };
            key.SetValue(value.Name, data, value.Kind);
        }
        foreach (var (name, node) in snapshot.SubKeys)
        {
            if (name.IndexOfAny(['\\', '/', '\0']) >= 0 || name is "." or "..") throw new IOException("备份注册表路径无效。");
            using var child = key.CreateSubKey(name);
            WriteNode(child, node, depth + 1);
        }
    }

    public static UninstallCommand ParseUninstallCommand(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || commandLine.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("该软件没有有效的卸载命令。");
        var command = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        string executable;
        string arguments;
        if (command[0] == '"')
        {
            var quote = command.IndexOf('"', 1);
            if (quote < 2) throw new ArgumentException("卸载程序路径的引号不完整。");
            executable = command[1..quote];
            arguments = command[(quote + 1)..].Trim();
        }
        else
        {
            // Registered uninstall strings often omit quotes around a full path containing spaces.
            // Resolve the complete .exe path instead of ever trying C:\Program.exe.
            var end = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (end >= 0 && (end + 4 == command.Length || char.IsWhiteSpace(command[end + 4]))) end += 4;
            else end = command.IndexOf(' ');
            if (end < 0) end = command.Length;
            executable = command[..end];
            arguments = command[end..].Trim();
            // A directory itself may contain ".exe ". Do not truncate such an ambiguous path
            // into a different executable. Quoted paths remain supported without this ambiguity.
            if (arguments.Length > 0 && arguments[0] is not ('/' or '-' or '"') &&
                arguments.Split(' ', 2)[0].IndexOfAny(['\\', '/', ':']) >= 0)
                throw new ArgumentException("卸载路径未加引号且存在歧义，请从 Windows 设置中卸载。");
        }
        if (string.Equals(executable, "msiexec", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(executable, "msiexec.exe", StringComparison.OrdinalIgnoreCase))
            executable = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
        if (!Path.IsPathFullyQualified(executable) || executable.StartsWith("\\\\", StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("卸载命令必须指向本机明确的 EXE 路径。");
        var name = Path.GetFileNameWithoutExtension(executable);
        if (new[] { "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "rundll32", "regsvr32" }
            .Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException("此软件使用脚本式卸载程序，请从 Windows 设置中卸载。");
        var argv = SplitArguments(arguments).ToArray();
        if (name.Equals("msiexec", StringComparison.OrdinalIgnoreCase))
            for (var i = 0; i < argv.Length; i++)
                if (argv[i].Equals("/i", StringComparison.OrdinalIgnoreCase)) argv[i] = "/x";
                else if (argv[i].StartsWith("/i{", StringComparison.OrdinalIgnoreCase)) argv[i] = "/x" + argv[i][2..];
        return new(Path.GetFullPath(executable), argv);
    }

    private static string[] SplitArguments(string arguments)
    {
        var memory = CommandLineToArgvW("stub " + arguments, out var count);
        if (memory == IntPtr.Zero) throw new Win32Exception();
        try { return Enumerable.Range(1, count - 1).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(memory); }
    }

    private static string Text(RegistryKey key, string name) => Convert.ToString(key.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames))?.Trim() ?? "";
    private static string? Optional(Func<string?> read) { try { return read(); } catch { return null; } }
    private static string? CleanPath(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { var path = Environment.ExpandEnvironmentVariables(text.Trim().Trim('"')); return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) : null; }
        catch { return null; }
    }
    private static string? CleanIcon(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim();
        var comma = value.LastIndexOf(',');
        if (comma >= 0 && int.TryParse(value[(comma + 1)..], out _)) value = value[..comma];
        return CleanPath(value);
    }

    private static void EnsureNoRegistryLinks(RegistryKey root, string subKey, RegistryView view)
    {
        var prefix = "";
        foreach (var segment in subKey.Split('\\'))
        {
            prefix = prefix.Length == 0 ? segment : prefix + "\\" + segment;
            var error = RegOpenKeyEx(root.Handle.DangerousGetHandle(), prefix, 8, 1u | (view == RegistryView.Registry64 ? 0x100u : 0x200u), out var handle);
            if (error == 2) return; // Missing is harmless for reads/restoration; no link can occur below it.
            if (error != 0) throw new Win32Exception(error, "无法确认注册表路径是否包含链接。");
            using (handle)
            {
                uint length = 0;
                error = RegQueryValueEx(handle, "SymbolicLinkValue", IntPtr.Zero, out var kind, IntPtr.Zero, ref length);
                if (error == 0 && kind == 6) throw new IOException("注册表路径包含链接，已跳过。");
                if (error != 0 && error != 2 && error != 234) throw new Win32Exception(error);
            }
        }
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", CharSet = CharSet.Unicode)]
    private static extern int RegOpenKeyEx(IntPtr key, string name, uint options, uint access, out Microsoft.Win32.SafeHandles.SafeRegistryHandle result);
    [DllImport("advapi32.dll", EntryPoint = "RegQueryValueExW", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueEx(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, string value,
        IntPtr reserved, out uint type, IntPtr data, ref uint size);
}
