using System.Globalization;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace Shunshou.Core;

public sealed record WatermarkCandidate(VideoRegion Region, string Label, double Confidence, int MatchedFrames)
{
    public override string ToString() => $"{Label} · {MatchedFrames} 帧 · {(int)(Confidence * 100)}%";
}

/// <summary>Conservative multi-frame candidates. Detection never edits the input video.</summary>
public sealed class WatermarkDetectionService(string? ffmpegDirectory = null, string? ocrDirectory = null)
{
    public async Task<IReadOnlyList<WatermarkCandidate>> DetectAsync(string input, VideoInspection inspection,
        IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        if (!File.Exists(input) || inspection.Duration <= 0) throw new ArgumentException("请选择有效的本地视频。");
        string engine = ffmpegDirectory ?? Environment.GetEnvironmentVariable("SHUNSHOU_FFMPEG_DIR") ?? Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "bin");
        string job = Path.Combine(Path.GetTempPath(), "Shunshou", "watermark-detect", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        try
        {
            var ocr = new OcrService(ocrDirectory);
            using var session = ocr.CreateSession();
            var samples = new List<IReadOnlyList<TextLayoutBlock>>();
            var frames = new List<SKBitmap>();
            int sampleWidth = 0, sampleHeight = 0;
            try
            {
                for (int i = 0; i < 5; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    double time = Math.Min(Math.Max(0, inspection.Duration - .1), inspection.Duration * (0.08 + .2 * i));
                    string frame = Path.Combine(job, $"frame-{i}.png");
                    progress?.Report(new(5 + i * 17, $"自动识别水印 · 分析第 {i + 1} / 5 帧"));
                    await VideoDownloadService.RunAsync(Path.Combine(engine, "ffmpeg.exe"),
                        ["-v", "error", "-nostdin", "-y", "-ss", time.ToString("0.###", CultureInfo.InvariantCulture), "-protocol_whitelist", "file,pipe", "-i", input,
                         "-map", "0:V:0", "-vf", "scale=ceil(iw*sar):ih,setsar=1,scale=w='min(1280,iw)':h=-1", "-frames:v", "1", "-an", "-update", "1", frame], job, ct);
                    var bitmap = SKBitmap.Decode(frame) ?? throw new InvalidDataException("无法读取视频采样帧。");
                    frames.Add(bitmap); sampleWidth = bitmap.Width; sampleHeight = bitmap.Height;
                    var result = await ocr.RecognizeLayoutAsync(frame, session, ct).ConfigureAwait(false);
                    samples.Add(result.Blocks);
                }
                var candidates = FindTextCandidates(samples, sampleWidth, sampleHeight).ToList();
                candidates.AddRange(FindStaticLogos(frames).Where(c => !candidates.Any(t => Overlap(c.Region, t.Region) > .15)));
                double sx = (double)inspection.Width / sampleWidth, sy = (double)inspection.Height / sampleHeight;
                var resultCandidates = candidates.OrderByDescending(c => c.Confidence).Take(8).Select(c =>
                {
                    int x = Math.Clamp((int)Math.Floor(c.Region.X * sx), 2, inspection.Width - 4);
                    int y = Math.Clamp((int)Math.Floor(c.Region.Y * sy), 2, inspection.Height - 4);
                    int right = Math.Clamp((int)Math.Ceiling((c.Region.X + c.Region.Width) * sx), x + 2, inspection.Width - 2);
                    int bottom = Math.Clamp((int)Math.Ceiling((c.Region.Y + c.Region.Height) * sy), y + 2, inspection.Height - 2);
                    return c with { Region = new(x, y, right - x, bottom - y) };
                }).ToArray();
                progress?.Report(new(100, resultCandidates.Length == 0 ? "未找到可靠的水印候选，可手动框选" : $"找到 {resultCandidates.Length} 处候选，确认选区后可预览"));
                return resultCandidates;
            }
            finally { foreach (var frame in frames) frame.Dispose(); }
        }
        finally { try { Directory.Delete(job, true); } catch (IOException) { } }
    }

