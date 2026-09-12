using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shunshou.Core;

public enum RecoveryMode { Combined, RecycleBin, FileRecords, DeepScan, BackupFolder }
public sealed record RecoveryRequest(string Source, string OutputDirectory, RecoveryMode Mode);
public sealed record RecoveryProgress(string Message, int Found);
public sealed record RecoveryCandidate(string Name, string StoredPath, string? OriginalPath, long Length,
    string Origin, DateTime? DeletedAt = null)
{
    public string Extension => Path.GetExtension(Name).TrimStart('.').ToLowerInvariant();
    public string Detail => $"{Origin} · {Length / 1024d:N1} KB" + (DeletedAt is { } time ? $" · 删除于 {time:g}" : "");
}
public sealed record RecoveryScan(IReadOnlyList<RecoveryCandidate> Files, string? SessionDirectory,
    bool Cancelled, string Message);

/// <summary>Independent read/copy recovery routes. No repair commands or source deletion are exposed.</summary>
public sealed class RecoveryService
{
    private readonly string _engineDirectory;
    public RecoveryService(string? engineDirectory = null) => _engineDirectory = engineDirectory ??
        Path.Combine(AppContext.BaseDirectory, "tools", "recovery", "bin");
    public bool HasDiskEngines => File.Exists(Path.Combine(_engineDirectory, "photorec_win.exe"));
    public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static bool IsDrive(string source) => Regex.IsMatch(source, @"^[A-Za-z]:[\\/]?$", RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Drives() => DriveInfo.GetDrives()
        .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable)
        .Where(d => { try { return d.IsReady; } catch { return false; } }).Select(d => d.RootDirectory.FullName).ToArray();

