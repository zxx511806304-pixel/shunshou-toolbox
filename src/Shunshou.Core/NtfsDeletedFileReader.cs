using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using DiscUtils.Ntfs;
using DiscUtils.Ntfs.Internals;

namespace Shunshou.Core;

public sealed record NtfsRecoveryResult(IReadOnlyList<RecoveryCandidate> Files, int SkippedRecords,
    bool Cancelled, string Message);

/// <summary>
/// Reads deleted NTFS records through DiscUtils' public, read-only forensic APIs. Disk names and
/// parent paths are display metadata only; every output is a new ordinary file in one flat directory.
/// No shell, external undelete writer, reflection, filesystem repair, or source write handle is used.
/// </summary>
public sealed class NtfsDeletedFileReader
{
    private const long MaximumRecords = 2_000_000;
    private const int MaximumCandidates = 100_000;
    private const long FreeSpaceReserve = 256L * 1024 * 1024;

    public Task<NtfsRecoveryResult> RecoverAsync(string sourcePath, string candidateDirectory,
        IProgress<RecoveryProgress>? progress, CancellationToken ct) =>
        Task.Run(() => Recover(sourcePath, candidateDirectory, progress, ct), ct);

    private static NtfsRecoveryResult Recover(string sourcePath, string candidateDirectory,
        IProgress<RecoveryProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        candidateDirectory = Path.GetFullPath(candidateDirectory);
        RecoveryService.RejectReparseAncestors(candidateDirectory);
        var device = sourcePath.StartsWith(@"\\.\", StringComparison.Ordinal);
        if (!device)
        {
            sourcePath = Path.GetFullPath(sourcePath);
            RecoveryService.RejectReparseAncestors(sourcePath);
            if (sourcePath == candidateDirectory || candidateDirectory.StartsWith(sourcePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("恢复目录不能位于源镜像路径内部。");
        }
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.RandomAccess);
        // A 4 KiB first read is sector-aligned on supported 512/1K/2K/4K devices. Do not ask a
        // raw-volume FileStream for Length, and do not rely on its implicit 4 KiB buffering.
        var boot = new byte[device ? 4096 : 512];
        input.ReadExactly(boot);
        var (volumeLength, recordSize, sectorSize, clusterSize) = ValidateBoot(boot);
        if (!device && volumeLength > input.Length)
            throw new InvalidDataException("NTFS 镜像比引导记录声明的卷大小短，无法安全读取删除记录。");
        input.Position = 0;
        using var bounded = new NtfsReadOnlyVolumeStream(input, volumeLength, sectorSize, ct);
        var firstRecord = new byte[recordSize];
        bounded.Position = checked(BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(48)) * clusterSize);
        bounded.ReadExactly(firstRecord);
        if (!ValidateRecord(firstRecord, sectorSize, clusterSize, volumeLength))
            throw new InvalidDataException("NTFS 主文件表记录损坏或使用暂不支持的属性布局。");
        bounded.Position = 0;
        using var ntfs = new NtfsFileSystem(bounded);
        long mftLength = ntfs.GetFileLength("$MFT");
        if (mftLength < recordSize || mftLength > volumeLength || mftLength % recordSize != 0)
            throw new InvalidDataException("NTFS 主文件表长度不合法。");
        long availableRecords = mftLength / recordSize;
        long recordCount = Math.Min(availableRecords, MaximumRecords);
        var mft = ntfs.GetMasterFileTable();
        using var rawMft = ntfs.OpenFile("$MFT", FileMode.Open, FileAccess.Read);
        var recordBuffer = new byte[recordSize];
        var files = new List<RecoveryCandidate>();
        var skipped = 0;
        var cancelled = false;
        var progressWatch = Stopwatch.StartNew();
        var lastReportedCount = -1;
        string? stopped = null;
        Directory.CreateDirectory(candidateDirectory);
        for (long index = 24; index < recordCount; index++)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }
            if (index % 128 == 0 && progressWatch.ElapsedMilliseconds >= 100) ReportProgress(index);
            if (files.Count >= MaximumCandidates) { stopped = "已达到单次 100,000 个候选文件的安全上限"; break; }
            string? partial = null;
            try
            {
                rawMft.Position = checked(index * recordSize);
                rawMft.ReadExactly(recordBuffer);
                if (Encoding.ASCII.GetString(recordBuffer, 0, 4) != "FILE") continue;
                if ((BinaryPrimitives.ReadUInt16LittleEndian(recordBuffer.AsSpan(22)) & 3) != 0) continue;
                if (!ValidateRecord(recordBuffer, sectorSize, clusterSize, volumeLength)) { skipped++; continue; }
                // Indexed reads permit a damaged record to be skipped without abandoning later records.
                // DiscUtils resolves the MFT's own runs and verifies the FILE update-sequence fixups.
                var entry = mft[index];
                if (entry is null || entry.Flags.HasFlag(MasterFileTableEntryFlags.InUse) ||
                    entry.Flags.HasFlag(MasterFileTableEntryFlags.IsDirectory)) continue;
                if (entry.BaseRecordReference.RecordIndex != 0) { skipped++; continue; }
                var attributes = entry.Attributes;
                if (attributes.Any(a => a.AttributeType is AttributeType.AttributeList or AttributeType.ReparsePoint))
                { skipped++; continue; }
                var names = attributes.OfType<FileNameAttribute>().Where(n => !string.IsNullOrWhiteSpace(n.FileName))
                    .OrderBy(n => (int)n.FileNameNamespace == 2 ? 1 : 0).ThenByDescending(n => n.FileName.Length).ToArray();
                if (names.Length == 0) continue;
                var data = attributes.Where(a => a.AttributeType == AttributeType.Data && string.IsNullOrEmpty(a.Name)).ToArray();
                if (data.Length != 1 || data[0].Flags != DiscUtils.Ntfs.Internals.AttributeFlags.None ||
                    data[0].ContentLength < 0 || data[0].ContentLength > volumeLength)
                { skipped++; continue; }
                var attribute = data[0];
                var content = attribute.Content;
                if (content.Capacity != attribute.ContentLength) { skipped++; continue; }
                var disk = new DriveInfo(Path.GetPathRoot(candidateDirectory)!);
                if (disk.AvailableFreeSpace - FreeSpaceReserve < attribute.ContentLength)
                { stopped = "保存磁盘的剩余空间不足，已停止读取并保留已恢复文件"; break; }
                string originalName = names[0].FileName;
                string originalPath = DisplayOriginalPath(mft, rawMft, names[0], recordCount, recordSize,
                    sectorSize, clusterSize, volumeLength, ct);
                RecoveryService.RejectReparseAncestors(candidateDirectory);
                using (var output = CreateCandidate(candidateDirectory, originalName, index, out var createdPath))
                {
                    partial = createdPath;
                    var buffer = new byte[128 * 1024];
                    long copied = 0;
                    while (copied < attribute.ContentLength)
                    {
                        ct.ThrowIfCancellationRequested();
                        int wanted = (int)Math.Min(buffer.Length, attribute.ContentLength - copied);
                        int read = content.Read(copied, buffer, 0, wanted);
                        if (read <= 0 || read > wanted) throw new InvalidDataException("删除记录的数据块不完整。");
                        output.Write(buffer, 0, read);
                        copied += read;
                    }
                    output.Flush();
                    if (output.Length != attribute.ContentLength) throw new InvalidDataException("恢复文件的长度核对失败。");
                }
                files.Add(new(originalName, partial!, originalPath, attribute.ContentLength, "删除记录"));
                partial = null;
            }
            catch (OperationCanceledException) { cancelled = true; break; }
            catch (Exception ex) when (ex is IOException or ArgumentException or IndexOutOfRangeException or
                OverflowException or NotSupportedException)
            {
                skipped++;
            }
            finally
            {
                // Only our own CreateNew file is eligible for removal, never an input or an earlier candidate.
                if (partial is not null) { try { File.Delete(partial); } catch (IOException) { } }
            }
            if (files.Count != lastReportedCount && (lastReportedCount < 0 || progressWatch.ElapsedMilliseconds >= 100))
                ReportProgress(index);
        }
        if (ct.IsCancellationRequested) cancelled = true;
        var message = cancelled ? $"已停止，保留 {files.Count:N0} 个完整候选文件。" :
            $"删除记录查找结束，保存 {files.Count:N0} 个候选文件。";
        if (skipped > 0) message += $" 跳过 {skipped:N0} 条无法可靠读取的记录，可继续尝试深度扫描。";
        if (stopped is not null) message += " " + stopped + "。";
        if (availableRecords > MaximumRecords) message += $" 本次最多检查前 {MaximumRecords:N0} 条记录。";
        message += " 原内容可能已被覆盖，请预览核对。";
        return new(files, skipped, cancelled, message);

