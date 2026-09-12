using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DiscUtils;
using DiscUtils.Ntfs;
using ImageMagick;
using Shunshou.Core;

namespace Shunshou.SmokeTests;

/// <summary>Recovery integration tests create artificial disk images; they never open a live volume or user recycle bin.</summary>
public static class RecoveryTests
{
    public static async Task RunAsync(string root)
    {
        var workspace = FindWorkspace();
        root = Path.GetFullPath(Path.Combine(root, "recovery"));
        Check(Within(workspace, root), "recovery fixtures must be inside the workspace");
        Directory.CreateDirectory(root);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var timings = new Dictionary<string, double>();
        var total = Stopwatch.StartNew();
        await Measure("RecycleMetadataAndSafeCopy", () => RecycleMetadataAndSafeCopy(root));
        await Measure("PathAndBackupGuards", () => PathAndBackupGuards(root));
        await Measure("EnumerationCancellationKeepsCandidates", () => EnumerationCancellationKeepsCandidates(root));
        await Measure("SectorAlignedReadOnlyStream", SectorAlignedReadOnlyStream);
        await Measure("ManagedNtfsSafetyAndFragmentation", () => ManagedNtfsSafetyAndFragmentation(root));
        var engines = FindEngines(workspace);
        var fixtureTimer = Stopwatch.StartNew();
        var fixture = CreateNtfsFixture(root);
        timings["CreateNtfsFixture"] = fixtureTimer.Elapsed.TotalSeconds;
        await Measure("FileRecordsAndRepeat", () => RecoverFileRecords(fixture, engines, root));
        await Measure("DeepScanAndCombined", () => RecoverByContent(fixture, engines, root));
        await Measure("Cancellation", () => CancelDiskScan(fixture, engines, root));
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new
        {
            Passed = true, Engines = engines, Seconds = total.Elapsed.TotalSeconds, ScenariosSeconds = timings,
            FileRecordsImplementation = "DiscUtils public read-only NTFS API with managed safe output",
            PhotoRecSha256 = HashFile(Path.Combine(engines, "photorec_win.exe")),
            CygwinSha256 = HashFile(Path.Combine(engines, "cygwin1.dll")),
            fixture.ImageHash, SourceUnchanged = HashFile(fixture.Image) == fixture.ImageHash
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS: recovery routes tested against synthetic NTFS image and recycle metadata; no live disk scanned.");

        async Task Measure(string name, Func<Task> action)
        {
            var watch = Stopwatch.StartNew();
            await action();
            timings[name] = watch.Elapsed.TotalSeconds;
            Console.WriteLine($"  {name}: {watch.Elapsed.TotalSeconds:F2}s");
        }
    }

    private sealed record NtfsFixture(string Image, IReadOnlyDictionary<string, byte[]> Deleted, string ImageHash);

    private static NtfsFixture CreateNtfsFixture(string root)
    {
        var imagePath = Path.Combine(root, "人工 NTFS 恢复镜像.img");
        const long capacity = 128L * 1024 * 1024;
        var text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("人工测试资料：顺手工具箱，学习与办公。This file is generated for deleted-file recovery testing.\r\n", 160)));
        var binary = new byte[65_537];
        new Random(303012).NextBytes(binary);
        var pngPixels = new byte[256 * 192 * 3];
        new Random(303013).NextBytes(pngPixels);
        byte[] png;
        using (var image = new MagickImage(pngPixels,
            new MagickReadSettings { Width = 256, Height = 192, Depth = 8, Format = MagickFormat.Rgb }))
            png = image.ToByteArray(MagickFormat.Png);
        var files = new Dictionary<string, byte[]>
        {
            [@"学习资料\已删除中文笔记.txt"] = text,
            [@"学习资料\已删除二进制.bin"] = binary,
            [@"学习资料\已删除图片.png"] = png,
            [@"学习资料\资料数据库.sqlite"] = Encoding.UTF8.GetBytes("Synthetic database fixture. Its extension must not be filtered as an engine artifact.")
        };
        var deletedRecords = new List<(long Offset, byte[] Bytes)>();
        using (var stream = new FileStream(imagePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            stream.SetLength(capacity);
            using var ntfs = NtfsFileSystem.Format(stream, "SHUNSHOU_TEST", Geometry.FromCapacity(capacity), 0, capacity / 512);
            ntfs.CreateDirectory("学习资料");
            foreach (var (name, bytes) in files)
            {
                using var output = ntfs.OpenFile(name, FileMode.CreateNew, FileAccess.Write);
                output.Write(bytes);
            }
            using (var output = ntfs.OpenFile("仍存在的文件.txt", FileMode.CreateNew, FileAccess.Write))
                output.Write(Encoding.UTF8.GetBytes("This file is not deleted and should not be reported as an undeleted file."));
            stream.Flush();
            var boot = new byte[512];
            stream.Position = 0;
            stream.ReadExactly(boot);
            int clusterSize = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11)) * boot[13];
            int encodedRecordSize = unchecked((sbyte)boot[64]);
            int recordSize = encodedRecordSize < 0 ? 1 << -encodedRecordSize : encodedRecordSize * clusterSize;
            Check(recordSize is >= 512 and <= 4096, "synthetic NTFS uses a supported MFT record size");
            var mftExtents = ntfs.PathToExtents("$MFT");
            foreach (var name in files.Keys)
            {
                long logicalOffset = (ntfs.GetFileId(name) & 0x0000FFFFFFFFFFFFL) * recordSize;
                long logicalBase = 0;
                long? physicalOffset = null;
                foreach (var extent in mftExtents)
                {
                    if (logicalOffset >= logicalBase && logicalOffset + recordSize <= logicalBase + extent.Length)
                    { physicalOffset = extent.Start + logicalOffset - logicalBase; break; }
                    logicalBase += extent.Length;
                }
                Check(physicalOffset.HasValue, "fixture record lies wholly in a known MFT extent: " + name);
                var record = new byte[recordSize];
                stream.Position = physicalOffset!.Value;
                stream.ReadExactly(record);
                Check(Encoding.ASCII.GetString(record, 0, 4) == "FILE", "saved record has NTFS FILE signature");
                deletedRecords.Add((physicalOffset.Value, record));
            }
            // Create everything first, then delete, so later fixture creation cannot reuse the deleted records.
            foreach (var name in files.Keys) ntfs.DeleteFile(name);
            Check(files.Keys.All(name => !ntfs.FileExists(name)), "NTFS directory no longer contains deleted fixtures");
            Check(ntfs.FileExists("仍存在的文件.txt"), "live marker remains present in fixture filesystem");
        }
        // DiscUtils deliberately truncates every attribute and resets the MFT record on DeleteFile
        // (File.Delete / MasterFileTable.RemoveRecord). To model a recoverable deletion with stale
        // metadata, retain those pre-deletion record bytes after normal deletion has removed the
        // directory links and released the file/cluster bitmaps. Clear InUse and the link count.
        // This is explicit artificial fixture construction, not a recovery-service shortcut.
        using (var stream = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // DiscUtils' minimal formatter leaves bootstrap code/signature empty. Supply the
            // standard NTFS boot signature in our generated image, rather than weakening validation.
            stream.Position = 510; stream.Write([0x55, 0xAA]);
            foreach (var (offset, record) in deletedRecords)
            {
                var flags = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(22));
                BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(22), (ushort)(flags & ~1));
                BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(18), 0);
                stream.Position = offset;
                stream.Write(record);
            }
        }
        using (var stream = File.OpenRead(imagePath))
        using (var ntfs = new NtfsFileSystem(stream))
            Check(files.Keys.All(name => !ntfs.FileExists(name)), "deleted state persists when the image is reopened");
        var hash = HashFile(imagePath);
        File.WriteAllText(Path.Combine(root, "fixture.json"), JsonSerializer.Serialize(new
        {
            Kind = "Artificial raw NTFS partition image; never a live disk", Capacity = capacity, ImageSha256 = hash,
            DeletionModel = "DiscUtils deletion releases directory entries and bitmaps; saved MFT attributes restored with InUse cleared to model recoverable stale records",
            Deleted = files.Select(pair => new { Path = pair.Key, Length = pair.Value.Length, Sha256 = Hash(pair.Value) })
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Created a 128 MiB NTFS image, deleted Chinese text, binary, PNG and .sqlite fixtures, and recorded source hash.");
        return new(imagePath, files, hash);
    }

    private static async Task SectorAlignedReadOnlyStream()
    {
        var data = new byte[1024 * 1024];
        new Random(303020).NextBytes(data);
        foreach (var sector in new[] { 512, 4096 })
        {
            using var source = new AlignedOnlyFixtureStream(data, sector);
            using var volume = new NtfsReadOnlyVolumeStream(source, data.Length, sector);
            foreach (var position in new[] { 0, 1, 509, 4093, 65_537, data.Length - 113 })
            {
                volume.Position = position;
                var actual = new byte[Math.Min(177_777, data.Length - position)];
                volume.ReadExactly(actual);
                Check(actual.SequenceEqual(data.AsSpan(position, actual.Length).ToArray()),
                    $"{sector}-byte sector wrapper preserves arbitrary offset/length reads without querying raw Length");
            }
            Check(!volume.CanWrite && source.WriteAttempts == 0, "raw volume wrapper never writes source");
            await Throws<NotSupportedException>(() => { volume.WriteByte(1); return Task.CompletedTask; }, "volume write blocked");
            await Throws<IOException>(() => { volume.Position = data.Length + 1; return Task.CompletedTask; }, "volume bounds enforced");
            using var cancelled = new CancellationTokenSource();
            using var cancelVolume = new NtfsReadOnlyVolumeStream(source, data.Length, sector, cancelled.Token);
            cancelled.Cancel();
            await Throws<OperationCanceledException>(() => { cancelVolume.ReadByte(); return Task.CompletedTask; }, "aligned read cancellation");
        }
    }

    /// <summary>Own synthetic image only. Stale MFT bytes model a recoverable deletion, as in CreateNtfsFixture.</summary>
    private static async Task ManagedNtfsSafetyAndFragmentation(string root)
    {
        var image = Path.Combine(root, "人工恶意名称与分散数据.img");
        const long capacity = 32L * 1024 * 1024;
        string longPrefix = new('学', 130);
        var generated = new Dictionary<string, byte[]>
        {
            ["resident.txt"] = Encoding.UTF8.GetBytes("常驻 MFT 的短文本 🧰"),
            ["unsafe-placeholder.txt"] = Encoding.UTF8.GetBytes("Path traversal and alternate-stream fixture, generated only."),
            [longPrefix + "甲.txt"] = Encoding.UTF8.GetBytes("Long name first candidate"),
            [longPrefix + "乙.txt"] = Encoding.UTF8.GetBytes("Long name second candidate"),
            ["fragmented.bin"] = Enumerable.Range(0, 3 * 4096 + 137).Select(i => (byte)(i * 37 + i / 257)).ToArray(),
            ["damaged-fixup.txt"] = Encoding.UTF8.GetBytes("This deliberately broken FILE fixup must never be recovered."),
            ["bad-run.bin"] = Enumerable.Range(0, 9000).Select(i => (byte)(i * 71)).ToArray(),
            ["last-good.txt"] = Encoding.UTF8.GetBytes("Records after malformed records must remain recoverable.")
        };
        var records = new Dictionary<string, (long Offset, byte[] Bytes)>();
        (long Start, long Length)[] mftExtents;
        int sector, cluster, recordSize;
        using (var stream = new FileStream(image, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            stream.SetLength(capacity);
            using var ntfs = NtfsFileSystem.Format(stream, "NTFS_SAFE_FIXTURE", Geometry.FromCapacity(capacity), 0, capacity / 512);
            // Padding records ensure the MFT has data beyond the first eight clusters.
            for (var i = 0; i < 40; i++)
                using (var file = ntfs.OpenFile("live-padding-" + i + ".txt", FileMode.CreateNew, FileAccess.Write)) file.WriteByte((byte)i);
            foreach (var (name, bytes) in generated)
                using (var file = ntfs.OpenFile(name, FileMode.CreateNew, FileAccess.Write)) file.Write(bytes);
            stream.Flush();
            var boot = new byte[512]; stream.Position = 0; stream.ReadExactly(boot);
            sector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11)); cluster = sector * boot[13];
            recordSize = unchecked((sbyte)boot[64]) < 0 ? 1 << -unchecked((sbyte)boot[64]) : boot[64] * cluster;
            Check(cluster == 4096 && sector == 512, "generated fixture uses explicit 4 KiB cluster/512-byte sector layout");
            mftExtents = ntfs.PathToExtents("$MFT").Select(e => (e.Start, e.Length)).ToArray();
            foreach (var name in generated.Keys)
            {
                long logical = (ntfs.GetFileId(name) & 0x0000FFFFFFFFFFFFL) * recordSize;
                long physical = MapMft(logical);
                var bytes = new byte[recordSize]; stream.Position = physical; stream.ReadExactly(bytes);
                records.Add(name, (physical, bytes));
            }
            foreach (var name in generated.Keys) ntfs.DeleteFile(name);
        }
        var expected = generated.Where(p => p.Key is not "damaged-fixup.txt" and not "bad-run.bin")
            .ToDictionary(p => p.Key == "unsafe-placeholder.txt" ? "../escape:stream.txt" : p.Key, p => p.Value);
        long[] fragmentLcns = [capacity / cluster - 128, capacity / cluster - 64, capacity / cluster - 96];
        using (var stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = 510; stream.Write([0x55, 0xAA]);
            foreach (var (name, item) in records)
            {
                var fixedRecord = RemoveFixtureFixups(item.Bytes, sector);
                BinaryPrimitives.WriteUInt16LittleEndian(fixedRecord.AsSpan(22), 0);
                BinaryPrimitives.WriteUInt16LittleEndian(fixedRecord.AsSpan(18), 0);
                if (name == "unsafe-placeholder.txt")
                    fixedRecord = RewriteFixtureAttributes(fixedRecord, attr =>
                    {
                        if (BinaryPrimitives.ReadUInt32LittleEndian(attr) != 0x30) return attr;
                        int offset = BinaryPrimitives.ReadUInt16LittleEndian(attr.AsSpan(20));
                        var nameBytes = Encoding.Unicode.GetBytes("../escape:stream.txt");
                        var changed = new byte[(offset + 66 + nameBytes.Length + 7) / 8 * 8];
                        attr.AsSpan(0, offset + 66).CopyTo(changed);
                        BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(4), (uint)changed.Length);
                        BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(16), (uint)(66 + nameBytes.Length));
                        changed[offset + 64] = (byte)(nameBytes.Length / 2);
                        nameBytes.CopyTo(changed.AsSpan(offset + 66));
                        return changed;
                    });
                if (name == "fragmented.bin")
                {
                    fixedRecord = ReplaceFixtureDataRuns(fixedRecord, [(fragmentLcns[0], 1), (fragmentLcns[1], 1), (fragmentLcns[2], 2)]);
                    var payload = generated[name];
                    for (var run = 0; run < 3; run++)
                    {
                        int offset = run * cluster;
                        int length = Math.Min(payload.Length - offset, (run == 2 ? 2 : 1) * cluster);
                        stream.Position = fragmentLcns[run] * cluster; stream.Write(payload, offset, length);
                    }
                }
                if (name == "bad-run.bin")
                    fixedRecord = ReplaceFixtureDataRuns(fixedRecord, [(capacity / cluster + 10, 3)]);
                var raw = AddFixtureFixups(fixedRecord, sector);
                if (name == "damaged-fixup.txt") raw[sector - 2] ^= 0x33;
                stream.Position = item.Offset; stream.Write(raw);
            }
            // Make the MFT itself fragmented, retaining its first eight clusters in place and
            // moving its remaining allocated stream to an unused area of this artificial image.
            var mftBytes = new byte[checked((int)mftExtents.Sum(e => e.Length))];
            int copied = 0;
            foreach (var extent in mftExtents)
            {
                stream.Position = extent.Start; stream.ReadExactly(mftBytes.AsSpan(copied, (int)extent.Length)); copied += (int)extent.Length;
            }
            const int firstClusters = 8;
            Check(mftExtents.Length == 1 && mftBytes.Length > firstClusters * cluster,
                "fixture starts with a contiguous MFT large enough to split into two runs");
            long relocatedMftLcn = capacity / cluster - 512;
            int tailOffset = firstClusters * cluster;
            stream.Position = relocatedMftLcn * cluster; stream.Write(mftBytes.AsSpan(tailOffset));
            var mftRecord = RemoveFixtureFixups(mftBytes.AsSpan(0, recordSize).ToArray(), sector);
            mftRecord = ReplaceFixtureDataRuns(mftRecord,
                [(mftExtents[0].Start / cluster, firstClusters), (relocatedMftLcn, mftBytes.Length / cluster - firstClusters)]);
            stream.Position = mftExtents[0].Start; stream.Write(AddFixtureFixups(mftRecord, sector));
        }
        string sourceHash = HashFile(image);
        var output = Path.Combine(root, "managed-safe-output");
        var reader = new NtfsDeletedFileReader();
        var result = await reader.RecoverAsync(image, output, null, default);
        Check(!result.Cancelled && result.SkippedRecords >= 2, "managed reader skips malformed fixups/out-of-volume data runs");
        foreach (var (name, bytes) in expected)
            Check(result.Files.Any(f => f.Name == name && HashFile(f.StoredPath) == Hash(bytes)), "managed reader exact bytes: " + name);
        Check(result.Files.Count == expected.Count, "only supported deleted records become candidates");
        Check(result.Files.All(f => Path.GetDirectoryName(f.StoredPath) == output && !Path.GetFileName(f.StoredPath).Contains(':')),
            "malicious path and ADS names become safe flat files; original names are metadata only");
        Check(result.Files.Select(f => f.StoredPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == expected.Count,
            "long Chinese names with identical prefixes never overwrite or collide");
        var firstHashes = result.Files.ToDictionary(f => f.StoredPath, f => HashFile(f.StoredPath));
        var second = await reader.RecoverAsync(image, output, null, default);
        Check(second.Files.Count == expected.Count && second.Files.All(f => !firstHashes.ContainsKey(f.StoredPath)), "CreateNew also protects repeated scans in the same candidate directory");
        Check(firstHashes.All(p => HashFile(p.Key) == p.Value), "repeat scan preserves earlier candidate bytes");
        using var during = new CancellationTokenSource();
        var cancelled = await reader.RecoverAsync(image, Path.Combine(root, "managed-cancelled"),
            new ImmediateProgress<RecoveryProgress>(p => { if (p.Found > 0) during.Cancel(); }), during.Token);
        Check(cancelled.Cancelled && cancelled.Files.Count > 0 && cancelled.Files.Count < expected.Count, "managed cancellation retains only completed candidates");
        Check(cancelled.Files.All(f => expected.TryGetValue(f.Name, out var bytes) && HashFile(f.StoredPath) == Hash(bytes)), "cancelled candidates are complete files");
        Check(HashFile(image) == sourceHash, "managed recovery, malicious metadata and cancellation leave entire source byte-identical");
        File.WriteAllText(Path.Combine(root, "managed-ntfs-verification.json"), JsonSerializer.Serialize(new
        {
            Passed = true, SourceSha256 = sourceHash, MftRuns = 2, FileDataRuns = 3,
            ResidentFile = true, MaliciousName = "../escape:stream.txt", LongChineseNameCharacters = longPrefix.Length + 5,
            result.SkippedRecords, Recovered = result.Files.Select(f => new { f.Name, f.Length, Sha256 = HashFile(f.StoredPath) })
        }, new JsonSerializerOptions { WriteIndented = true }));

        long MapMft(long logical)
        {
            foreach (var extent in mftExtents)
            { if (logical + recordSize <= extent.Length) return extent.Start + logical; logical -= extent.Length; }
            throw new InvalidDataException("Generated record extends outside MFT extents.");
        }
    }

    private static byte[] RemoveFixtureFixups(byte[] raw, int sector)
    {
        var result = raw.ToArray();
        int usa = BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(4));
        for (int i = 1; i <= result.Length / sector; i++) result.AsSpan(usa + 2 * i, 2).CopyTo(result.AsSpan(i * sector - 2, 2));
        return result;
    }

    private static byte[] AddFixtureFixups(byte[] fixedRecord, int sector)
    {
        var result = fixedRecord.ToArray();
        int usa = BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(4));
        for (int i = 1; i <= result.Length / sector; i++)
        {
            result.AsSpan(i * sector - 2, 2).CopyTo(result.AsSpan(usa + 2 * i, 2));
            result.AsSpan(usa, 2).CopyTo(result.AsSpan(i * sector - 2, 2));
        }
        return result;
    }

    private static byte[] RewriteFixtureAttributes(byte[] fixedRecord, Func<byte[], byte[]> rewrite)
    {
        int first = BinaryPrimitives.ReadUInt16LittleEndian(fixedRecord.AsSpan(20));
        var result = new byte[fixedRecord.Length]; fixedRecord.AsSpan(0, first).CopyTo(result);
        int sourceOffset = first, destinationOffset = first;
        while (BinaryPrimitives.ReadUInt32LittleEndian(fixedRecord.AsSpan(sourceOffset)) != uint.MaxValue)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(fixedRecord.AsSpan(sourceOffset + 4));
            var attr = rewrite(fixedRecord.AsSpan(sourceOffset, size).ToArray());
            Check(destinationOffset + attr.Length + 8 <= result.Length, "artificial rewritten attributes fit their MFT record");
            attr.CopyTo(result.AsSpan(destinationOffset)); sourceOffset += size; destinationOffset += attr.Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(destinationOffset), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24), (uint)(destinationOffset + 8));
        return result;
    }

    private static byte[] ReplaceFixtureDataRuns(byte[] fixedRecord, (long Lcn, int Count)[] runs) =>
        RewriteFixtureAttributes(fixedRecord, attr =>
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(attr) != 0x80 || attr[9] != 0) return attr;
            Check(attr[8] == 1, "generated fragmented fixture uses nonresident data");
            int offset = BinaryPrimitives.ReadUInt16LittleEndian(attr.AsSpan(32));
            var result = new byte[(offset + runs.Length * 6 + 1 + 7) / 8 * 8];
            attr.AsSpan(0, offset).CopyTo(result);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)result.Length);
            long previous = 0;
            foreach (var run in runs)
            {
                Check(run.Count is > 0 and < 256, "fixture mapping pair uses a one-byte run length");
                result[offset++] = 0x41; result[offset++] = (byte)run.Count;
                BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), checked((int)(run.Lcn - previous)));
                offset += 4; previous = run.Lcn;
            }
            return result;
        });

    private sealed class AlignedOnlyFixtureStream(byte[] bytes, int sector) : Stream
    {
        private long position;
        public int WriteAttempts { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException("Raw volume Length is deliberately unavailable.");
        public override long Position { get => position; set { Check(value % sector == 0, "underlying raw seek is sector-aligned"); position = value; } }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Check(position % sector == 0 && count % sector == 0, "underlying raw reads are sector-aligned");
            Check(position >= 0 && position + count <= bytes.Length, "raw read stays in artificial volume bounds");
            bytes.AsSpan((int)position, count).CopyTo(buffer.AsSpan(offset, count)); position += count; return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        { SeekOrigin.Begin => offset, SeekOrigin.Current => position + offset, _ => throw new NotSupportedException() };
        public override void Flush() { }
        public override void SetLength(long value) { WriteAttempts++; throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { WriteAttempts++; throw new NotSupportedException(); }
    }

    private static async Task RecoverFileRecords(NtfsFixture fixture, string engines, string root)
    {
        var service = new RecoveryService(engines);
        Check(service.HasDiskEngines, "official PhotoRec content scanner is available");
        var output = Path.Combine(root, "file-records");
        var request = new RecoveryRequest(fixture.Image, output, RecoveryMode.FileRecords);
        var scan = await ScanWithTimeout(service, request);
        Check(!scan.Cancelled && scan.SessionDirectory is not null && Within(output, scan.SessionDirectory), "file-record scan completes inside its selected output directory");
        var hashes = scan.Files.ToDictionary(file => file.StoredPath, file => HashFile(file.StoredPath));
        foreach (var (originalName, bytes) in fixture.Deleted)
        {
            var matches = scan.Files.Where(file => hashes[file.StoredPath] == Hash(bytes)).ToArray();
            Check(matches.Length > 0, "managed NTFS reader recovers exact deleted bytes: " + originalName);
            Check(matches.Any(file => file.Name == Path.GetFileName(originalName)), "managed NTFS reader preserves deleted Chinese filename metadata: " + originalName);
        }
        Check(scan.Files.All(file => file.Name != "仍存在的文件.txt"), "file-record undelete does not confuse a live file with a deleted file");
        Check(HashFile(fixture.Image) == fixture.ImageHash, "managed NTFS reader leaves entire source image byte-identical");
        Check(File.Exists(Path.Combine(scan.SessionDirectory!, "session.json")), "file-record scan writes a session manifest");

        var second = await ScanWithTimeout(service, request);
        Check(second.SessionDirectory != scan.SessionDirectory, "repeated scan creates a distinct session and never overwrites an earlier result");
        Check(hashes.All(pair => File.Exists(pair.Key) && HashFile(pair.Key) == pair.Value), "earlier recovered files survive the second scan unchanged");
        Check(HashFile(fixture.Image) == fixture.ImageHash, "repeated managed NTFS scan still leaves source unchanged");
        Console.WriteLine($"PASS: managed NTFS reader recovered {fixture.Deleted.Count} deleted fixtures with exact hashes and Chinese names; repeated output isolated.");
    }

    private static async Task RecoverByContent(NtfsFixture fixture, string engines, string root)
    {
        var output = Path.Combine(root, "deep-scan");
        var scan = await ScanWithTimeout(new RecoveryService(engines), new(fixture.Image, output, RecoveryMode.DeepScan));
        Check(!scan.Cancelled && scan.SessionDirectory is not null, "PhotoRec content scan completes");
        var pngBytes = fixture.Deleted.Single(pair => pair.Key.EndsWith(".png", StringComparison.Ordinal)).Value;
        Check(scan.Files.Any(file => file.Length == pngBytes.Length && HashFile(file.StoredPath) == Hash(pngBytes)),
            "PhotoRec carves the complete deleted PNG by content with an exact hash");
        Check(scan.Files.All(file => Within(output, file.StoredPath)), "every carved candidate remains inside selected output");
        Check(HashFile(fixture.Image) == fixture.ImageHash, "PhotoRec leaves entire source image byte-identical");
        // Arbitrary .bin and unbounded text have no guaranteed carving signature or exact terminator.
        // Their exact recovery is verified through FileRecords, not incorrectly promised by DeepScan.
        Console.WriteLine($"PASS: PhotoRec produced {scan.Files.Count} candidates and recovered complete PNG bytes; source hash unchanged.");
        var combinedRoot = Path.Combine(root, "combined");
        var combined = await ScanWithTimeout(new RecoveryService(engines), new(fixture.Image, combinedRoot, RecoveryMode.Combined));
        var executedModes = new HashSet<RecoveryMode>();
        foreach (var manifest in Directory.GetFiles(combinedRoot, "session.json", SearchOption.AllDirectories))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(manifest));
            var mode = json.RootElement.GetProperty("Mode");
            executedModes.Add(mode.ValueKind == JsonValueKind.Number ? (RecoveryMode)mode.GetInt32() : Enum.Parse<RecoveryMode>(mode.GetString()!));
        }
        Check(executedModes.Contains(RecoveryMode.FileRecords) && executedModes.Contains(RecoveryMode.DeepScan),
            "combined recovery records execution of both independent disk routes, even when duplicate display rows are merged");
        foreach (var bytes in fixture.Deleted.Values)
            Check(combined.Files.Any(file => file.Length == bytes.Length && HashFile(file.StoredPath) == Hash(bytes)),
                "combined recovery retains exact deleted-file content from successful routes");
        Check(HashFile(fixture.Image) == fixture.ImageHash, "combined recovery leaves source unchanged");
        Console.WriteLine("PASS: combined recovery includes both routes and every exact deleted-file payload.");
    }

    private static async Task CancelDiskScan(NtfsFixture fixture, string engines, string root)
    {
        var service = new RecoveryService(engines);
        var preCancelledOutput = Path.Combine(root, "pre-cancelled");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Throws<OperationCanceledException>(() => service.ScanAsync(new(fixture.Image, preCancelledOutput, RecoveryMode.DeepScan), null, cancelled.Token),
                "pre-cancelled recovery scan");
        }
        Check(!Directory.Exists(preCancelledOutput), "pre-cancelled scan publishes no output directory");
        using var during = new CancellationTokenSource();
        var progress = new ImmediateProgress<RecoveryProgress>(_ => during.Cancel());
        var output = Path.Combine(root, "cancelled-during-scan");
        var scan = await service.ScanAsync(new(fixture.Image, output, RecoveryMode.DeepScan), progress, during.Token);
        Check(during.IsCancellationRequested && scan.Cancelled, "cancelling while an engine is running returns cancelled status");
        Check(scan.SessionDirectory is not null && File.Exists(Path.Combine(scan.SessionDirectory, "session.json")), "cancelled scan retains a readable session manifest");
        var count = Directory.GetFiles(scan.SessionDirectory!, "*", SearchOption.AllDirectories).Length;
        await Task.Delay(750);
        Check(Directory.GetFiles(scan.SessionDirectory!, "*", SearchOption.AllDirectories).Length == count, "cancelled engine stops writing candidates");
        Check(HashFile(fixture.Image) == fixture.ImageHash, "cancellation leaves original disk image unchanged");
        Console.WriteLine("PASS: pre-cancelled scan has no output; mid-scan cancellation stops the engine and preserves source.");
    }

    private static async Task RecycleMetadataAndSafeCopy(string root)
    {
        var bin = Path.Combine(root, "synthetic-recycle-bin");
        Directory.CreateDirectory(bin);
        var deleted = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var v1 = Encoding.UTF8.GetBytes("V1 人工回收站文件");
        var v2 = Encoding.UTF8.GetBytes("V2 人工回收站文件，路径包含 🧰");
        WriteRecycle(bin, "FIRST.txt", 1, @"C:\人工资料\中文笔记.txt", v1, deleted);
        WriteRecycle(bin, "SECOND.txt", 2, @"D:\人工资料\工具🧰笔记.txt", v2, deleted);
        File.WriteAllBytes(Path.Combine(bin, "$Ishort.txt"), [1, 2, 3]);
        var invalid = new byte[32];
        BinaryPrimitives.WriteInt64LittleEndian(invalid, 2);
        BinaryPrimitives.WriteInt32LittleEndian(invalid.AsSpan(24), int.MaxValue);
        File.WriteAllBytes(Path.Combine(bin, "$Iinvalid.txt"), invalid);
        File.WriteAllBytes(Path.Combine(bin, "$Rinvalid.txt"), [10]);
        WriteRecycle(bin, "missing.txt", 2, @"C:\人工资料\已丢失.txt", [4, 5], deleted);
        File.Delete(Path.Combine(bin, "$Rmissing.txt"));

        var before = Directory.GetFiles(bin).ToDictionary(path => path, HashFile);
        var scan = RecoveryService.ScanRecycleDirectory(bin, null, default);
        Check(scan.Files.Count == 2, "recycle parser accepts valid v1/v2 and skips malformed/missing payloads");
        Check(scan.Files.Any(file => file.Name == "中文笔记.txt" && file.Length == v1.Length && file.DeletedAt?.ToUniversalTime() == deleted), "v1 fixed UTF-16 path and deleted time are decoded");
        Check(scan.Files.Any(file => file.Name == "工具🧰笔记.txt" && file.OriginalPath == @"D:\人工资料\工具🧰笔记.txt"), "v2 length-prefixed UTF-16 path preserves Chinese and surrogate pairs");
        Check(RecoveryService.Filter(scan.Files, "🧰", ".TXT, png").Count == 1, "candidate filtering matches names and case-insensitive file types");
        var output = Path.Combine(root, "copied-recycle-files");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "中文笔记.txt"), "existing user-like fixture must remain unchanged");
        var copied = await RecoveryService.CopySelectedAsync(scan.Files, bin, output, default);
        Check(copied.Count == 2 && copied.Any(path => Path.GetFileName(path) == "中文笔记_2.txt"), "copying recovered files resolves name collisions without overwrite");
        Check(File.ReadAllText(Path.Combine(output, "中文笔记.txt")) == "existing user-like fixture must remain unchanged", "existing output content remains unchanged");
        Check(copied.Select(HashFile).ToHashSet().SetEquals([Hash(v1), Hash(v2)]), "copied recycle contents have original hashes");
        var maliciousName = scan.Files[0] with { Name = @"..\..\escaped.txt" };
        var reservedName = scan.Files[0] with { Name = "CON.txt" };
        var safeCopies = await RecoveryService.CopySelectedAsync([maliciousName, reservedName], bin, output, default);
        Check(safeCopies.All(path => Path.GetDirectoryName(path) == output) && !File.Exists(Path.Combine(root, "escaped.txt")), "candidate names cannot traverse outside the destination");
        Check(safeCopies.Any(path => Path.GetFileName(path) == "Recovered_CON.txt"), "reserved Windows device names are made safe");
        Check(before.All(pair => HashFile(pair.Key) == pair.Value), "scanning and copying recycle fixtures preserve every original metadata and payload file");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Throws<OperationCanceledException>(() => Task.Run(() => RecoveryService.ScanRecycleDirectory(bin, null, cancelled.Token)), "cancelled recycle scan");
        Console.WriteLine("PASS: synthetic $I v1/v2 parsing, invalid metadata handling, filtering, safe names and non-overwriting copy.");
    }

    private static async Task PathAndBackupGuards(string root)
    {
        var source = Path.Combine(root, "backup-source");
        var target = Path.Combine(root, "backup-output");
        Directory.CreateDirectory(Path.Combine(source, "子目录"));
        File.WriteAllText(Path.Combine(source, "子目录", "资料.txt"), "Generated backup fixture");
        await Throws<InvalidOperationException>(() => Task.FromResult(RecoveryService.ValidateDestination(source, Path.Combine(source, "nested"))), "output nested in source rejected");
        await Throws<InvalidOperationException>(() => Task.FromResult(RecoveryService.ValidateDestination(source, source)), "source and destination equality rejected");
        await Throws<InvalidOperationException>(() => Task.FromResult(RecoveryService.ValidateDestination(Path.GetPathRoot(root)!, target)), "same live source volume rejected without opening that volume");
        await Throws<InvalidOperationException>(() => Task.FromResult(RecoveryService.ValidateDestination(source, @"\\invalid.example\share\target")), "network destination rejected before network access");
        var service = new RecoveryService();
        var scan = await service.ScanAsync(new(source, target, RecoveryMode.BackupFolder), null, default);
        Check(scan.Files.Count == 1 && scan.Files[0].OriginalPath == Path.Combine("子目录", "资料.txt"), "backup enumeration preserves relative source context");
        var junction = Path.Combine(root, "junction-to-fixture");
        await CreateJunction(root, junction, source);
        await Throws<IOException>(() => Task.FromResult(RecoveryService.ValidateDestination(source, Path.Combine(junction, "output"))), "destination reparse ancestor rejected");
        await Throws<IOException>(() => service.ScanAsync(new(junction, target, RecoveryMode.BackupFolder), null, default), "backup source reparse ancestor rejected");
        await Throws<IOException>(() => RecoveryService.CopySelectedAsync([scan.Files[0] with { StoredPath = Path.Combine(junction, "子目录", "资料.txt") }],
            source, target, default), "selected recovered file through reparse ancestor rejected");
        var fakeImage = Path.Combine(source, "fake.img");
        File.WriteAllBytes(fakeImage, new byte[512]);
        var engines = FindEngines(FindWorkspace());
        await Throws<IOException>(() => new RecoveryService(engines).ScanAsync(new(Path.Combine(junction, "fake.img"), target, RecoveryMode.DeepScan), null, default),
            "disk image source through reparse ancestor rejected");
        await Throws<InvalidOperationException>(() => new RecoveryService(engines).ScanAsync(new(fakeImage, target, RecoveryMode.FileRecords), null, default),
            "non-NTFS data is rejected by the file-record route before the recovery engine starts");
        Check(File.Exists(Path.Combine(source, "子目录", "资料.txt")), "reparse guards leave fixture target untouched");
        Console.WriteLine("PASS: destination confinement, same-volume guard, backup enumeration and reparse ancestor rejection.");
    }

    private static async Task EnumerationCancellationKeepsCandidates(string root)
    {
        var bin = Path.Combine(root, "cancel-recycle-files");
        Directory.CreateDirectory(bin);
        for (var i = 0; i < 3; i++)
            WriteRecycle(bin, $"{i}.txt", 2, $@"C:\人工资料\取消保留{i}.txt", Encoding.UTF8.GetBytes($"recycle-{i}"), DateTime.UtcNow);
        using (var cancellation = new CancellationTokenSource())
        {
            var scan = RecoveryService.ScanRecycleDirectory(bin,
                new ImmediateProgress<RecoveryProgress>(p => { if (p.Found > 0) cancellation.Cancel(); }), cancellation.Token);
            Check(scan.Cancelled && scan.Files.Count == 1 && File.Exists(scan.Files[0].StoredPath),
                "cancelling recycle enumeration retains the first discovered candidate");
        }

        var folderBin = Path.Combine(root, "cancel-recycle-folder");
        Directory.CreateDirectory(folderBin);
        WriteRecycle(folderBin, "FOLDER", 2, @"C:\人工资料\已删目录", [], DateTime.UtcNow);
        string storedFolder = Path.Combine(folderBin, "$RFOLDER");
        File.Delete(storedFolder); // Replace only the generated payload fixture with a deleted-folder fixture.
        Directory.CreateDirectory(storedFolder);
        for (var i = 0; i < 3; i++) File.WriteAllText(Path.Combine(storedFolder, $"嵌套{i}.txt"), $"nested-{i}");
        using (var cancellation = new CancellationTokenSource())
        {
            var scan = RecoveryService.ScanRecycleDirectory(folderBin,
                new ImmediateProgress<RecoveryProgress>(p => { if (p.Found > 0) cancellation.Cancel(); }), cancellation.Token);
            Check(scan.Cancelled && scan.Files.Count == 1 && scan.Files[0].OriginalPath?.StartsWith(@"C:\人工资料\已删目录\", StringComparison.Ordinal) == true,
                "cancelling inside a deleted folder retains its discovered candidate and original path");
        }

        var backup = Path.Combine(root, "cancel-backup");
        Directory.CreateDirectory(backup);
        for (var i = 0; i < 3; i++) File.WriteAllText(Path.Combine(backup, $"备份{i}.txt"), $"backup-{i}");
        using (var cancellation = new CancellationTokenSource())
        {
            var scan = await new RecoveryService().ScanAsync(new(backup, Path.Combine(root, "cancel-backup-output"), RecoveryMode.BackupFolder),
                new ImmediateProgress<RecoveryProgress>(p => { if (p.Found > 0) cancellation.Cancel(); }), cancellation.Token);
            Check(scan.Cancelled && scan.Files.Count == 1 && File.Exists(scan.Files[0].StoredPath),
                "cancelling backup enumeration retains its first candidate");
        }
        Check(Directory.GetFiles(backup).Length == 3 && Directory.GetFiles(storedFolder).Length == 3,
            "cancelled enumeration leaves generated source files untouched");
        Console.WriteLine("PASS: cancelled recycle, deleted-folder and backup scans retain already discovered candidates.");
    }

    private static void WriteRecycle(string root, string suffix, long version, string original, byte[] content, DateTime deleted)
    {
        var path = Encoding.Unicode.GetBytes(original + '\0');
        var bytes = new byte[version == 1 ? 544 : 28 + path.Length];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, version);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), content.Length);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), deleted.ToFileTimeUtc());
        if (version == 2) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), path.Length / 2);
        path.CopyTo(bytes.AsSpan(version == 1 ? 24 : 28));
        File.WriteAllBytes(Path.Combine(root, "$I" + suffix), bytes);
        File.WriteAllBytes(Path.Combine(root, "$R" + suffix), content);
    }

    private static async Task<RecoveryScan> ScanWithTimeout(RecoveryService service, RecoveryRequest request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var result = await service.ScanAsync(request, new ImmediateProgress<RecoveryProgress>(p => Console.WriteLine("  " + p.Message)), timeout.Token);
        Check(!timeout.IsCancellationRequested, "real recovery engine finishes within the fixture time budget");
        return result;
    }

    private static async Task CreateJunction(string root, string link, string target)
    {
        Check(Within(root, link) && Within(root, target) && !Directory.Exists(link) && Directory.Exists(target),
            "junction setup only accepts new links and generated targets inside this test fixture");
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var script = $"New-Item -Path {Quote(link)} -ItemType Junction -Value {Quote(target)} -ErrorAction Stop | Out-Null";
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(start) ?? throw new IOException("Cannot create junction test fixture.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await stdout;
        Check(process.ExitCode == 0, "junction fixture creation succeeds: " + await stderr);
        Check(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint), "junction fixture is a real Windows reparse point");
    }

    private static string FindWorkspace()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "src", "Shunshou.Core", "Shunshou.Core.csproj"))) return directory.FullName;
        throw new DirectoryNotFoundException("Run recovery tests inside the repository workspace.");
    }

    private static string FindEngines(string workspace)
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("SHUNSHOU_RECOVERY_ENGINES");
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            overrideDirectory = Path.GetFullPath(overrideDirectory);
            Check(Within(workspace, overrideDirectory), "test engine override must name a directory inside this workspace");
            if (!File.Exists(Path.Combine(overrideDirectory, "photorec_win.exe")))
                throw new FileNotFoundException("Test recovery engine override does not contain the official PhotoRec CLI executable.");
            return overrideDirectory;
        }
        foreach (var directory in new[] { Path.Combine(workspace, "runtime", "recovery", "bin"),
            Path.Combine(workspace, ".tools", "recovery-upstream-7.2", "testdisk-7.2") })
            if (File.Exists(Path.Combine(directory, "photorec_win.exe"))) return directory;
        throw new FileNotFoundException("Download the pinned official recovery engines before running the recovery integration tests.");
    }

    private static bool Within(string root, string path) => Path.GetFullPath(path).StartsWith(
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("FAILED: " + message); }
    private static async Task Throws<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("FAILED: expected " + typeof(T).Name + ": " + message);
    }
    private sealed class ImmediateProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
}