    public static string ValidateDestination(string source, string destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) throw new InvalidOperationException("请先选择恢复文件的保存位置。");
        var target = Path.GetFullPath(destination);
        if (target.StartsWith(@"\\", StringComparison.Ordinal)) throw new InvalidOperationException("请选择本机磁盘或移动硬盘上的保存位置。");
        RejectReparseAncestors(target);
        var sourcePath = Path.GetFullPath(source);
        // A disk image is an ordinary immutable source file: sibling output is safe. A live source volume is not.
        if (IsDrive(source) && string.Equals(RecoveryVolumeSafety.VolumeIdentity(sourcePath),
                RecoveryVolumeSafety.VolumeIdentity(target), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("恢复文件不能保存到正在恢复的磁盘。请选择另一个磁盘，建议使用移动硬盘。");
        if (IsWithin(sourcePath, target) || string.Equals(sourcePath, target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("保存位置不能位于源文件或备份文件夹内部。");
        return target;
    }

    public async Task<RecoveryScan> ScanAsync(RecoveryRequest request, IProgress<RecoveryProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Mode == RecoveryMode.Combined)
        {
            ValidateDestination(request.Source, request.OutputDirectory);
            var found = new List<RecoveryCandidate>();
            var notes = new List<string>();
            string? session = null;
            var modes = IsDrive(request.Source) ? new[] { RecoveryMode.RecycleBin, RecoveryMode.FileRecords, RecoveryMode.DeepScan } :
                new[] { RecoveryMode.FileRecords, RecoveryMode.DeepScan };
            foreach (var mode in modes)
            {
                if (ct.IsCancellationRequested) return new(found, session, true, $"已停止，保留 {found.Count:N0} 个候选文件。");
                try
                {
                    var scan = await ScanAsync(request with { Mode = mode }, progress, ct);
                    found.AddRange(scan.Files); session = scan.SessionDirectory ?? session;
                    if (scan.Cancelled) return new(found, session, true, $"已停止，保留 {found.Count:N0} 个候选文件。");
                }
                catch (OperationCanceledException) { return new(found, session, true, $"已停止，保留 {found.Count:N0} 个候选文件。"); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                { notes.Add(ex.Message); }
            }
            progress?.Report(new("正在合并相同内容的扫描结果…", found.Count));
            var merged = await MergeAnonymousDuplicatesAsync(found, ct);
            return new(merged, session, ct.IsCancellationRequested, $"综合查找结束，共 {merged.Count:N0} 个候选文件。" +
                (notes.Count > 0 ? "部分方式未完成：" + string.Join("；", notes) : "请预览检查内容。"));
        }
        if (request.Mode == RecoveryMode.RecycleBin)
        {
            if (!IsDrive(request.Source)) throw new InvalidOperationException("回收站查找需要选择一个磁盘。");
            string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("无法读取当前用户。");
            string bin = Path.Combine(Path.GetPathRoot(Path.GetFullPath(request.Source))!, "$Recycle.Bin", sid);
            return await Task.Run(() => ScanRecycleDirectory(bin, progress, ct), ct);
        }
        if (request.Mode == RecoveryMode.BackupFolder)
        {
            if (!Directory.Exists(request.Source) || IsDrive(request.Source)) throw new InvalidOperationException("请选择已有备份中的文件夹。");
            RejectReparseAncestors(request.Source);
            return await Task.Run(() =>
            {
                var files = EnumerateFiles(request.Source, "已有备份", ct, progress);
                return new RecoveryScan(files, null, ct.IsCancellationRequested, ct.IsCancellationRequested
                    ? $"已停止，保留 {files.Count:N0} 个已找到的文件。" : "已读取已有备份中的文件。");
            }, ct);
        }
        return await ScanDiskAsync(request, progress, ct);
    }

    public static RecoveryScan ScanRecycleDirectory(string directory, IProgress<RecoveryProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = new List<RecoveryCandidate>();
        if (!Directory.Exists(directory)) return new(result, null, false, "这个磁盘的回收站中没有找到文件。");
        RejectReparseAncestors(directory);
        foreach (string metadata in Directory.EnumerateFiles(directory, "$I*", SearchOption.TopDirectoryOnly))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                if ((File.GetAttributes(metadata) & FileAttributes.ReparsePoint) != 0 || new FileInfo(metadata).Length > 131072) continue;
                var data = File.ReadAllBytes(metadata);
                if (data.Length < 28) continue;
                long version = BinaryPrimitives.ReadInt64LittleEndian(data);
                int offset, length;
                if (version == 1) { offset = 24; length = Math.Min(520, data.Length - offset); }
                else if (version == 2)
                {
                    offset = 28;
                    int characters = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(24));
                    if (characters < 1 || characters > 32768 || (long)characters * 2 > data.Length - offset) continue;
                    length = characters * 2;
                }
                else continue;
                string original = Encoding.Unicode.GetString(data, offset, length).TrimEnd('\0');
                if (string.IsNullOrWhiteSpace(original) || original.Contains('\0')) continue;
                string stored = Path.Combine(directory, "$R" + Path.GetFileName(metadata)[2..]);
                if ((!File.Exists(stored) && !Directory.Exists(stored)) || (File.GetAttributes(stored) & FileAttributes.ReparsePoint) != 0) continue;
                DateTime? deleted = null;
                try { deleted = DateTime.FromFileTimeUtc(BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(16))).ToLocalTime(); }
                catch (ArgumentOutOfRangeException) { }
                if (Directory.Exists(stored))
                {
                    foreach (var nested in EnumerateFiles(stored, "回收站", ct, progress, result.Count))
                        result.Add(nested with { OriginalPath = Path.Combine(original, Path.GetRelativePath(stored, nested.StoredPath)), DeletedAt = deleted });
                }
                else result.Add(new(Path.GetFileName(original), stored, original, new FileInfo(stored).Length, "回收站", deleted));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            if (result.Count == 1 || result.Count > 0 && result.Count % 100 == 0)
                progress?.Report(new("正在查找回收站…", result.Count));
        }
        return new(result, null, ct.IsCancellationRequested, ct.IsCancellationRequested
            ? $"已停止，保留 {result.Count:N0} 个已找到的文件。"
            : $"找到 {result.Count:N0} 个文件。选中后可复制恢复，回收站中的原文件保留。");
    }

