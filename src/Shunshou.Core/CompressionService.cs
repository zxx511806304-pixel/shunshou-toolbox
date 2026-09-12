using System.IO.Compression;
using ImageMagick;

namespace Shunshou.Core;

public sealed record CompressionResult(string OutputPath, long OriginalBytes, long OutputBytes,
    int FileCount, bool ReachedTarget, string Message);

/// <summary>Local ZIP processing. Inputs are never overwritten and every image trial starts from the input.</summary>
public sealed class CompressionService
{
    private const long MaximumExpandedBytes = 5L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 20_000;
    private const double TargetMargin = 0.975;
    private static readonly string TempParent = Path.Combine(Path.GetTempPath(), "Shunshou", "archive-jobs");
    private sealed record Entry(string Name, string? Source, long Length, DateTimeOffset Modified)
    {
        public bool IsDirectory => Source is null;
    }

    public Task<CompressionResult> CompressAsync(string inputPath, string outputDirectory, long maxBytes,
        bool allowLossy, bool allowResize, IProgress<ToolProgress>? progress, CancellationToken ct) =>
        Task.Run(() => Compress(inputPath, outputDirectory, maxBytes, allowLossy, allowResize, progress, ct), ct);

    public Task<string> CreateZipAsync(string inputPath, string outputDirectory,
        IProgress<ToolProgress>? progress, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var entries = SnapshotFolderOrFile(inputPath, ct);
        var job = CreateJob();
        try
        {
            var temporaryZip = Path.Combine(job, "result.zip");
            WriteZip(entries, temporaryZip, null, progress, 0, 90, ct);
            ValidateResult(temporaryZip, entries, ct);
            var output = PublishFile(temporaryZip, outputDirectory, GetStem(inputPath) + "_packed", ".zip");
            progress?.Report(new(100, "打包完成，原文件已保留"));
            return output;
        }
        finally { DeleteJob(job); }
    }, ct);

    public Task<string> ExtractZipAsync(string inputPath, string outputDirectory,
        IProgress<ToolProgress>? progress, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var job = CreateJob();
        try
        {
            var extracted = Path.Combine(job, "extracted");
            ExtractChecked(inputPath, extracted, progress, ct);
            Directory.CreateDirectory(Path.GetFullPath(outputDirectory));
            var output = NewOutputDirectory(outputDirectory, GetStem(inputPath) + "_extracted");
            // Copy from a validated temporary tree. This also works across disk volumes.
            foreach (var entry in SnapshotFolderOrFile(extracted, ct))
            {
                ct.ThrowIfCancellationRequested();
                var target = SafeEntryPath(output, entry.Name);
                if (entry.IsDirectory) Directory.CreateDirectory(target);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var source = File.OpenRead(entry.Source!);
                    using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                    CopyChecked(source, destination, MaximumExpandedBytes, ct);
                }
            }
            progress?.Report(new(100, "解压完成，原压缩包已保留"));
            return output;
        }
        finally { DeleteJob(job); }
    }, ct);

    private static CompressionResult Compress(string inputPath, string outputDirectory, long maxBytes,
        bool allowLossy, bool allowResize, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        if (maxBytes < 1024) throw new ArgumentOutOfRangeException(nameof(maxBytes), "目标大小不能小于 1 KB。");
        if (allowResize && !allowLossy)
            throw new ArgumentException("缩小像素尺寸会损失图像信息，需要同时允许有损处理。");
        ct.ThrowIfCancellationRequested();
        var job = CreateJob();
        try
        {
            inputPath = Path.GetFullPath(inputPath);
            var isZip = File.Exists(inputPath);
            IReadOnlyList<Entry> entries;
            long originalBytes;
            if (isZip)
            {
                if (!Path.GetExtension(inputPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("按上传上限压缩目前接受 ZIP 或文件夹。其他压缩包请先解压。");
                originalBytes = new FileInfo(inputPath).Length;
                entries = ExtractChecked(inputPath, Path.Combine(job, "source"), progress, ct);
            }
            else
            {
                entries = SnapshotFolderOrFile(inputPath, ct);
                originalBytes = entries.Sum(e => e.Length);
            }

            var fileCount = entries.Count(e => !e.IsDirectory);
            if (fileCount == 0) throw new InvalidDataException("输入里没有文件，请选择包含文件的 ZIP 或文件夹。");
            var target = (long)Math.Floor(maxBytes * TargetMargin);
            var candidateZip = Path.Combine(job, "candidate.zip");
            var bestZip = Path.Combine(job, "best.zip");
            var candidateDirectory = Path.Combine(job, "images");
            var losslessDirectory = Path.Combine(job, "lossless");
            Directory.CreateDirectory(candidateDirectory);
            Directory.CreateDirectory(losslessDirectory);

            WriteZip(entries, candidateZip, null, progress, 6, 8, ct);
            File.Copy(candidateZip, bestZip, true);
            long bestBytes = new FileInfo(bestZip).Length;
            var bestMode = "无损打包，所有文件内容保持不变";
            // The original archive may already have a more efficient compression method.
            if (isZip && originalBytes < bestBytes)
            {
                File.Copy(inputPath, bestZip, true);
                bestBytes = originalBytes;
            }

            var warnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lossless = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var compressible = entries.Where(e => !e.IsDirectory && IsImage(e.Name)).ToArray();
            if (bestBytes > target)
            {
                for (var index = 0; index < compressible.Length; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = compressible[index];
                    var output = Path.Combine(losslessDirectory, index + Path.GetExtension(entry.Name));
                    try
                    {
                        if (OptimizeLosslessly(entry.Source!, output, ct)) lossless[entry.Name] = output;
                    }
                    catch (MagickException) { warnings.Add(entry.Name); }
                    catch (InvalidDataException) { warnings.Add(entry.Name); }
                    progress?.Report(new(14 + 12d * (index + 1) / Math.Max(1, compressible.Length),
                        $"无损优化 {index + 1}/{compressible.Length}：{Path.GetFileName(entry.Name)}"));
                }
                WriteZip(entries, candidateZip, lossless, progress, 26, 4, ct);
                ConsiderCandidate("无损优化，像素尺寸与画质保持不变");
            }

            if (allowLossy && bestBytes > target && compressible.Length > 0)
            {
                ct.ThrowIfCancellationRequested();
                // candidate.zip is the completed baseline with lossless replacements. Measuring
                // entry payloads in that ZIP lets the allocator reserve every non-image byte and
                // all ZIP headers before distributing the image budget.
                Dictionary<string, long> payloadBytes;
                using (var baseline = ZipFile.OpenRead(candidateZip))
                    payloadBytes = baseline.Entries.ToDictionary(e => e.FullName.Replace('\\', '/'),
                        e => e.CompressedLength, StringComparer.OrdinalIgnoreCase);
                var inputImages = compressible.Select(entry => new BudgetImage(entry.Name, entry.Source!,
                    lossless.GetValueOrDefault(entry.Name, entry.Source!), payloadBytes[entry.Name])).ToArray();
                var allocation = AdaptiveImageBudget.Optimize(inputImages, new FileInfo(candidateZip).Length,
                    target, allowResize, candidateDirectory, progress, ct);
                foreach (var name in allocation.SkippedImages) warnings.Add(name);
                WriteZip(entries, candidateZip, allocation.Paths, null, 0, 0, ct);
                ConsiderCandidate(allocation.Description);
                // The final ZIP remains authoritative, even if an encoder or archive layout makes
                // the byte estimate imperfect. An over-limit result is never reported as success.
                if (new FileInfo(candidateZip).Length > target && bestBytes >= maxBytes)
                {
                    progress?.Report(new(93, "当前授权范围内仍未达标，将保留全部文件并报告实际大小"));
                }
            }

            ct.ThrowIfCancellationRequested();
            progress?.Report(new(94, "核对输出文件数、目录与实际压缩包大小"));
            ValidateResult(bestZip, entries, ct);
            var reached = bestBytes < maxBytes;
            var published = PublishFile(bestZip, outputDirectory,
                GetStem(inputPath) + (reached ? "_upload" : "_compressed_over_limit"), ".zip");
            bestBytes = new FileInfo(published).Length;
            reached = bestBytes < maxBytes;
            var message = reached
                ? $"已达标：{bestBytes / 1_000_000d:F2} MB，小于 {maxBytes / 1_000_000d:F2} MB 上限，" +
                    (bestBytes <= target ? "并预留余量。" : "但未留足预设的 2.5% 余量。") + $"{bestMode}。"
                : $"未达目标：当前可得 {bestBytes / 1_000_000d:F2} MB，上传上限为 {maxBytes / 1_000_000d:F2} MB，还需减少至少 {Math.Max(1, bestBytes - maxBytes + 1):N0} 字节。{bestMode}。全部 {fileCount} 个文件已保留；" +
                    (allowResize ? "已用尽本版本的保守压缩范围，可提高目标大小。" : allowLossy
                        ? "可提高目标大小，或在确认画质损失后允许缩小图片尺寸。" : "可提高目标大小，或明确允许有损处理后重试。");
            if (warnings.Count > 0) message += $" 有 {warnings.Count} 张无法安全优化的图片保留原文件。";
            if (compressible.Length == 0) message += " 未发现本版本可优化的 JPG、PNG 或 WebP 图片。";
            progress?.Report(new(100, reached ? "压缩完成，已检查最终 ZIP 大小" : "处理完成，未达到目标大小"));
            return new(published, originalBytes, bestBytes, fileCount, reached, message);

            void ConsiderCandidate(string mode)
            {
                var length = new FileInfo(candidateZip).Length;
                if (length >= bestBytes) return;
                File.Copy(candidateZip, bestZip, true);
                bestBytes = length;
                bestMode = mode;
            }
        }
        finally { DeleteJob(job); }
    }

    private static bool IsImage(string name) => Path.GetExtension(name).ToLowerInvariant() is
        ".jpg" or ".jpeg" or ".jfif" or ".png" or ".webp";

    private static bool OptimizeLosslessly(string source, string destination, CancellationToken ct)
    {
        // The Q8 build must not silently reduce a 16-bit PNG, animation, or multipage image.
        using var probe = new MagickImageCollection();
        probe.Ping(source, new MagickReadSettings { Format = FormatOf(source) });
        if (probe.Count != 1 || probe[0].Depth > 8 || (ulong)probe[0].Width * probe[0].Height > 100_000_000) return false;
        ct.ThrowIfCancellationRequested();
        if (FormatOf(source) == MagickFormat.WebP) return false;
        File.Copy(source, destination, true);
        var optimizer = new ImageOptimizer();
        optimizer.LosslessCompress(new FileInfo(destination));
        ct.ThrowIfCancellationRequested();
        return new FileInfo(destination).Length < new FileInfo(source).Length;
    }

    internal static MagickFormat FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".jfif" => MagickFormat.Jpeg,
        ".png" => MagickFormat.Png,
        ".webp" => MagickFormat.WebP,
        _ => throw new NotSupportedException("当前文件不是支持压缩的图片。")
    };

    private static IReadOnlyList<Entry> SnapshotFolderOrFile(string input, CancellationToken ct)
    {
        input = Path.GetFullPath(input);
        var entries = new List<Entry>();
        long total = 0;
        var root = Directory.Exists(input) ? input : Path.GetDirectoryName(input)!;
        if (!Directory.Exists(input) && !File.Exists(input)) throw new FileNotFoundException("输入文件或文件夹不存在。", input);
        var stack = new Stack<string>();
        stack.Push(input);
        while (stack.TryPop(out var current))
        {
            ct.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(current);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("不处理符号链接、目录联接或在线占位文件：" + current);
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                if (!current.Equals(root, StringComparison.OrdinalIgnoreCase))
                    entries.Add(new(Path.GetRelativePath(root, current).Replace('\\', '/') + "/", null, 0, Directory.GetLastWriteTimeUtc(current)));
                foreach (var child in Directory.EnumerateFileSystemEntries(current)) stack.Push(child);
            }
            else
            {
                var file = new FileInfo(current);
                total = checked(total + file.Length);
                if (total > MaximumExpandedBytes) throw new InvalidDataException("单次任务的原始文件总大小不能超过 5 GB。");
                var name = Path.GetRelativePath(root, current).Replace('\\', '/');
                SafeEntryPath(Path.GetTempPath(), name);
                entries.Add(new(name, current, file.Length, file.LastWriteTimeUtc));
            }
            if (entries.Count > MaximumEntries) throw new InvalidDataException("单次任务最多支持 20,000 个文件及文件夹。");
        }
        return entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<Entry> ExtractChecked(string input, string destination, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        if (!File.Exists(input)) throw new FileNotFoundException("ZIP 文件不存在。", input);
        if (File.GetAttributes(input).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("不能读取链接形式的压缩包。");
        using var stream = File.OpenRead(input);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > MaximumEntries) throw new InvalidDataException("压缩包包含超过 20,000 个条目。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long declared = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            SafeEntryPath(destination, entry.FullName);
            if (!names.Add(entry.FullName.Replace('\\', '/').TrimEnd('/')))
                throw new InvalidDataException("压缩包包含重复或大小写冲突的路径：" + entry.FullName);
            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if ((unixType != 0 && unixType != 0x8000 && unixType != 0x4000) ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("压缩包包含符号链接或特殊文件，已停止解压。");
            declared = checked(declared + entry.Length);
            if (declared > MaximumExpandedBytes) throw new InvalidDataException("压缩包解开后超过 5 GB，已停止处理。");
        }
        Directory.CreateDirectory(destination);
        var result = new List<Entry>();
        long actual = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var path = SafeEntryPath(destination, name);
            if (name.EndsWith('/'))
            {
                if (entry.Length != 0) throw new InvalidDataException("目录条目包含异常数据。");
                Directory.CreateDirectory(path);
                result.Add(new(name, null, 0, entry.LastWriteTime));
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var source = entry.Open();
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                var length = CopyChecked(source, output, Math.Min(entry.Length, MaximumExpandedBytes - actual), ct);
                if (length != entry.Length) throw new InvalidDataException("ZIP 文件长度校验失败：" + name);
                actual += length;
                result.Add(new(name, path, length, entry.LastWriteTime));
            }
            progress?.Report(new(5d * result.Count / Math.Max(1, zip.Entries.Count), $"检查并读取 {result.Count}/{zip.Entries.Count}"));
        }
        return result;
    }

    private static string SafeEntryPath(string root, string entryName)
    {
        var normalized = entryName.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') || normalized.Contains(':') || normalized.Contains('\0'))
            throw new InvalidDataException("压缩包包含不安全路径。");
        var segments = normalized.TrimEnd('/').Split('/');
        foreach (var segment in segments)
        {
            var device = segment.Split('.')[0];
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                device.Equals("CON", StringComparison.OrdinalIgnoreCase) || device.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                device.Equals("AUX", StringComparison.OrdinalIgnoreCase) || device.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                (device.Length == 4 && (device.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || device.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && char.IsDigit(device[3])))
                throw new InvalidDataException("压缩包包含 Windows 不支持或不安全的文件名：" + entryName);
        }
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(rootFull, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("压缩包路径越界，已停止处理。");
        return full;
    }

    private static void WriteZip(IReadOnlyList<Entry> entries, string output, IReadOnlyDictionary<string, string>? alternatives,
        IProgress<ToolProgress>? progress, double start, double span, CancellationToken ct)
    {
        using var file = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        for (var index = 0; index < entries.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var item = entries[index];
            var entry = zip.CreateEntry(item.Name, CompressionLevel.SmallestSize);
            if (item.Modified.Year is >= 1980 and <= 2107) entry.LastWriteTime = item.Modified;
            if (!item.IsDirectory)
            {
                var sourcePath = alternatives is not null && alternatives.TryGetValue(item.Name, out var alternative) ? alternative : item.Source!;
                if (File.GetAttributes(sourcePath).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("源文件变为链接，任务已停止。");
                using var source = File.OpenRead(sourcePath);
                using var target = entry.Open();
                CopyChecked(source, target, MaximumExpandedBytes, ct);
            }
            progress?.Report(new(start + span * (index + 1) / Math.Max(1, entries.Count), $"打包 {index + 1}/{entries.Count}"));
        }
    }

    private static void ValidateResult(string zipPath, IReadOnlyList<Entry> expected, CancellationToken ct)
    {
        using var file = File.OpenRead(zipPath);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        var expectedNames = expected.Select(e => e.Name.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        if (zip.Entries.Count != expected.Count) throw new InvalidDataException("输出压缩包文件数量校验失败。");
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!expectedNames.Remove(entry.FullName.Replace('\\', '/'))) throw new InvalidDataException("输出压缩包路径校验失败。");
            using var data = entry.Open();
            var bytes = CopyChecked(data, Stream.Null, MaximumExpandedBytes, ct);
            if (bytes != entry.Length) throw new InvalidDataException("输出压缩包内容长度校验失败。");
        }
    }

    private static long CopyChecked(Stream source, Stream destination, long limit, CancellationToken ct)
    {
        var buffer = new byte[128 * 1024];
        long copied = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
        {
            ct.ThrowIfCancellationRequested();
            copied = checked(copied + read);
            if (copied > limit) throw new InvalidDataException("文件展开大小超出声明或安全上限，任务已停止。");
            destination.Write(buffer, 0, read);
        }
        ct.ThrowIfCancellationRequested();
        return copied;
    }

    private static string CreateJob()
    {
        var path = Path.Combine(TempParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteJob(string path)
    {
        // Delete only the exact random directory allocated by this operation.
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(TempParent), StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(full), "N", out _)) return;
        try { if (Directory.Exists(full)) Directory.Delete(full, true); }
        catch (IOException) { /* A locked temporary file may be cleaned up after the process exits. */ }
        catch (UnauthorizedAccessException) { }
    }

    private static string GetStem(string input)
    {
        var full = Path.GetFullPath(input).TrimEnd(Path.DirectorySeparatorChar);
        var name = Directory.Exists(full) ? Path.GetFileName(full) : Path.GetFileNameWithoutExtension(full);
        if (string.IsNullOrWhiteSpace(name)) name = "Files";
        return name.Length > 70 ? name[..70] : name;
    }

    private static string PublishFile(string source, string directory, string stem, string extension)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, $"{stem}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}{extension}");
        File.Copy(source, output, false);
        return output;
    }

    private static string NewOutputDirectory(string directory, string stem)
    {
        var output = Path.Combine(Path.GetFullPath(directory), $"{stem}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(output);
        return output;
    }
}
