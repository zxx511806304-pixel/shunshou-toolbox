using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace Shunshou.Core;

public sealed record HijackFinding(string Id, string Kind, string Title, string Detail, bool CanAutoFix);

public sealed record HijackScanResult(IReadOnlyList<HijackFinding> Findings, int SkippedDirectories);

/// <summary>浏览器劫持与弹窗源头扫描：检查快捷方式、主页设置、启动项。</summary>
public sealed class HijackScanService
{
    private static readonly string[] HomepageWhitelist =
    [
        "about:blank", "about:home",
        "baidu.com", "bing.com", "google.com", "sogou.com", "so.com",
        // go.microsoft.com/fwlink/?LinkId=255141 is the Windows default first-run home page;
        // msn.com is the default Edge home page on many installs.
        "microsoft.com", "msn.com",
        "chrome://newtab", "edge://newtab", "chrome://", "edge://"
    ];

    public async Task<HijackScanResult> ScanAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var findings = new List<HijackFinding>();
        var skipped = new List<string>();
        progress?.Report("正在检查桌面快捷方式…");
        await Task.Delay(50, ct).ConfigureAwait(false);
        findings.AddRange(await ScanShortcutsAsync(skipped, ct).ConfigureAwait(false));

        progress?.Report("正在检查浏览器主页设置…");
        await Task.Delay(50, ct).ConfigureAwait(false);
        findings.AddRange(await ScanHomepageAsync().ConfigureAwait(false));

        progress?.Report("正在检查浏览器配置…");
        await Task.Delay(50, ct).ConfigureAwait(false);
        findings.AddRange(await ScanBrowserPreferencesAsync().ConfigureAwait(false));

        progress?.Report("正在检查可疑启动项…");
        await Task.Delay(50, ct).ConfigureAwait(false);
        findings.AddRange(await ScanSuspiciousStartupAsync().ConfigureAwait(false));

