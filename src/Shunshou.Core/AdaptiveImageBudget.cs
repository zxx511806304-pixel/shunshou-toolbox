using System.IO.Compression;
using ImageMagick;

namespace Shunshou.Core;

internal sealed record BudgetImage(string Name, string Source, string Baseline, long CompressedBytes);
internal sealed record ImageBudgetResult(IReadOnlyDictionary<string, string> Paths, string Description,
    IReadOnlyList<string> SkippedImages);

/// <summary>
/// Allocates the ZIP's remaining bytes to individual images. The size model uses the same Deflate
/// level as the final ZIP; only the completed ZIP decides whether the upload limit was met.
/// Screenshot detection is conservative and heuristic, not an OCR or legibility guarantee.
/// </summary>
internal static class AdaptiveImageBudget
{
    private sealed record Candidate(string Path, long Bytes, uint? Quality = null, int Scale = 100, bool Owned = false);
    private sealed class ImageState(BudgetImage input, int index)
    {
        public BudgetImage Input { get; } = input;
        public int Index { get; } = index;
        public Candidate Current { get; set; } = new(input.Baseline, input.CompressedBytes);
        public bool Supported { get; set; }
        public bool Protected { get; set; }
        public MagickFormat Format { get; set; }
    }

    public static ImageBudgetResult Optimize(IReadOnlyList<BudgetImage> inputs, long initialZipBytes,
        long targetZipBytes, bool allowResize, string workingDirectory, IProgress<ToolProgress>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(workingDirectory);
        var states = inputs.Select((input, index) => new ImageState(input, index)).ToArray();
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long estimatedBytes = initialZipBytes;
        var sequence = 0;
        for (var index = 0; index < states.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var state = states[index];
            try { Inspect(state, ct); }
            catch (MagickException) { skipped.Add(state.Input.Name); }
            catch (InvalidDataException) { skipped.Add(state.Input.Name); }
            if (!state.Supported) skipped.Add(state.Input.Name);
            progress?.Report(new(30 + 6d * (index + 1) / Math.Max(1, states.Length),
                $"逐图检查 {index + 1}/{states.Length}：{Path.GetFileName(state.Input.Name)}"));
        }

        // PNG's quality slider is not a lossless size control. It is never quantized or converted
        // to another format; PNG only enters the explicitly authorized resizing phases below.
        RunPhase(states.Where(s => s.Supported && !s.Protected && s.Format != MagickFormat.Png), false, 30, 36, 20);
        RunPhase(states.Where(s => s.Supported && s.Protected && s.Format != MagickFormat.Png), false, 85, 56, 12);
        if (allowResize)
        {
            RunPhase(states.Where(s => s.Supported && !s.Protected), true, 40, 68, 15);
            // Text-like images are resized last, and by at most 30%. Even with permission, this
            // conservative floor can leave a target unreachable rather than silently erase detail.
            RunPhase(states.Where(s => s.Supported && s.Protected), true, 70, 83, 9);
        }

        var changed = states.Where(s => s.Current.Owned).ToArray();
        var resized = changed.Count(s => s.Current.Scale < 100);
        var qualities = changed.Where(s => s.Current.Quality.HasValue).Select(s => s.Current.Quality!.Value).ToArray();
        var description = changed.Length == 0 ? "仅无损优化，未重新编码图片" :
            $"逐图分配预算，{changed.Length} 张图片进行了已授权的有损处理" +
            (qualities.Length > 0 ? $"，编码质量 {qualities.Min()}–{qualities.Max()}" : "") +
            (resized == 0 ? "，所有图片保留像素尺寸" : $"，其中 {resized} 张缩小像素尺寸") +
            $"；其余 {states.Length - changed.Length} 张图片保留原文件或仅无损优化";
        if (states.Any(s => s.Supported && s.Protected))
            description += "；透明图、小图及疑似文字截图优先保护（启发式识别）";
        return new(states.ToDictionary(s => s.Input.Name, s => s.Current.Path, StringComparer.OrdinalIgnoreCase),
            description, skipped.ToArray());

        void RunPhase(IEnumerable<ImageState> eligible, bool resize, int minimum, double start, double span)
        {
            if (estimatedBytes <= targetZipBytes) return;
            var images = eligible.ToArray();
            var floors = new List<(ImageState Image, Candidate Floor)>();
            for (var index = 0; index < images.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var state = images[index];
                var candidate = Encode(state, resize ? ResizeQuality(state) : (uint)minimum, resize ? minimum : 100);
                if (candidate is not null)
                {
                    if (candidate.Bytes < state.Current.Bytes) floors.Add((state, candidate));
                    else DeleteOwned(candidate);
                }
                Report(index, false);
            }
            if (floors.Count == 0) return;

            var floorZipBytes = estimatedBytes - floors.Sum(item => item.Image.Current.Bytes - item.Floor.Bytes);
            var available = Math.Max(0, targetZipBytes - floorZipBytes);
            var budgets = Allocate(floors.Select(item => item.Floor.Bytes).ToArray(),
                floors.Select(item => item.Image.Current.Bytes).ToArray(), available);
            long unused = 0;
            for (var index = 0; index < floors.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var (state, floor) = floors[index];
                var previous = state.Current;
                // Reuse space left by a previous image when a higher quality step would not fit it.
                var budget = Math.Min(previous.Bytes, budgets[index] + unused);
                var chosen = floor;
                if (budget >= previous.Bytes)
                {
                    chosen = previous;
                    DeleteOwned(floor);
                }
                else if (budget > floor.Bytes)
                {
                    var low = minimum + 1;
                    var high = 99;
                    while (low <= high)
                    {
                        ct.ThrowIfCancellationRequested();
                        var value = low + (high - low) / 2;
                        var trial = Encode(state, resize ? ResizeQuality(state) : (uint)value, resize ? value : 100);
                        if (trial is null) break;
                        if (trial.Bytes <= budget)
                        {
                            DeleteOwned(chosen);
                            chosen = trial;
                            low = value + 1;
                        }
                        else
                        {
                            DeleteOwned(trial);
                            high = value - 1;
                        }
                    }
                    // Native encoders are not strictly monotonic. Probe immediately above the
                    // boundary as well; never discard a known-fitting candidate on size alone.
                    var nextValue = resize ? chosen.Scale + 1 : (int)chosen.Quality!.Value + 1;
                    for (var value = nextValue; value <= Math.Min(99, nextValue + 1); value++)
                    {
                        var trial = Encode(state, resize ? ResizeQuality(state) : (uint)value, resize ? value : 100);
                        if (trial is not null && trial.Bytes <= budget)
                        {
                            DeleteOwned(chosen);
                            chosen = trial;
                        }
                        else if (trial is not null) DeleteOwned(trial);
                    }
                }
                unused = Math.Max(0, budgets[index] + unused - chosen.Bytes);
                state.Current = chosen;
                estimatedBytes -= previous.Bytes - chosen.Bytes;
                if (!ReferenceEquals(previous, chosen)) DeleteOwned(previous);
                Report(index, true);
            }

            void Report(int index, bool refining) => progress?.Report(new(
                start + span * ((refining ? 0.35 : 0) + (refining ? 0.65 : 0.35) *
                    (index + 1) / Math.Max(1d, refining ? floors.Count : images.Length)),
                $"{(resize ? "按授权细化图片尺寸" : "逐图细化编码质量")} {index + 1}/{(refining ? floors.Count : images.Length)}"));
        }

        Candidate? Encode(ImageState state, uint quality, int scale)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(workingDirectory, $"{state.Index}-{sequence++}{Path.GetExtension(state.Input.Name)}");
            try
            {
                if (File.GetAttributes(state.Input.Source).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("源图片变为链接，任务已停止。");
                // ALWAYS read the original. Neither the lossless candidate nor a previous lossy
                // result may become the input to a subsequent trial.
                using var image = new MagickImage(state.Input.Source, new MagickReadSettings { Format = state.Format });
                if (scale < 100)
                    image.Resize(Math.Max(1u, (uint)Math.Round(image.Width * scale / 100d)),
                        Math.Max(1u, (uint)Math.Round(image.Height * scale / 100d)));
                if (state.Format != MagickFormat.Png) image.Quality = quality;
                image.Write(path, state.Format);
                ct.ThrowIfCancellationRequested();
                var bytes = CompressedLength(path, ct);
                return new(path, bytes, state.Format == MagickFormat.Png ? null : quality, scale, true);
            }
            catch (MagickException) { skipped.Add(state.Input.Name); File.Delete(path); return null; }
            catch (InvalidDataException) { skipped.Add(state.Input.Name); File.Delete(path); return null; }
        }

        void DeleteOwned(Candidate candidate)
        {
            // Every owned path was constructed above inside this job's private images directory.
            if (candidate.Owned && Path.GetDirectoryName(candidate.Path) == workingDirectory) File.Delete(candidate.Path);
        }
    }