        void ReportProgress(long index)
        {
            progress?.Report(new($"只读检查 NTFS 记录 {index + 1:N0}/{recordCount:N0}，已恢复 {files.Count:N0} 个候选", files.Count));
            lastReportedCount = files.Count;
            progressWatch.Restart();
        }
    }

    private static (long VolumeLength, int RecordSize, int SectorSize, int ClusterSize) ValidateBoot(byte[] boot)
    {
        if (Encoding.ASCII.GetString(boot, 3, 8) != "NTFS    " || boot[510] != 0x55 || boot[511] != 0xAA)
            throw new InvalidDataException("没有找到有效的 NTFS 引导记录。");
        int sector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11));
        int sectorsPerCluster = boot[13];
        if (sector is not (512 or 1024 or 2048 or 4096) || sectorsPerCluster == 0 ||
            (sectorsPerCluster & (sectorsPerCluster - 1)) != 0)
            throw new InvalidDataException("NTFS 扇区或簇大小不受支持。");
        long totalSectors = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(40));
        long volumeLength = checked(totalSectors * sector);
        int cluster = checked(sector * sectorsPerCluster);
        int encoded = unchecked((sbyte)boot[64]);
        int record = encoded < 0 && encoded >= -16 ? 1 << -encoded : encoded > 0 ? checked(encoded * cluster) : 0;
        long mftCluster = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(48));
        if (volumeLength < 512 || record < sector || record > 65536 || record % sector != 0 ||
            mftCluster < 0 || mftCluster >= volumeLength / cluster)
            throw new InvalidDataException("NTFS 文件记录或主文件表地址超出边界。");
        return (volumeLength, record, sector, cluster);
    }

    private static bool ValidateRecord(byte[] original, int sector, int cluster, long volumeLength)
    {
        // Guard allocations and unsupported layouts before GenericAttribute constructs its typed
        // objects. This validates bytes already mapped through the MFT's read-only logical stream;
        // DiscUtils remains responsible for resolving that stream's fragmented runs.
        var record = original.ToArray();
        try
        {
            if (Encoding.ASCII.GetString(record, 0, 4) != "FILE") return false;
            int fixup = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4));
            int fixupCount = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(6));
            if (fixup < 8 || fixupCount != record.Length / sector + 1 || fixup + fixupCount * 2 > record.Length) return false;
            ushort signature = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(fixup));
            for (var i = 1; i < fixupCount; i++)
            {
                int tail = i * sector - 2;
                if (BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(tail)) != signature) return false;
                record[tail] = record[fixup + i * 2];
                record[tail + 1] = record[fixup + i * 2 + 1];
            }
            int offset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));
            uint used = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(24));
            uint allocated = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(28));
            if (offset < 32 || used > record.Length || allocated > record.Length || allocated < used || offset >= used) return false;
            for (var count = 0; count < 256 && offset + 4 <= used; count++)
            {
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset));
                if (type == uint.MaxValue) return true;
                if (offset + 24 > used || type == (uint)AttributeType.AttributeList) return false;
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset + 4));
                if (size < 24 || size > used - offset || size % 8 != 0) return false;
                var attr = record.AsSpan(offset, (int)size);
                int nameBytes = attr[9] * 2;
                int nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr[10..]);
                if (nameBytes > 0 && (nameOffset < 24 || nameOffset + nameBytes > size)) return false;
                ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(attr[12..]);
                if (attr[8] == 0)
                {
                    uint bytes = BinaryPrimitives.ReadUInt32LittleEndian(attr[16..]);
                    int contentOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr[20..]);
                    if (contentOffset < 24 || contentOffset > size || bytes > size - contentOffset) return false;
                    if (type == (uint)AttributeType.FileName && (bytes < 66 || bytes > 576 ||
                        attr[contentOffset + 64] * 2 + 66 > bytes)) return false;
                    if (type == (uint)AttributeType.StandardInformation && bytes > 1024) return false;
                }
                else if (attr[8] == 1)
                {
                    if (size < 64 || flags != 0 || type is (uint)AttributeType.FileName or (uint)AttributeType.StandardInformation) return false;
                    long startVcn = BinaryPrimitives.ReadInt64LittleEndian(attr[16..]);
                    long endVcn = BinaryPrimitives.ReadInt64LittleEndian(attr[24..]);
                    int runOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr[32..]);
                    long contentLength = BinaryPrimitives.ReadInt64LittleEndian(attr[48..]);
                    long initializedLength = BinaryPrimitives.ReadInt64LittleEndian(attr[56..]);
                    if (startVcn != 0 || endVcn < 0 || runOffset < 64 || runOffset >= size ||
                        contentLength < 0 || contentLength > volumeLength || initializedLength < 0 || initializedLength > contentLength) return false;
                    long lcn = 0;
                    long clusters = 0;
                    bool terminated = false;
                    while (runOffset < size)
                    {
                        int header = attr[runOffset++];
                        if (header == 0) { terminated = true; break; }
                        int lengthBytes = header & 15;
                        int offsetBytes = header >> 4;
                        if (lengthBytes is < 1 or > 8 || offsetBytes is < 1 or > 8 || runOffset + lengthBytes + offsetBytes > size) return false;
                        ulong length = 0;
                        for (var i = 0; i < lengthBytes; i++) length |= (ulong)attr[runOffset + i] << (8 * i);
                        runOffset += lengthBytes;
                        ulong rawDelta = 0;
                        for (var i = 0; i < offsetBytes; i++) rawDelta |= (ulong)attr[runOffset + i] << (8 * i);
                        bool negative = (attr[runOffset + offsetBytes - 1] & 128) != 0;
                        if (negative && offsetBytes < 8) rawDelta |= ulong.MaxValue << (offsetBytes * 8);
                        runOffset += offsetBytes;
                        if (length == 0 || length > long.MaxValue) return false;
                        lcn = checked(lcn + unchecked((long)rawDelta));
                        long end = checked(lcn + (long)length);
                        if (lcn < 0 || end > volumeLength / cluster) return false;
                        clusters = checked(clusters + (long)length);
                    }
                    if (!terminated || clusters != checked(endVcn + 1) || checked(clusters * cluster) < contentLength) return false;
                }
                else return false;
                offset += (int)size;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException) { return false; }
        return false;
    }

    private static string DisplayOriginalPath(DiscUtils.Ntfs.Internals.MasterFileTable mft, Stream rawMft,
        FileNameAttribute name, long recordCount, int recordSize, int sector, int cluster, long volumeLength, CancellationToken ct)
    {
        var parts = new List<string> { name.FileName };
        var parent = name.ParentDirectory;
        var visited = new HashSet<long>();
        for (var depth = 0; depth < 64 && parent.RecordIndex != 5; depth++)
        {
            ct.ThrowIfCancellationRequested();
            if (parent.RecordIndex < 0 || parent.RecordIndex >= recordCount || !visited.Add(parent.RecordIndex)) break;
            try
            {
                var raw = new byte[recordSize];
                rawMft.Position = checked(parent.RecordIndex * recordSize);
                rawMft.ReadExactly(raw);
                if (!ValidateRecord(raw, sector, cluster, volumeLength)) break;
                var entry = mft[parent.RecordIndex];
                if (entry is null || !entry.Flags.HasFlag(MasterFileTableEntryFlags.IsDirectory) ||
                    (parent.RecordSequenceNumber != 0 && entry.SequenceNumber != parent.RecordSequenceNumber)) break;
                var parentName = entry.Attributes.OfType<FileNameAttribute>().OrderBy(n => (int)n.FileNameNamespace == 2 ? 1 : 0).FirstOrDefault();
                if (parentName is null) break;
                parts.Add(parentName.FileName);
                parent = parentName.ParentDirectory;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or IndexOutOfRangeException or OverflowException) { break; }
        }
        parts.Reverse();
        return string.Join('\\', parts);
    }

    private static FileStream CreateCandidate(string directory, string original, long record, out string path)
    {
        // Never treat disk metadata as a path: replace separators/colons and control characters.
        var safe = new string(original.Select(c => c < 32 || Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).TrimEnd(' ', '.');
        if (safe.Length == 0) safe = "Recovered";
        var extension = Path.GetExtension(safe);
        if (extension.Length > 20) extension = "";
        var stem = extension.Length > 0 ? safe[..^extension.Length] : safe;
        // Truncate by Unicode scalar rather than slicing a surrogate pair. The record ID prevents
        // two long names with the same prefix from colliding; CreateNew handles repeat calls safely.
        stem = string.Concat(stem.EnumerateRunes().Take(80).Select(rune => rune.ToString()));
        var baseName = $"Recovered_{stem}_mft{record:X}";
        for (var suffix = 0; ; suffix++)
        {
            path = Path.Combine(directory, baseName + (suffix == 0 ? "" : "_" + suffix) + extension);
            if (Path.GetDirectoryName(Path.GetFullPath(path)) != directory) throw new IOException("恢复目标路径不安全。");
            try { return new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024); }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path)) { }
        }
    }

}