    public static IReadOnlyList<RecoveryCandidate> Filter(IEnumerable<RecoveryCandidate> files, string name, string extensions)
    {
        name = name.Trim();
        var types = extensions.Split([',', '，', ';', '；', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim().TrimStart('*', '.').ToLowerInvariant()).Where(e => e.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return files.Where(f => (name.Length == 0 || f.Name.Contains(name, StringComparison.OrdinalIgnoreCase) ||
            (f.OriginalPath?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false)) &&
            (types.Count == 0 || types.Contains(f.Extension))).ToArray();
    }

    private async Task<RecoveryScan> ScanDiskAsync(RecoveryRequest request, IProgress<RecoveryProgress>? progress, CancellationToken ct)
    {
        if (request.Mode == RecoveryMode.DeepScan && !HasDiskEngines)
            throw new InvalidOperationException("恢复组件不完整，请重新解压完整软件包。");
        bool drive = IsDrive(request.Source);
        string source = drive ? @"\\.\" + request.Source[..2].ToUpperInvariant() : Path.GetFullPath(request.Source);
        if (!drive && !File.Exists(source)) throw new FileNotFoundException("没有找到磁盘镜像。", source);
        if (!drive) RejectReparseAncestors(source);
        if (drive && !IsAdministrator) throw new UnauthorizedAccessException("扫描删除记录需要管理员权限。");
        // Opening for read alone validates access; no source handle with write access is created by our adapter.
        byte[] boot = new byte[drive ? 4096 : 512];
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1))
            input.ReadExactly(boot);
        bool ntfs = Encoding.ASCII.GetString(boot, 3, 8) == "NTFS    ";
        if (request.Mode == RecoveryMode.FileRecords && !ntfs)
            throw new InvalidOperationException("没有找到可读取的 NTFS 删除记录。可使用深度扫描按文件内容查找。");
        string target = ValidateDestination(request.Source, request.OutputDirectory);
        Directory.CreateDirectory(target);
        string session = Path.Combine(target, "Recovery_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(session);
        string candidates = Path.Combine(session, "candidates");
        Directory.CreateDirectory(candidates);
        if (request.Mode == RecoveryMode.FileRecords)
        {
            var recovered = await new NtfsDeletedFileReader().RecoverAsync(source, candidates, progress, ct);
            await WriteSessionAsync(session, request, recovered.Files, recovered.Cancelled, 0, recovered.SkippedRecords);
            return new(recovered.Files, session, recovered.Cancelled, recovered.Message);
        }
        string log = Path.Combine(session, "engine.log");
        var start = new ProcessStartInfo(Path.Combine(_engineDirectory, "photorec_win.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = candidates,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        start.Environment["__COMPAT_LAYER"] = "RunAsInvoker";
        start.Environment["LANG"] = "en_US.UTF-8";
        start.Environment["LC_ALL"] = "en_US.UTF-8";
        start.Environment["TERMINFO"] = _engineDirectory;
        // Fixed short relative paths avoid native path-buffer truncation. CWD is a fresh, validated
        // session directory; configs, logs and temporary files cannot fall back to the user's profile.
        start.Environment["HOME"] = "..";
        start.Environment["USERPROFILE"] = "..";
        start.Environment["HOMEPATH"] = "..";
        start.Environment["HOMEDRIVE"] = Path.GetPathRoot(session)!.TrimEnd('\\');
        start.Environment["TMP"] = "..";
        start.Environment["TEMP"] = "..";
        start.Environment["TMPDIR"] = "..";
        start.ArgumentList.Add("/logname"); start.ArgumentList.Add("../engine.log"); start.ArgumentList.Add("/log");
        start.ArgumentList.Add("/d"); start.ArgumentList.Add("files");
        start.ArgumentList.Add("/cmd"); start.ArgumentList.Add(source);
        // Only a fixed read/recover command allowlist is exposed. Never pass arbitrary command text or repair operations.
        start.ArgumentList.Add("partition_none,fileopt,custom,disable,options,paranoid,keep_corrupted_file_no,wholespace,search");
        ct.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("无法启动恢复组件。");
        process.StandardInput.Close();
        var stdout = DrainAsync(process.StandardOutput);
        var stderr = DrainAsync(process.StandardError);
        bool cancelled = false;
        string? stopReason = null;
        try
        {
            while (!process.HasExited)
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }
                var disk = new DriveInfo(Path.GetPathRoot(target)!);
                if (disk.AvailableFreeSpace < 256L * 1024 * 1024) { stopReason = "保存磁盘空间不足，扫描已停止，已找到的文件保留。"; break; }
                progress?.Report(new("正在深度扫描，候选文件正保存到所选位置…", 0));
                await Task.Delay(500, CancellationToken.None);
            }
        }
        finally
        {
            if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
            await process.WaitForExitAsync(CancellationToken.None);
        }
        string error = await stderr; await stdout;
        cancelled |= ct.IsCancellationRequested;
        var files = EnumerateFiles(candidates, "深度扫描", CancellationToken.None);
        string diagnostic = File.Exists(log) ? await ReadDiagnosticAsync(log) : error;
        bool engineFailed = process.ExitCode != 0 || Regex.IsMatch(diagnostic,
            @"(?m)^\s*(Syntax error|Support for this filesystem hasn't been implemented|Unable to open file or device)", RegexOptions.CultureInvariant);
        await WriteSessionAsync(session, request, files, cancelled, process.ExitCode, 0);
        if (!cancelled && stopReason == null && engineFailed && files.Count == 0)
            throw new IOException($"恢复扫描未完成（代码 {process.ExitCode}）。诊断记录保存在 {session}。" + (error.Length > 0 ? "请检查磁盘是否可读。" : ""));
        return new(files, session, cancelled, stopReason ?? (cancelled ? $"已停止，保留 {files.Count:N0} 个候选文件。" :
            engineFailed ? $"扫描未完全完成，已保存 {files.Count:N0} 个候选文件。请检查诊断记录和文件内容。" :
            $"扫描结束，保存了 {files.Count:N0} 个候选文件，请预览检查内容。"));
    }

    private static Task WriteSessionAsync(string session, RecoveryRequest request,
        IReadOnlyList<RecoveryCandidate> files, bool cancelled, int exitCode, int skippedRecords) =>
        File.WriteAllTextAsync(Path.Combine(session, "session.json"), JsonSerializer.Serialize(new
        {
            Version = 1, request.Source, request.Mode, Completed = DateTimeOffset.Now, Cancelled = cancelled,
            ExitCode = exitCode, SkippedRecords = skippedRecords,
            Note = "Candidates are recovered copies. Decoding a preview does not prove complete content integrity.",
            Files = files.Select(f => new { f.Name, f.OriginalPath, RelativePath = Path.GetRelativePath(session, f.StoredPath), f.Length, f.Origin })
        }, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        var tail = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            tail.Append(buffer, 0, read);
            if (tail.Length > 16000) tail.Remove(0, tail.Length - 16000);
        }
        return tail.ToString();
    }

    private static async Task<string> ReadDiagnosticAsync(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        var buffer = new char[65536];
        int count = await reader.ReadBlockAsync(buffer);
        return new string(buffer, 0, count);
    }

    /// <summary>Retain every named file/version; hide only byte-identical anonymous copies from the deep route.
    /// All original candidate files remain on disk. A failed comparison preserves both results.</summary>
    public static async Task<IReadOnlyList<RecoveryCandidate>> MergeAnonymousDuplicatesAsync(
        IReadOnlyList<RecoveryCandidate> files, CancellationToken ct)
    {
        var namedBySize = files.Where(f => f.Origin != "深度扫描").GroupBy(f => f.Length).ToDictionary(g => g.Key, g => g.ToArray());
        var hashes = new Dictionary<long, HashSet<string>>();
        var result = new List<RecoveryCandidate>();
        try
        {
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (file.Origin != "深度扫描" || !namedBySize.TryGetValue(file.Length, out var named)) { result.Add(file); continue; }
                if (!hashes.TryGetValue(file.Length, out var known))
                {
                    known = []; hashes[file.Length] = known;
                    foreach (var candidate in named)
                        if (await HashCandidateAsync(candidate.StoredPath, ct) is { } digest) known.Add(digest);
                }
                string? hash = await HashCandidateAsync(file.StoredPath, ct);
                if (hash == null || !known.Contains(hash)) result.Add(file);
            }
            return result;
        }
        catch (OperationCanceledException) { return files; }
    }