    private static uint ResizeQuality(ImageState state) => state.Protected ? 85u : 50u;

    private static void Inspect(ImageState state, CancellationToken ct)
    {
        state.Format = CompressionService.FormatOf(state.Input.Name);
        using var probe = new MagickImageCollection();
        probe.Ping(state.Input.Source, new MagickReadSettings { Format = state.Format });
        if (probe.Count != 1 || probe[0].Depth > 8 || (ulong)probe[0].Width * probe[0].Height > 100_000_000) return;
        ct.ThrowIfCancellationRequested();
        using var image = new MagickImage(state.Input.Source, new MagickReadSettings { Format = state.Format });
        state.Protected = image.HasAlpha || image.Width < 128 || image.Height < 128;
        if (!state.Protected)
        {
            var ratio = Math.Min(1, 320d / Math.Max(image.Width, image.Height));
            image.Resize(Math.Max(1u, (uint)Math.Round(image.Width * ratio)), Math.Max(1u, (uint)Math.Round(image.Height * ratio)));
            var pixels = image.ToByteArray(MagickFormat.Rgb);
            var colors = new int[512];
            var flat = 0;
            var edge = 0;
            var neighbors = 0;
            var width = (int)image.Width;
            for (var pixel = 0; pixel < pixels.Length / 3; pixel++)
            {
                var offset = pixel * 3;
                colors[((pixels[offset] >> 5) << 6) | ((pixels[offset + 1] >> 5) << 3) | (pixels[offset + 2] >> 5)]++;
                if (pixel % width == 0) continue;
                var difference = Math.Abs(pixels[offset] - pixels[offset - 3]) +
                    Math.Abs(pixels[offset + 1] - pixels[offset - 2]) + Math.Abs(pixels[offset + 2] - pixels[offset - 1]);
                if (difference <= 24) flat++;
                if (difference >= 96) edge++;
                neighbors++;
            }
            var count = pixels.Length / 3d;
            var ordered = colors.OrderDescending().ToArray();
            state.Protected = neighbors > 0 && edge > neighbors * 0.005 && flat > neighbors * 0.45 &&
                (ordered[0] > count * 0.55 || ordered.Take(8).Sum() > count * 0.85);
        }
        state.Supported = true;
        ct.ThrowIfCancellationRequested();
    }

