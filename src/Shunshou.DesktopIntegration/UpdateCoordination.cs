using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Shunshou.DesktopIntegration;

public sealed record RunningAppProcess(int ProcessId, string Name, string? ExecutablePath, bool LocationUncertain);

public static class UpdateCoordination
{
    public static IDisposable? AcquireApplicationLease(string directory, string? lockRoot = null) => AcquireLease(directory, updating: false, lockRoot);
    public static IDisposable? AcquireUpdateLease(string directory, string? lockRoot = null) => AcquireLease(directory, updating: true, lockRoot);

    // Windows file sharing provides multiple app readers and an exclusive updater, across async
    // continuations and elevated windows. The lock lives outside the package, so it survives renames.
    private static IDisposable? AcquireLease(string directory, bool updating, string? lockRoot)
    {
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeDirectory(directory))));
        lockRoot ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShunshouToolbox", "locks");
        Directory.CreateDirectory(lockRoot);
        var path = Path.Combine(lockRoot, identity + ".lock");
        using var gate = new Mutex(false, @"Local\ShunshouToolbox.LockCreation." + identity);
        bool ownsGate;
        try { ownsGate = gate.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { ownsGate = true; }
        if (!ownsGate) return null;
        try
        {
            if (!File.Exists(path))
            {
                try { using var created = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite); }
                catch (IOException) when (File.Exists(path)) { }
            }
            try
            {
                return new FileStream(path, FileMode.Open, updating ? FileAccess.ReadWrite : FileAccess.Read,
                    updating ? FileShare.None : FileShare.Read);
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) { return null; }
        }
        finally { gate.ReleaseMutex(); }
    }

    public static string NormalizeDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("需要软件目录的完整路径。", nameof(directory));
        // Resolve junction aliases and 8.3 paths for existing directories. For a new target use its
        // nearest existing parent, so app and setup derive the same identity after creation.
        var suffix = new Stack<string>();
        var ancestor = fullPath;
        while (!Directory.Exists(ancestor))
        {
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(ancestor));
            if (string.IsNullOrEmpty(parent)) break;
            suffix.Push(Path.GetFileName(Path.TrimEndingDirectorySeparator(ancestor)));
            ancestor = parent;
        }
        using var handle = CreateFile(ancestor, 0, FileShare.ReadWrite | FileShare.Delete, nint.Zero, FileMode.Open, 0x02000000, nint.Zero);
        if (!handle.IsInvalid)
        {
            var buffer = new StringBuilder(32768);
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length > 0 && length < buffer.Capacity)
            {
                ancestor = buffer.ToString();
                if (ancestor.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) ancestor = @"\\" + ancestor[8..];
                else if (ancestor.StartsWith(@"\\?\", StringComparison.Ordinal)) ancestor = ancestor[4..];
                while (suffix.Count > 0) ancestor = Path.Combine(ancestor, suffix.Pop());
                fullPath = ancestor;
            }
        }
        return Path.TrimEndingDirectorySeparator(fullPath).ToUpperInvariant();
    }

    public static IReadOnlyList<RunningAppProcess> FindRunningAppProcesses(string directory)
    {
        var root = NormalizeDirectory(directory);
        var toolsRoot = root + Path.DirectorySeparatorChar + "TOOLS" + Path.DirectorySeparatorChar;
        var blockers = new List<RunningAppProcess>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string name = "";
                try
                {
                    if (process.Id == Environment.ProcessId) continue;
                    name = process.ProcessName;
                    var executable = process.MainModule?.FileName;
                    if (string.IsNullOrEmpty(executable))
                    {
                        if (IsKnownApplicationName(name)) blockers.Add(new(process.Id, name, null, true));
                        continue;
                    }
                    var processRoot = NormalizeDirectory(Path.GetDirectoryName(executable)!);
                    var normalizedExecutable = processRoot + Path.DirectorySeparatorChar + Path.GetFileName(executable).ToUpperInvariant();
                    if ((processRoot == root && IsApplicationEntryName(name)) || normalizedExecutable.StartsWith(toolsRoot, StringComparison.OrdinalIgnoreCase))
                        blockers.Add(new(process.Id, name, executable, false));
                }
                catch (InvalidOperationException) { /* The process exited while being inspected. */ }
                catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
                {
                    if (IsKnownApplicationName(name)) blockers.Add(new(process.Id, name, null, true));
                }
            }
        }
        return blockers;
    }

    private static bool IsApplicationEntryName(string name) => name.Equals("顺手工具箱", StringComparison.OrdinalIgnoreCase) || name.Equals("Shunshou.App", StringComparison.OrdinalIgnoreCase);
    private static bool IsKnownApplicationName(string name) => IsApplicationEntryName(name) || name.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase) || name.Equals("ffprobe", StringComparison.OrdinalIgnoreCase);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, nint security, FileMode mode, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
}
