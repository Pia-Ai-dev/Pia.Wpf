using Pia.Models;

namespace Pia.Infrastructure;

/// <summary>
/// Hand-rolled LCS (longest-common-subsequence) line diff. Minimal-deps: no external
/// diff library. Produces a flat sequence of <see cref="DiffLine"/>s tagged Removed
/// (only in old), Added (only in new), or Context (common). Used to render the
/// write_file approval card's old→new preview.
/// </summary>
public static class LineDiff
{
    private const int MaxDiffLines = 400; // cap rendered rows so a huge file can't flood the card

    /// <summary>
    /// Computes a line-level diff of <paramref name="oldText"/> → <paramref name="newText"/>.
    /// A null/empty <paramref name="oldText"/> (new file) yields an all-Added result.
    /// The output is capped at a sane number of rows with a trailing context marker.
    /// </summary>
    public static IReadOnlyList<DiffLine> Compute(string? oldText, string newText)
    {
        var oldLines = SplitLines(oldText ?? string.Empty, isEmptySource: string.IsNullOrEmpty(oldText));
        var newLines = SplitLines(newText ?? string.Empty, isEmptySource: string.IsNullOrEmpty(newText));

        var result = new List<DiffLine>();

        // LCS via the classic DP table. Line counts here are bounded by the write cap
        // (512K chars), so an O(n*m) table is acceptable for typical files; guard the
        // worst case by falling back to a plain replace block when either side is huge.
        if ((long)oldLines.Length * newLines.Length > 4_000_000L)
        {
            foreach (var l in oldLines) Add(result, new DiffLine(DiffLineKind.Removed, l));
            foreach (var l in newLines) Add(result, new DiffLine(DiffLineKind.Added, l));
            return Trim(result);
        }

        int n = oldLines.Length, m = newLines.Length;
        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                dp[i, j] = oldLines[i] == newLines[j]
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (oldLines[x] == newLines[y])
            {
                Add(result, new DiffLine(DiffLineKind.Context, oldLines[x]));
                x++; y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                Add(result, new DiffLine(DiffLineKind.Removed, oldLines[x]));
                x++;
            }
            else
            {
                Add(result, new DiffLine(DiffLineKind.Added, newLines[y]));
                y++;
            }
        }
        while (x < n) Add(result, new DiffLine(DiffLineKind.Removed, oldLines[x++]));
        while (y < m) Add(result, new DiffLine(DiffLineKind.Added, newLines[y++]));

        return Trim(result);
    }

    private static void Add(List<DiffLine> result, DiffLine line)
    {
        if (result.Count <= MaxDiffLines) result.Add(line);
    }

    private static IReadOnlyList<DiffLine> Trim(List<DiffLine> result)
    {
        if (result.Count > MaxDiffLines)
        {
            result.RemoveRange(MaxDiffLines, result.Count - MaxDiffLines);
            result.Add(new DiffLine(DiffLineKind.Context, $"… (diff truncated at {MaxDiffLines} lines)"));
        }
        return result;
    }

    /// <summary>
    /// Splits on LF, strips a trailing CR per line (so EOL style never affects the diff), and
    /// drops the spurious empty final element a trailing newline produces. An empty source
    /// yields zero lines (so a brand-new file diffs as all-Added with no phantom blank line).
    /// </summary>
    private static string[] SplitLines(string text, bool isEmptySource)
    {
        if (isEmptySource) return [];
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].EndsWith('\r')) lines[i] = lines[i][..^1];
        if (lines.Length > 0 && lines[^1].Length == 0 && text.EndsWith('\n'))
            return lines[..^1];
        return lines;
    }
}