    private static async Task<string?> HashCandidateAsync(string path, CancellationToken ct)
    {
        try
        {
            RejectReparseAncestors(path);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            return Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static IReadOnlyList<RecoveryCandidate> EnumerateFiles(string root, string origin, CancellationToken ct,
        IProgress<RecoveryProgress>? progress = null, int alreadyFound = 0)
    {
        var result = new List<RecoveryCandidate>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 100 };
        foreach (string file in Directory.EnumerateFiles(root, "*", options))
        {
            // Return collected files when cancelled, including files inside a deleted folder.
            // The route reports cancellation separately so the UI can keep these candidates.
            if (ct.IsCancellationRequested) break;
            string name = Path.GetFileName(file);
            if (origin == "深度扫描" && name is "report.xml" or "photorec.ses" or "photorec.log") continue;
            try { result.Add(new(name, file, origin == "已有备份" ? Path.GetRelativePath(root, file) : null, new FileInfo(file).Length, origin)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (result.Count == 1 || result.Count > 0 && result.Count % 100 == 0)
                progress?.Report(new($"正在查找{origin}…", alreadyFound + result.Count));
        }
        return result;
    }

    public static async Task<IReadOnlyList<string>> CopySelectedAsync(IEnumerable<RecoveryCandidate> selection,
        string source, string destination, CancellationToken ct)
    {
        string target = ValidateDestination(source, destination);
        Directory.CreateDirectory(target);
        var copied = new List<string>();
        foreach (var file in selection)
        {
            ct.ThrowIfCancellationRequested();
            RejectReparseAncestors(file.StoredPath);
            string name = SafeName(file.Name);
            string output = Path.Combine(target, name);
            for (int number = 2; File.Exists(output) || Directory.Exists(output); number++)
                output = Path.Combine(target, Path.GetFileNameWithoutExtension(name) + "_" + number + Path.GetExtension(name));
            RejectReparseAncestors(target);
            bool created = false;
            try
            {
                await using var input = new FileStream(file.StoredPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                await using var result = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                created = true;
                await input.CopyToAsync(result, ct);
                if (result.Length != input.Length) throw new IOException("复制长度不一致。");
            }
            catch { if (created) { try { File.Delete(output); } catch (IOException) { } } throw; }
            copied.Add(output);
        }
        return copied;
    }

    private static string SafeName(string name)
    {
        name = Path.GetFileName(name);
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        name = name.TrimEnd(' ', '.');
        if (name.Length > 220)
        {
            string extension = Path.GetExtension(name);
            if (extension.Length > 24) extension = extension[..24];
            name = Path.GetFileNameWithoutExtension(name)[..Math.Min(190, Path.GetFileNameWithoutExtension(name).Length)] + extension;
        }
        if (name.Length == 0 || Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)) name = "Recovered_" + name;
        return name;
    }

    private static bool IsWithin(string parent, string child) => child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static void RejectReparseAncestors(string path)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("恢复路径不能经过符号链接或目录联接，请选择实际文件夹。");
            current = Path.GetDirectoryName(current);
        }
    }
}
