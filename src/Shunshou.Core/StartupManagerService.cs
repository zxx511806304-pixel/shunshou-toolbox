using System.Diagnostics;
using Microsoft.Win32;

namespace Shunshou.Core;

public sealed record StartupEntry(string Id, string Name, string Command, string Source, bool Enabled, bool RequiresAdmin, string? Company, bool Suspicious);

/// <summary>启动项管理：枚举注册表 Run/RunOnce 与启动文件夹，支持禁用/恢复。纯本地、离线。</summary>
public sealed class StartupManagerService
{
    private const string BackupRoot = @"Software\ShunshouToolbox\StartupBackup";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";

    public async Task<IReadOnlyList<StartupEntry>> ListAsync()
    {
        var list = new List<StartupEntry>();
        list.AddRange(await EnumerateRegistryAsync(RegistryHive.CurrentUser, RegistryView.Registry64, RunKey, "注册表 · 当前用户", false));
        list.AddRange(await EnumerateRegistryAsync(RegistryHive.CurrentUser, RegistryView.Registry64, RunOnceKey, "注册表 · 当前用户", false));
        list.AddRange(await EnumerateRegistryAsync(RegistryHive.LocalMachine, RegistryView.Registry64, RunKey, "注册表 · 所有用户", true));
        list.AddRange(await EnumerateRegistryAsync(RegistryHive.LocalMachine, RegistryView.Registry64, RunOnceKey, "注册表 · 所有用户", true));
        list.AddRange(await EnumerateStartupFolderAsync(Environment.SpecialFolder.Startup, "启动文件夹 · 当前用户", false));
        list.AddRange(await EnumerateStartupFolderAsync(Environment.SpecialFolder.CommonStartup, "启动文件夹 · 所有用户", true));
        list.AddRange(await EnumerateBackupAsync());
        return list;
    }

    public Task DisableAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Id 不能为空", nameof(id));
        if (id.StartsWith("hkcu-run:", StringComparison.Ordinal) || id.StartsWith("hkcu-runonce:", StringComparison.Ordinal))
            return DisableRegistryAsync(id);
        if (id.StartsWith("startup-folder:", StringComparison.Ordinal))
            return DisableStartupFolderAsync(id);
        if (id.StartsWith("hklm-run:", StringComparison.Ordinal) || id.StartsWith("hklm-runonce:", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("该项需要管理员权限，请以管理员身份运行后重试。");
        throw new InvalidOperationException("不支持的启动项类型：" + id);
    }

    public Task EnableAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Id 不能为空", nameof(id));
        if (id.StartsWith("hkcu-run:", StringComparison.Ordinal) || id.StartsWith("hkcu-runonce:", StringComparison.Ordinal))
            return EnableRegistryAsync(id);
        if (id.StartsWith("startup-folder:", StringComparison.Ordinal))
            return EnableStartupFolderAsync(id);
        if (id.StartsWith("hklm-run:", StringComparison.Ordinal) || id.StartsWith("hklm-runonce:", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("该项需要管理员权限，请以管理员身份运行后重试。");
        throw new InvalidOperationException("不支持的启动项类型：" + id);
    }

    private static Task<List<StartupEntry>> EnumerateRegistryAsync(RegistryHive hive, RegistryView view, string subKey, string source, bool requiresAdmin)
    {
        var result = new List<StartupEntry>();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey);
            if (key is null) return Task.FromResult(result);
            foreach (string name in key.GetValueNames())
            {
                try
                {
                    var value = key.GetValue(name) as string;
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    string exePath = ExtractExePath(value);
                    string? company = GetCompanyName(exePath);
                    bool suspicious = IsSuspicious(exePath, value);
                    string idPrefix = hive == RegistryHive.CurrentUser
                        ? (subKey.EndsWith("RunOnce", StringComparison.OrdinalIgnoreCase) ? "hkcu-runonce:" : "hkcu-run:")
                        : (subKey.EndsWith("RunOnce", StringComparison.OrdinalIgnoreCase) ? "hklm-runonce:" : "hklm-run:");
                    result.Add(new StartupEntry(idPrefix + name, name, value, source, true, requiresAdmin, company, suspicious));
                }
                catch { }
            }
        }
        catch { }
        return Task.FromResult(result);
    }

    private static Task<List<StartupEntry>> EnumerateStartupFolderAsync(Environment.SpecialFolder folder, string source, bool requiresAdmin)
    {
        var result = new List<StartupEntry>();
        string path = Environment.GetFolderPath(folder);
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return Task.FromResult(result);
        var extensions = new[] { ".lnk", ".exe", ".bat", ".cmd", ".url" };
        foreach (string file in Directory.EnumerateFiles(path, "*.*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (!extensions.Contains(ext)) continue;
                string name = Path.GetFileNameWithoutExtension(file);
                string command = file;
                string? company = GetCompanyName(file);
                bool suspicious = IsSuspicious(file, command);
                result.Add(new StartupEntry("startup-folder:" + file, name, command, source, true, requiresAdmin, company, suspicious));
            }
            catch { }
        }
        // .disabled files (currently disabled)
        foreach (string file in Directory.EnumerateFiles(path, "*.disabled", SearchOption.TopDirectoryOnly))
        {
            try
            {
                string original = file[..^".disabled".Length];
                string ext = Path.GetExtension(original).ToLowerInvariant();
                if (!extensions.Contains(ext)) continue;
                string name = Path.GetFileNameWithoutExtension(original);
                string command = original;
                string? company = GetCompanyName(original);
                bool suspicious = IsSuspicious(original, command);
                result.Add(new StartupEntry("startup-folder:" + original, name, command, source, false, requiresAdmin, company, suspicious));
            }
            catch { }
        }
        return Task.FromResult(result);
    }