        progress?.Report("检查完成");
        return new HijackScanResult(findings, skipped.Count);
    }

    public async Task FixAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Id 不能为空", nameof(id));
        if (id.StartsWith("lnk:", StringComparison.Ordinal))
        {
            string path = id.Split(':', 2)[1];
            await Task.Run(() => ClearShortcutArguments(path)).ConfigureAwait(false);
            return;
        }
        if (id.Equals("ie-homepage", StringComparison.Ordinal))
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Internet Explorer\Main", writable: true);
            if (key is null) throw new InvalidOperationException("注册表项不存在。");
            key.SetValue("Start Page", "about:blank");
            return;
        }
        throw new InvalidOperationException("不支持自动修复的项目：" + id);
    }

    private static async Task<List<HijackFinding>> ScanShortcutsAsync(List<string> skipped, CancellationToken ct)
    {
        var findings = new List<HijackFinding>();
        // Use the real per-user and all-machine roots. The Start Menu root also contains a
        // localized compatibility junction ("程序" -> "Programs") whose ACL denies traversal;
        // SpecialFolder.Programs resolves straight to the real "Programs" folder, so we never
        // touch that junction.
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
        }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase);

        var files = new List<string>();
        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            EnumerateLnkFiles(root, files, skipped, ct);
        }

        foreach (string file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (target, arguments) = await Task.Run(() => ReadShortcut(file), ct).ConfigureAwait(false);
                if (arguments.IndexOf("http://", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    arguments.IndexOf("https://", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    findings.Add(new HijackFinding(
                        "lnk:" + file,
                        "快捷方式",
                        "快捷方式被追加网址",
                        $"{Path.GetFileName(file)} 的目标被附加了网址参数：{arguments}。这通常是不良软件劫持浏览器首页的手段。",
                        true));
                }
            }
            catch { }
        }
        return findings;
    }

    /// <summary>Recursively collects .lnk files. Any directory that denies access or is a reparse
    /// point (junction/symlink, e.g. the localized "程序" compatibility junction) is skipped and
    /// recorded instead of aborting the whole scan.</summary>
    private static void EnumerateLnkFiles(string directory, List<string> files, List<string> skipped, CancellationToken ct)
    {
        string[] currentFiles;
        try
        {
            currentFiles = Directory.GetFiles(directory, "*.lnk");
        }
        catch (UnauthorizedAccessException) { skipped.Add(directory); return; }
        catch (IOException) { skipped.Add(directory); return; }
        files.AddRange(currentFiles);

        string[] subDirectories;
        try
        {
            subDirectories = Directory.GetDirectories(directory);
        }
        catch (UnauthorizedAccessException) { skipped.Add(directory); return; }
        catch (IOException) { skipped.Add(directory); return; }

        foreach (string sub in subDirectories)
        {
            ct.ThrowIfCancellationRequested();
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(sub);
            }
            catch (UnauthorizedAccessException) { skipped.Add(sub); continue; }
            catch (IOException) { skipped.Add(sub); continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                skipped.Add(sub);
                continue;
            }
            EnumerateLnkFiles(sub, files, skipped, ct);
        }
    }

    private static async Task<List<HijackFinding>> ScanHomepageAsync()
    {
        var findings = new List<HijackFinding>();
        await Task.CompletedTask;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Internet Explorer\Main");
            var startPage = key?.GetValue("Start Page") as string;
            if (!string.IsNullOrWhiteSpace(startPage) && !IsHomepageAllowed(startPage))
            {
                findings.Add(new HijackFinding(
                    "ie-homepage",
                    "主页",
                    "IE/旧 Edge 主页被修改",
                    $"当前主页被设置为：{startPage}。",
                    true));
            }
        }
        catch { }
        return findings;
    }

    private static async Task<List<HijackFinding>> ScanBrowserPreferencesAsync()
    {
        var findings = new List<HijackFinding>();
        await Task.CompletedTask;
        // Chrome / Edge Secure Preferences are protected by browser integrity checks;
        // directly editing triggers validation failure. We only read and report.
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\User Data\Default\Secure Preferences"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\User Data\Default\Secure Preferences")
        };
        foreach (string path in paths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                string json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                // Parse the file properly and inspect ONLY the top-level "homepage" string.
                // String-searching used to match unrelated nested fields (e.g. new-tab-page
                // identifiers), which produced hash-looking false positives.
                if (!TryReadConfiguredHomepage(json, out string homepage)) continue;
                if (!string.IsNullOrWhiteSpace(homepage) && !IsHomepageAllowed(homepage))
                {
                    findings.Add(new HijackFinding(
                        "browser-prefs:" + path,
                        "浏览器配置",
                        "浏览器主页被设置为陌生网址",
                        $"检测到浏览器主页被设置为 {homepage}。由于浏览器会校验配置文件完整性，请打开浏览器设置手动检查并恢复。",
                        false));
                }
            }
            catch (JsonException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return findings;
    }

    private static async Task<List<HijackFinding>> ScanSuspiciousStartupAsync()
    {
        var findings = new List<HijackFinding>();
        await Task.CompletedTask;
        try
        {
            var startup = new StartupManagerService();
            var entries = await startup.ListAsync().ConfigureAwait(false);
            foreach (var entry in entries)
            {
                if (entry.Suspicious)
                {
                    findings.Add(new HijackFinding(
                        "startup:" + entry.Id,
                        "启动项",
                        "可疑开机启动项",
                        $"{entry.Name} 位于临时目录或命令可疑：{entry.Command}。建议前往「启动项管理」禁用或删除。",
                        false));
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        return findings;
    }

    /// <summary>Reads only the top-level "homepage" string from a Chrome/Edge preferences JSON.
    /// Returns false when there is no top-level homepage or the JSON cannot be parsed.</summary>
    internal static bool TryReadConfiguredHomepage(string json, out string homepage)
    {
        homepage = string.Empty;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            if (doc.RootElement.TryGetProperty("homepage", out JsonElement prop) &&
                prop.ValueKind == JsonValueKind.String)
            {
                homepage = prop.GetString() ?? string.Empty;
                return true;
            }
        }
        catch (JsonException) { }
        return false;
    }

    private static bool IsHomepageAllowed(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;
        string lower = url.ToLowerInvariant();
        foreach (string allowed in HomepageWhitelist)
            if (lower.Contains(allowed, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static (string TargetPath, string Arguments) ReadShortcut(string path)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return (string.Empty, string.Empty);
            shell = Activator.CreateInstance(shellType);
            shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { path });
            if (shortcut is null) return (string.Empty, string.Empty);
            Type shortcutType = shortcut.GetType();
            string target = shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string ?? "";
            string arguments = shortcutType.InvokeMember("Arguments", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string ?? "";
            return (target, arguments);
        }
        catch { return (string.Empty, string.Empty); }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void ClearShortcutArguments(string path)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) throw new InvalidOperationException("无法创建 WScript.Shell COM 对象。");
            shell = Activator.CreateInstance(shellType);
            shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { path });
            if (shortcut is null) throw new InvalidOperationException("无法读取快捷方式。");
            Type shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "" });
            shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }
}