    private static long[] Allocate(long[] minimums, long[] maximums, long extra)
    {
        var budgets = minimums.ToArray();
        var active = Enumerable.Range(0, budgets.Length).Where(i => budgets[i] < maximums[i]).ToList();
        while (extra > 0 && active.Count > 0)
        {
            var weight = active.Sum(i => Math.Max(1d, minimums[i]));
            long assigned = 0;
            var remaining = extra;
            foreach (var index in active)
            {
                var increment = Math.Min(maximums[index] - budgets[index],
                    Math.Min(remaining, Math.Max(1L, (long)Math.Floor(extra * Math.Max(1d, minimums[index]) / weight))));
                budgets[index] += increment;
                assigned += increment;
                remaining -= increment;
            }
            extra -= assigned;
            active.RemoveAll(i => budgets[i] >= maximums[i]);
            if (assigned == 0) break;
        }
        return budgets;
    }

    private static long CompressedLength(string path, CancellationToken ct)
    {
        using var count = new CountingStream();
        using (var deflater = new DeflateStream(count, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var source = File.OpenRead(path))
        {
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = source.Read(buffer)) != 0)
            {
                ct.ThrowIfCancellationRequested();
                deflater.Write(buffer, 0, read);
            }
        }
        ct.ThrowIfCancellationRequested();
        return count.Length;
    }

    private sealed class CountingStream : Stream
    {
        private long length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => length;
        public override long Position { get => length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => length = checked(length + count);
        public override void Write(ReadOnlySpan<byte> buffer) => length = checked(length + buffer.Length);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