    private static Task<List<StartupEntry>> EnumerateBackupAsync()
    {
        var result = new List<StartupEntry>();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(BackupRoot);
            if (key is null) return Task.FromResult(result);
            foreach (string subKeyName in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(subKeyName);
                if (sub is null) continue;
                string sourceKey = sub.GetValue("SourceKey") as string ?? "";
                string sourceName = sub.GetValue("SourceName") as string ?? subKeyName;
                string command = sub.GetValue("Command") as string ?? "";
                string exePath = ExtractExePath(command);
                string? company = GetCompanyName(exePath);
                bool suspicious = IsSuspicious(exePath, command);
                string idPrefix = sourceKey.EndsWith("RunOnce", StringComparison.OrdinalIgnoreCase) ? "hkcu-runonce:" : "hkcu-run:";
                result.Add(new StartupEntry(idPrefix + sourceName, sourceName, command, "注册表 · 当前用户（已禁用）", false, false, company, suspicious));
            }
        }
        catch { }
        return Task.FromResult(result);
    }

    private Task DisableRegistryAsync(string id)
    {
        string name = id.Split(':', 2)[1];
        string subKey = id.StartsWith("hkcu-runonce:", StringComparison.Ordinal) ? RunOnceKey : RunKey;
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(subKey, writable: true);
        if (key is null) throw new InvalidOperationException("注册表项不存在：" + subKey);
        var value = key.GetValue(name) as string;
        if (value is null) throw new InvalidOperationException("启动项不存在或已被移除。");
        using var backupKey = baseKey.CreateSubKey(BackupRoot + "\\" + name, writable: true);
        backupKey.SetValue("SourceKey", subKey);
        backupKey.SetValue("SourceName", name);
        backupKey.SetValue("Command", value);
        key.DeleteValue(name);
        return Task.CompletedTask;
    }

    private Task EnableRegistryAsync(string id)
    {
        string name = id.Split(':', 2)[1];
        string subKey = id.StartsWith("hkcu-runonce:", StringComparison.Ordinal) ? RunOnceKey : RunKey;
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var backupKey = baseKey.OpenSubKey(BackupRoot + "\\" + name, writable: true);
        if (backupKey is null) throw new InvalidOperationException("备份记录不存在，无法恢复。");
        string command = backupKey.GetValue("Command") as string ?? "";
        string sourceKey = backupKey.GetValue("SourceKey") as string ?? subKey;
        string sourceName = backupKey.GetValue("SourceName") as string ?? name;
        using var key = baseKey.CreateSubKey(sourceKey, writable: true);
        key.SetValue(sourceName, command);
        baseKey.DeleteSubKeyTree(BackupRoot + "\\" + name, throwOnMissingSubKey: false);
        return Task.CompletedTask;
    }

    private static Task DisableStartupFolderAsync(string id)
    {
        string path = id.Split(':', 2)[1];
        string disabled = path + ".disabled";
        if (!File.Exists(path)) throw new InvalidOperationException("文件不存在：" + path);
        if (File.Exists(disabled)) throw new InvalidOperationException("已存在同名备份文件，请先处理：" + disabled);
        File.Move(path, disabled);
        return Task.CompletedTask;
    }

    private static Task EnableStartupFolderAsync(string id)
    {
        string path = id.Split(':', 2)[1];
        string disabled = path + ".disabled";
        if (!File.Exists(disabled)) throw new InvalidOperationException("备份文件不存在：" + disabled);
        if (File.Exists(path)) throw new InvalidOperationException("目标文件已存在，无法恢复：" + path);
        File.Move(disabled, path);
        return Task.CompletedTask;
    }

    internal static string ExtractExePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return string.Empty;
        string trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            int end = trimmed.IndexOf('"', 1);
            if (end > 1) return trimmed.Substring(1, end - 1);
        }
        int space = trimmed.IndexOf(' ');
        string first = space > 0 ? trimmed[..space] : trimmed;
        return Environment.ExpandEnvironmentVariables(first);
    }

    private static string? GetCompanyName(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var info = FileVersionInfo.GetVersionInfo(path);
            return string.IsNullOrWhiteSpace(info.CompanyName) ? null : info.CompanyName;
        }
        catch { return null; }
    }

    internal static bool IsSuspicious(string exePath, string command)
    {
        string expanded = Environment.ExpandEnvironmentVariables(exePath);
        if (!string.IsNullOrWhiteSpace(expanded))
        {
            string temp = Path.GetTempPath();
            if (expanded.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
                expanded.IndexOf("\\AppData\\Local\\Temp", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        if (!File.Exists(expanded) && (command.Contains("powershell", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("wscript", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("mshta", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("https://", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }
}