    public static IReadOnlyList<WatermarkCandidate> FindTextCandidates(IReadOnlyList<IReadOnlyList<TextLayoutBlock>> samples, int width, int height)
    {
        var groups = new List<List<(TextLayoutBlock Block, int Frame)>>();
        for (int frame = 0; frame < samples.Count; frame++)
        foreach (var block in samples[frame])
        {
            string text = Normalize(block.Text);
            if (text.Length < 2 || block.Confidence is < .65 || block.Width > width * .5 || block.Height > height * .2) continue;
            bool nearEdge = block.Y < height * .23 || block.Bottom > height * .77 || block.X < width * .15 || block.Right > width * .85;
            if (!nearEdge) continue;
            // Ordinary bottom-centred captions should not be treated as watermarks.
            bool brand = Regex.IsMatch(text, "抖音|小红书|快手|bilibili|douyin|xiaohongshu|shunshou|watermark|logo|原创|作者|拍摄|版权所有|ID", RegexOptions.IgnoreCase) || block.Text.Contains('@');
            if (block.Y > height * .62 && block.X > width * .18 && block.Right < width * .82 && !brand) continue;
            var group = groups.FirstOrDefault(g => !g.Any(x => x.Frame == frame) &&
                Normalize(g[0].Block.Text) == text && Overlap(ToRegion(g[0].Block), ToRegion(block)) > .4);
            if (group is null) { group = []; groups.Add(group); }
            group.Add((block, frame));
        }
        return groups.Where(g => g.Count >= Math.Max(3, (int)Math.Ceiling(samples.Count * .6))).Select(g =>
        {
            int x = Math.Max(2, (int)g.Min(b => b.Block.X) - 8), y = Math.Max(2, (int)g.Min(b => b.Block.Y) - 6);
            int right = Math.Min(width - 2, (int)Math.Ceiling(g.Max(b => b.Block.Right)) + 8), bottom = Math.Min(height - 2, (int)Math.Ceiling(g.Max(b => b.Block.Bottom)) + 6);
            return new WatermarkCandidate(new(x, y, Math.Max(2, right - x), Math.Max(2, bottom - y)), g[0].Block.Text,
                Math.Min(.97, .65 + .06 * g.Count), g.Count);
        }).ToArray();
    }

    private static IEnumerable<WatermarkCandidate> FindStaticLogos(IReadOnlyList<SKBitmap> frames)
    {
        // Persistent bright edges on changing corner backgrounds. Static slides are
        // deliberately skipped: their titles and graphics are indistinguishable here.
        const int step = 3;
        int w = frames[0].Width, h = frames[0].Height, gw = w / step, gh = h / step;
        var stable = new bool[gw * gh]; int changing = 0, pixels = 0;
        for (int y = 1; y < gh - 1; y++) for (int x = 1; x < gw - 1; x++)
        {
            int px = x * step, py = y * step;
            var colors = frames.Select(f => f.GetPixel(px, py)).ToArray();
            float[] luma = colors.Select(c => (c.Red + c.Green + c.Blue) / 3f).ToArray();
            double mean = luma.Average(), spread = luma.Max() - luma.Min();
            pixels++; if (spread > 25) changing++;
            if (!(px < w * .28 || px > w * .72) || !(py < h * .26 || py > h * .74)) continue;
            if (mean < 170 || spread > 13) continue;
            int edgeFrames = frames.Count(f => Math.Abs(Luma(f.GetPixel(px, py)) - Luma(f.GetPixel(px + step, py))) > 24 ||
                Math.Abs(Luma(f.GetPixel(px, py)) - Luma(f.GetPixel(px, py + step))) > 24);
            stable[y * gw + x] = edgeFrames >= 4;
        }
        if (changing < pixels * .05) yield break;
        var joined = new bool[stable.Length];
        for (int y = 2; y < gh - 2; y++) for (int x = 2; x < gw - 2; x++)
            if (stable[y * gw + x]) for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++) joined[(y + dy) * gw + x + dx] = true;
        for (int start = 0; start < joined.Length; start++)
        {
            if (!joined[start]) continue;
            var queue = new Queue<int>(); queue.Enqueue(start); joined[start] = false;
            int left = gw, top = gh, right = 0, bottom = 0, hits = 0;
            while (queue.Count > 0)
            {
                int p = queue.Dequeue(), x = p % gw, y = p / gw;
                left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); if (stable[p]) hits++;
                foreach (int n in new[] { p - 1, p + 1, p - gw, p + gw }) if (n >= 0 && n < joined.Length && Math.Abs(n % gw - x) <= 1 && joined[n]) { joined[n] = false; queue.Enqueue(n); }
            }
            int cw = (right - left + 1) * step, ch = (bottom - top + 1) * step;
            if (hits < 10 || cw < 18 || ch < 15 || cw > w * .27 || ch > h * .22) continue;
            yield return new(new(left * step, top * step, cw, ch), "固定图形候选", .7, frames.Count);
        }
    }
    private static float Luma(SKColor c) => (c.Red + c.Green + c.Blue) / 3f;
    private static string Normalize(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static VideoRegion ToRegion(TextLayoutBlock b) => new((int)b.X, (int)b.Y, (int)b.Width, (int)b.Height);
    private static double Overlap(VideoRegion a, VideoRegion b) => Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X)) *
        (double)Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y)) / Math.Max(1, Math.Min(a.Width * a.Height, b.Width * b.Height));
}