/// <summary>Sector-aligned, bounded reads over a volume handle; does not query or own the source stream's length/lifetime.</summary>
public sealed class NtfsReadOnlyVolumeStream : Stream
{
    private readonly Stream source;
    private readonly long length;
    private readonly int sector;
    private readonly CancellationToken ct;
    private readonly byte[] aligned;
    private long position;

    public NtfsReadOnlyVolumeStream(Stream source, long length, int sectorSize, CancellationToken ct = default)
    {
        if (!source.CanRead || !source.CanSeek) throw new ArgumentException("NTFS 来源需要支持定位读取。", nameof(source));
        if (sectorSize is not (512 or 1024 or 2048 or 4096) || length <= 0 || length % sectorSize != 0)
            throw new ArgumentOutOfRangeException(nameof(length), "卷边界和扇区大小必须有效且对齐。");
        this.source = source;
        this.length = length;
        sector = sectorSize;
        this.ct = ct;
        aligned = new byte[128 * 1024 + sector];
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
        ct.ThrowIfCancellationRequested();
        if (position >= length || count == 0) return 0;
        int wanted = (int)Math.Min(Math.Min(count, 128 * 1024), length - position);
        long start = position / sector * sector;
        int prefix = (int)(position - start);
        int readSize = (prefix + wanted + sector - 1) / sector * sector;
        if (start + readSize > length) throw new IOException("对齐读取超出卷边界。");
        source.Position = start;
        source.ReadExactly(aligned.AsSpan(0, readSize));
        ct.ThrowIfCancellationRequested();
        aligned.AsSpan(prefix, wanted).CopyTo(buffer.AsSpan(offset, wanted));
        position += wanted;
        return wanted;
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        ct.ThrowIfCancellationRequested();
        long next = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => checked(position + offset),
            SeekOrigin.End => checked(length + offset), _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
        if (next < 0 || next > length) throw new IOException("NTFS 记录试图读取卷边界以外的数据。");
        return position = next;
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException("NTFS 来源只读。");
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("NTFS 来源只读。");
}
