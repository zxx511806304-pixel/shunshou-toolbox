namespace Shunshou.Core;

public enum DiffKind { Same, Added, Removed }
public sealed record DiffLine(int? Left, int? Right, DiffKind Kind, string Text);
public sealed record DiffResult(IReadOnlyList<DiffLine> Lines, int Same, int Added, int Removed, bool Approximate)
{
    public bool Identical => Added == 0 && Removed == 0;
}

/// <summary>Line-based comparison for contracts, lists and homework. Everything stays on this computer.</summary>
public static class TextDiffService
{
    private const int MaxLinesPerSide = 20_000;
    private const long MaxBytesPerSide = 4_000_000;
    private const long MaxWorkCells = 4_000_000;

    public static DiffResult Compare(string? left, string? right, bool ignoreWhitespace = false, bool ignoreCase = false)
    {
        string leftText = left ?? "";
        string rightText = right ?? "";
        Check(leftText, nameof(left), "第一份");
        Check(rightText, nameof(right), "第二份");
        var leftLines = Split(leftText);
        var rightLines = Split(rightText);
        Func<string, string> normalize = line => ignoreCase
            ? (ignoreWhitespace ? Collapse(line) : line).ToUpperInvariant()
            : ignoreWhitespace ? Collapse(line) : line;

        int prefix = 0;
        while (prefix < leftLines.Count && prefix < rightLines.Count &&
               normalize(leftLines[prefix]) == normalize(rightLines[prefix])) prefix++;
        int suffix = 0;
        while (suffix < leftLines.Count - prefix && suffix < rightLines.Count - prefix &&
               normalize(leftLines[^(suffix + 1)]) == normalize(rightLines[^(suffix + 1)])) suffix++;

        var leftMiddle = leftLines.GetRange(prefix, leftLines.Count - prefix - suffix);
        var rightMiddle = rightLines.GetRange(prefix, rightLines.Count - prefix - suffix);
        var result = new List<DiffLine>();
        for (int i = 0; i < prefix; i++) result.Add(new(i + 1, i + 1, DiffKind.Same, leftLines[i]));

        int same = prefix + suffix, added, removed;
        bool approximate = false;
        if ((long)leftMiddle.Count * rightMiddle.Count > MaxWorkCells && leftMiddle.Count > 0 && rightMiddle.Count > 0)
        {
            // Keep the answer honest instead of freezing the window on a huge comparison.
            approximate = true;
            removed = leftMiddle.Count;
            added = rightMiddle.Count;
            for (int i = 0; i < leftMiddle.Count; i++) result.Add(new(prefix + i + 1, null, DiffKind.Removed, leftMiddle[i]));
            for (int i = 0; i < rightMiddle.Count; i++) result.Add(new(null, prefix + i + 1, DiffKind.Added, rightMiddle[i]));
        }
        else
        {
            int[,] table = new int[leftMiddle.Count + 1, rightMiddle.Count + 1];
            for (int i = leftMiddle.Count - 1; i >= 0; i--)
                for (int j = rightMiddle.Count - 1; j >= 0; j--)
                    table[i, j] = normalize(leftMiddle[i]) == normalize(rightMiddle[j])
                        ? table[i + 1, j + 1] + 1
                        : Math.Max(table[i + 1, j], table[i, j + 1]);
            added = removed = 0;
            int a = 0, b = 0;
            while (a < leftMiddle.Count && b < rightMiddle.Count)
            {
                if (normalize(leftMiddle[a]) == normalize(rightMiddle[b]))
                {
                    result.Add(new(prefix + a + 1, prefix + b + 1, DiffKind.Same, leftMiddle[a]));
                    same++; a++; b++;
                }
                else if (table[a + 1, b] >= table[a, b + 1])
                {
                    result.Add(new(prefix + a + 1, null, DiffKind.Removed, leftMiddle[a])); removed++; a++;
                }
                else
                {
                    result.Add(new(null, prefix + b + 1, DiffKind.Added, rightMiddle[b])); added++; b++;
                }
            }
            while (a < leftMiddle.Count) { result.Add(new(prefix + a + 1, null, DiffKind.Removed, leftMiddle[a])); removed++; a++; }
            while (b < rightMiddle.Count) { result.Add(new(null, prefix + b + 1, DiffKind.Added, rightMiddle[b])); added++; b++; }
        }
        for (int i = 0; i < suffix; i++)
            result.Add(new(leftLines.Count - suffix + i + 1, rightLines.Count - suffix + i + 1, DiffKind.Same, leftLines[leftLines.Count - suffix + i]));
        return new(result, same, added, removed, approximate);
    }

    private static void Check(string text, string argument, string label)
    {
        if (text.Length > MaxBytesPerSide) throw new ArgumentException($"{label}文本超过 {MaxBytesPerSide / 1_000_000} MB，请分段对比。", argument);
        if (CountLines(text) > MaxLinesPerSide) throw new ArgumentException($"{label}文本超过 {MaxLinesPerSide:N0} 行，请分段对比。", argument);
    }

    private static int CountLines(string text)
    {
        int count = 1;
        foreach (char c in text) if (c == '\n') count++;
        return count;
    }

    private static List<string> Split(string text)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n' && text[i] != '\r') continue;
            lines.Add(text[start..i].TrimEnd());
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }
        lines.Add(text[start..].TrimEnd());
        if (lines.Count > 1 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static string Collapse(string line) => string.Join(' ', line.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries));
}
