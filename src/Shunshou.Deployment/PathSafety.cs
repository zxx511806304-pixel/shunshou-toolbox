using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Shunshou.Deployment;

internal static class PathSafety
{
    internal const string ManifestName = "package-manifest.json";
    internal const int MaxEntries = 100_000;
    internal const long MaxUserBytes = 20L * 1024 * 1024 * 1024;
    internal const long MaxPackageBytes = 4L * 1024 * 1024 * 1024;

    internal static string Target(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal))
            throw new IOException("请选择本地磁盘上的完整软件文件夹路径。");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(full)!;
        if (full.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            throw new IOException("不能把磁盘根目录作为软件文件夹。");
        Relative(full[root.Length..].Replace('\\', '/'));
        NoLinks(full);
        if (File.Exists(full)) throw new IOException("软件位置是一个文件，请选择文件夹。");
        if (!Directory.Exists(Path.GetDirectoryName(full))) throw new IOException("软件文件夹的上一级目录不存在，请先选择已有位置。");
        return full;
    }

    internal static string Relative(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 1000 || path.Contains('\\') || Path.IsPathRooted(path))
            throw new InvalidDataException("安装包或目录包含无效路径。");
        var parts = path.Split('/');
        if (parts.Length > 64) throw new InvalidDataException("目录层级过深，无法安全处理。");
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.Any(c => c < 32 || "<>:\"|?*".Contains(c)))
                throw new InvalidDataException("安装包或目录包含无效路径：" + path);
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3])))
                throw new InvalidDataException("路径包含 Windows 保留名称：" + path);
        }
        return path;
    }

    internal static string Under(string root, string relative)
    {
        Relative(relative);
        var result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("路径超出软件文件夹。");
        return result;
    }

    internal static void NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("为保护关联文件，暂不更新含符号链接或目录联接的位置：" + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static void PlainEntry(string path)
    {
        NoLinks(path);
        var attributes = File.GetAttributes(path);
        if (OperatingSystem.IsWindows())
        {
            FileSystemSecurity security = (attributes & FileAttributes.Directory) != 0
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
            if (security.AreAccessRulesProtected || rules.Count == 0 || rules.Cast<FileSystemAccessRule>().Any(rule => !rule.IsInherited))
                throw new IOException("目录或文件使用了自定义访问权限，暂不自动更新，以免改变权限：" + path);
        }
        if ((attributes & (FileAttributes.Encrypted | FileAttributes.Offline | FileAttributes.Device)) != 0)
            throw new IOException("文件具有暂不支持安全复制的属性，请先另存为普通本地文件：" + path);
        // Copying only the default stream would silently discard user metadata. Refuse instead.
        if (OperatingSystem.IsWindows())
        {
            var handle = FindFirstStreamW(path, 0, out var data, 0);
            if (handle != new IntPtr(-1))
            {
                try
                {
                    do
                    {
                        if (data.Name.Equals(":Zone.Identifier:$DATA", StringComparison.OrdinalIgnoreCase)
                            && (attributes & FileAttributes.Directory) == 0 && data.Size is >= 0 and <= 65536)
                            continue;
                        if (!string.Equals(data.Name, "::$DATA", StringComparison.Ordinal))
                            throw new IOException("文件包含额外数据流，暂不自动更新此目录：" + path);
                    } while (FindNextStreamW(handle, out data));
                    var error = Marshal.GetLastWin32Error();
                    if (error != 38) throw new IOException("无法完整检查文件数据流：" + path);
                }
                finally { FindClose(handle); }
            }
            else
            {
                var error = Marshal.GetLastWin32Error();
                // FAT filesystems do not support streams; an empty directory can have no streams.
                if (error is not (38 or 1 or 50)) throw new IOException("无法检查文件数据流：" + path);
            }
        }
    }

    internal static string Key(string target) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.ToUpperInvariant())))[..16].ToLowerInvariant();
    internal static string JournalPath(string target) => Path.Combine(Path.GetDirectoryName(target)!, ".shunshou-update-" + Key(target) + ".json");
    internal static string Sibling(string target, string role, string transaction) =>
        Path.Combine(Path.GetDirectoryName(target)!, ".shunshou-" + Key(target) + "-" + role + "-" + transaction);

    internal static bool IsHash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    internal static bool HashEquals(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamData { public long Size; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStreamW(string fileName, int level, out StreamData data, int flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindNextStreamW(IntPtr handle, out StreamData data);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindClose(IntPtr handle);
}
