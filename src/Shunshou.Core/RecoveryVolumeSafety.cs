using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Shunshou.Core;

internal static class RecoveryVolumeSafety
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);

    public static string VolumeIdentity(string path)
    {
        string current = Path.GetFullPath(path);
        while (!Directory.Exists(current) && !File.Exists(current))
            current = Path.GetDirectoryName(current) ?? throw new IOException("无法确认保存位置所在的磁盘。");
        using var handle = CreateFileW(current, 0, 7, 0, 3, 0x02000000, 0);
        if (handle.IsInvalid) throw new IOException("无法核实恢复路径所在的磁盘。请选择可访问的本地磁盘。", new Win32Exception(Marshal.GetLastWin32Error()));
        var name = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandleW(handle, name, (uint)name.Capacity, 1); // VOLUME_NAME_GUID
        if (length == 0 || length >= name.Capacity) throw new IOException("无法核实磁盘身份，未开始写入恢复文件。");
        string final = name.ToString();
        int end = final.IndexOf('}');
        if (!final.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) || end < 0)
            throw new IOException("恢复保存位置必须能够识别为本地磁盘。");
        return final[..(end + 1)];
    }
}
