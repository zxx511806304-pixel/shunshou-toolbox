using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Shunshou.Core;

/// <summary>
/// Extraction for archives the built-in ZIP reader cannot open (.7z, .rar and others) using the pinned
/// 7-Zip engine that ships with the app. The archive is validated before extraction and the extracted
/// tree is validated again afterwards, so the safety rules match the ZIP path. Creating .7z or .rar is
/// not offered: the bundled unRAR code must not be used to build a RAR compressor.
/// </summary>
public sealed class SevenZipService(string? engineDirectory = null)
{
    private const long MaximumExpandedBytes = 5L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 20_000;
    private static readonly string TemporaryRoot = Path.Combine(Path.GetTempPath(), "Shunshou", "archive-jobs");

    public static readonly string[] SupportedExtensions = [".7z", ".rar", ".zip", ".tar", ".cab"];

    public Task<string> ExtractAsync(string inputPath, string outputDirectory,
        IProgress<ToolProgress>? progress, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        string input = Path.GetFullPath(inputPath);
        if (!File.Exists(input)) throw new FileNotFoundException("压缩包不存在。", input);
        if (File.GetAttributes(input).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("不能读取链接形式的压缩包。");
        string extension = Path.GetExtension(input).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
            throw new NotSupportedException("该工具支持解压 " + string.Join("、", SupportedExtensions) + "；ZIP 可以继续使用“ZIP 解压”。");
        string engine = ResolveEngine();
        var entries = ListEntries(engine, input, ct);
        if (entries.Count == 0) throw new InvalidDataException("压缩包是空的。");
        string job = Path.Combine(TemporaryRoot, Guid.NewGuid().ToString("N"));
        string extracted = Path.Combine(job, "extracted");
        Directory.CreateDirectory(extracted);
        try
        {
            progress?.Report(new(10, $"正在解压 {entries.Count} 个条目"));
            Extract(engine, input, extracted, ct);
            var files = VerifyTree(extracted, ct);
            if (files == 0) throw new InvalidDataException("压缩包中没有可恢复的文件。");
            Directory.CreateDirectory(Path.GetFullPath(outputDirectory));
            string output = NewOutputDirectory(outputDirectory, Path.GetFileNameWithoutExtension(input) + "_extracted");
            CopyTree(extracted, output, ct);
            progress?.Report(new(100, $"解压完成 · {files} 个文件，原压缩包已保留"));
            return output;
        }
        finally
        {
            var resolved = Path.GetFullPath(job);
            if (Path.GetDirectoryName(resolved) == Path.GetFullPath(TemporaryRoot) && Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
            {
                try { if (Directory.Exists(resolved)) Directory.Delete(resolved, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }, ct);

    private string ResolveEngine()
    {
        var directory = engineDirectory ?? Environment.GetEnvironmentVariable("SHUNSHOU_SEVENZIP_DIR") ??
            Path.Combine(AppContext.BaseDirectory, "tools", "sevenzip");
        if (!File.Exists(Path.Combine(directory, "7z.exe")) && engineDirectory is null && Environment.GetEnvironmentVariable("SHUNSHOU_SEVENZIP_DIR") is null)
        {
            for (var parent = new DirectoryInfo(AppContext.BaseDirectory); parent is not null; parent = parent.Parent)
            {
                var developerDirectory = Path.Combine(parent.FullName, "runtime", "sevenzip");
                if (!File.Exists(Path.Combine(developerDirectory, "7z.exe"))) continue;
                directory = developerDirectory;
                break;
            }
        }
        string engine = Path.Combine(Path.GetFullPath(directory), "7z.exe");
        if (!File.Exists(engine))
            throw new FileNotFoundException("缺少离线 7z 引擎。请完整解压软件，确保 tools/sevenzip 内有 7z.exe 与 7z.dll。");
        return engine;
    }

    private sealed record Entry(string Path, long Size, bool Directory, bool Link);

    /// <summary>Reads the archive listing before anything is written, so unsafe archives are refused up front.</summary>
    private static IReadOnlyList<Entry> ListEntries(string engine, string input, CancellationToken ct)
    {
        string listing = Run(engine, ["l", "-slt", "-sccUTF-8", "-bd", "-y", "--", input], ct);
        var entries = new List<Entry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long declared = 0;
        string? path = null;
        long size = 0;
        string type = "";
        string attributes = "";
        bool folder = false;
        bool link = false;
        foreach (string raw in listing.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                Flush();
                continue;
            }
            int separator = line.IndexOf(" = ", StringComparison.Ordinal);
            if (separator <= 0) continue;
            string key = line[..separator];
            string value = line[(separator + 3)..];
            switch (key)
            {
                case "Path": path = value; break;
                case "Size": long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out size); break;
                case "Type": type = value; break;
                case "Attributes": attributes = value; break;
                case "Folder": folder = value.Trim() == "+"; break;
                // 7-Zip spells these with a space in the -slt listing; other builds use the compact names.
                case "Symbolic Link" or "SymbolicLink": link = !string.IsNullOrWhiteSpace(value); break;
                case "Hard Link" or "HardLink": link = link || !string.IsNullOrWhiteSpace(value); break;
                case "Copy Link" or "CopyLink": link = link || !string.IsNullOrWhiteSpace(value); break;
            }
        }
        Flush();
        return entries;

        void Flush()
        {
            if (path is null) return;
            string current = path;
            long currentSize = size;
            string currentType = type;
            string currentAttributes = attributes;
            bool currentFolder = folder;
            bool currentLink = link;
            path = null; size = 0; type = ""; attributes = ""; folder = false; link = false;
            // 7-Zip's -slt output marks folders with "Attributes = D" (and sometimes "Folder = +")
            // rather than a Type line, so both signals are honoured here.
            bool directory = currentFolder || currentAttributes.Contains('D');
            // "AL" marks a symlink entry in the attributes column of RAR5 archives.
            if (currentAttributes.Contains('L')) currentLink = true;
            if (currentType is not ("File" or "Directory") && !directory && currentAttributes.Length == 0) return;
            SafeEntryPath(current);
            if (!names.Add(current.Replace('\\', '/').TrimEnd('/')))
                throw new InvalidDataException("压缩包包含重复或大小写冲突的路径：" + current);
            if (currentLink) throw new InvalidDataException("压缩包包含符号链接或硬链接，已停止解压：" + current);
            if (entries.Count >= MaximumEntries) throw new InvalidDataException("压缩包包含超过 20,000 个条目。");
            declared = checked(declared + currentSize);
            if (declared > MaximumExpandedBytes) throw new InvalidDataException("压缩包解开后超过 5 GB，已停止处理。");
            entries.Add(new(current, currentSize, directory || currentType == "Directory", false));
        }
    }

    private static void Extract(string engine, string input, string destination, CancellationToken ct)
    {
        Run(engine, ["x", "-sccUTF-8", "-bd", "-y", "-o" + destination, "--", input], ct);
    }

    /// <summary>Confirms the extracted tree only contains plain files inside the destination.</summary>
    private static int VerifyTree(string root, CancellationToken ct)
    {
        string full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        int files = 0;
        long bytes = 0;
        var pending = new Stack<string>();
        pending.Push(full);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("解压结果包含链接，已停止并保留原压缩包：" + Path.GetFileName(entry));
                if (!Path.GetFullPath(entry).StartsWith(full, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("解压结果路径越界，已停止处理。");
                if (attributes.HasFlag(FileAttributes.Directory)) { pending.Push(entry); continue; }
                files++;
                bytes = checked(bytes + new FileInfo(entry).Length);
                if (files > MaximumEntries) throw new InvalidDataException("解压结果超过 20,000 个文件，已停止处理。");
                if (bytes > MaximumExpandedBytes) throw new InvalidDataException("解压结果超过 5 GB，已停止处理。");
            }
        }
        return files;
    }

    private static void CopyTree(string source, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        foreach (string entry in Directory.EnumerateFileSystemEntries(source))
        {
            ct.ThrowIfCancellationRequested();
            string name = Path.GetFileName(entry);
            string target = Path.Combine(destination, name);
            if (Directory.Exists(entry)) CopyTree(entry, target, ct);
            else File.Move(entry, target, false);
        }
    }

    private static string NewOutputDirectory(string parent, string name)
    {
        parent = Path.GetFullPath(parent);
        string candidate = Path.Combine(parent, name);
        for (int suffix = 2; Directory.Exists(candidate) || File.Exists(candidate); suffix++)
            candidate = Path.Combine(parent, $"{name} ({suffix})");
        Directory.CreateDirectory(candidate);
        return candidate;
    }

    /// <summary>Same rules the ZIP path enforces: relative segments only, no device names, no drive colons.</summary>
    private static void SafeEntryPath(string entryName)
    {
        string normalized = entryName.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') || normalized.Contains(':') || normalized.Contains('\0'))
            throw new InvalidDataException("压缩包包含不安全路径。");
        foreach (string segment in normalized.TrimEnd('/').Split('/'))
        {
            string device = segment.Split('.')[0];
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                device.Equals("CON", StringComparison.OrdinalIgnoreCase) || device.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                device.Equals("AUX", StringComparison.OrdinalIgnoreCase) || device.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                (device.Length == 4 && (device.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || device.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && char.IsDigit(device[3])))
                throw new InvalidDataException("压缩包包含 Windows 不支持或不安全的文件名：" + entryName);
        }
    }

    private static string Run(string executable, IEnumerable<string> arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动本地 7z 引擎。");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            process.WaitForExit();
            Task.WaitAll([stdout, stderr], ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            string detail = (stderr.Result + "\n" + stdout.Result).Trim();
            if (detail.Length > 1200) detail = detail[..1200];
            throw new InvalidDataException("7z 引擎处理失败，请确认压缩包完整、未加密。\n" + detail);
        }
        return stdout.Result;
    }
}
