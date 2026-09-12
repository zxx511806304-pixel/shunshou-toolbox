using System.Text;

namespace Shunshou.Core;

/// <summary>A text region in top-left coordinates, in image pixels or PDF points.</summary>
public sealed record TextLayoutBlock(string Text, double X, double Y, double Width, double Height,
    double FontSize = 0, bool IsOcr = true, double? Confidence = null)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

public sealed record OcrImageResult(int Width, int Height, IReadOnlyList<TextLayoutBlock> Blocks)
{
    public string Text => string.Join(Environment.NewLine, Blocks.Select(x => x.Text));
    public int ReviewCount => Blocks.Count(x => x.Confidence is < 0.8);
}

internal sealed record TextLayoutPage(double Width, double Height, IReadOnlyList<TextLayoutBlock> Blocks);

internal static class TextLayout
{
    // Keep genuine repetitions at different positions. Only overlapping renderings
    // are candidates for deduplication; native text wins over OCR at the same place.
    public static IReadOnlyList<TextLayoutBlock> Merge(IEnumerable<TextLayoutBlock> input)
    {
        var result = new List<TextLayoutBlock>();
        foreach (var block in input.Where(x => !string.IsNullOrWhiteSpace(x.Text) && x.Width > 0 && x.Height > 0)
                     .OrderBy(x => x.IsOcr).ThenByDescending(x => x.Confidence ?? 1))
        {
            if (result.Any(existing => IsDuplicate(existing, block))) continue;
            result.Add(block);
        }
        return Order(result);
    }

    private static bool IsDuplicate(TextLayoutBlock a, TextLayoutBlock b)
    {
        double overlapX = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X));
        double overlapY = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y));
        double overlap = overlapX * overlapY / Math.Max(1, Math.Min(a.Width * a.Height, b.Width * b.Height));
        if (overlap < 0.55) return false;
        string left = Normalize(a.Text), right = Normalize(b.Text);
        if (left.Length == 0 || right.Length == 0) return false;
        if (left == right) return true;
        // A reliable native line replaces the OCR of that line even if OCR has a
        // few mistakes. Do not discard OCR that spans substantially more content.
        if (!a.IsOcr && b.IsOcr && overlap >= 0.7 && b.Width <= a.Width * 1.3 && b.Height <= a.Height * 1.5
            && Math.Min(left.Length, right.Length) >= Math.Max(left.Length, right.Length) * 0.7)
            return Similarity(left, right) >= 0.65;
        return false;
    }

    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static double Similarity(string a, string b)
    {
        if (a.Length > 1000 || b.Length > 1000) return 0; // Bound work for pathological text streams.
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        var current = new int[b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (current, previous) = (previous, current);
        }
        return 1 - (double)previous[b.Length] / Math.Max(a.Length, b.Length);
    }

    public static IReadOnlyList<TextLayoutBlock> Order(IEnumerable<TextLayoutBlock> source)
    {
        var blocks = source.ToList();
        var result = new List<TextLayoutBlock>(blocks.Count);
        OrderRegion(blocks, result, 0);
        return result;
    }

    private static void OrderRegion(List<TextLayoutBlock> blocks, List<TextLayoutBlock> output, int depth)
    {
        if (blocks.Count < 2 || depth > 24) { output.AddRange(blocks.OrderBy(x => x.Y).ThenBy(x => x.X)); return; }
        double height = blocks.Select(x => x.Height / Math.Max(1, x.Text.Count(c => c == '\n') + 1)).Order().ElementAt(blocks.Count / 2);
        // Large horizontal whitespace separates headings and footers from columns.
        if (TrySplit(blocks, vertical: false, Math.Max(10, height * 1.8), out var first, out var second)
            || TrySplit(blocks, vertical: true, Math.Max(14, height * 1.2), out first, out second))
        {
            OrderRegion(first, output, depth + 1); OrderRegion(second, output, depth + 1); return;
        }
        // Group nearby baselines using text height, not a fixed pixel grid.
        var pending = blocks.OrderBy(x => x.Y).ToList();
        while (pending.Count > 0)
        {
            var anchor = pending[0];
            var row = pending.Where(x => Math.Abs((x.Y + x.Height / 2) - (anchor.Y + anchor.Height / 2))
                <= Math.Min(x.Height, anchor.Height) * 0.45).OrderBy(x => x.X).ToArray();
            foreach (var item in row) { output.Add(item); pending.Remove(item); }
        }
    }

    private static bool TrySplit(List<TextLayoutBlock> blocks, bool vertical, double minimum,
        out List<TextLayoutBlock> first, out List<TextLayoutBlock> second)
    {
        var sorted = blocks.OrderBy(x => vertical ? x.X : x.Y).ToArray();
        double end = vertical ? sorted[0].Right : sorted[0].Bottom;
        double best = minimum, split = double.NaN;
        for (int i = 1; i < sorted.Length; i++)
        {
            double start = vertical ? sorted[i].X : sorted[i].Y;
            if (start - end > best) { best = start - end; split = (start + end) / 2; }
            end = Math.Max(end, vertical ? sorted[i].Right : sorted[i].Bottom);
        }
        first = []; second = [];
        if (double.IsNaN(split)) return false;
        first = blocks.Where(x => (vertical ? x.X : x.Y) < split).ToList();
        second = blocks.Where(x => (vertical ? x.X : x.Y) >= split).ToList();
        return first.Count > 0 && second.Count > 0;
    }

    public static string JoinLines(IEnumerable<string> lines)
    {
        var result = new StringBuilder();
        foreach (string line in lines.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            string next = line.Trim();
            if (result.Length > 0 && result[^1] < 0x2E80 && next[0] < 0x2E80) result.Append(' ');
            result.Append(next);
        }
        return result.ToString();
    }
}
