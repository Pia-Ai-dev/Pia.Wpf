using Pia.Models;

namespace Pia.Infrastructure;

/// <summary>
/// Hand-rolled LCS (longest-common-subsequence) line diff. Minimal-deps: no external diff library.
/// Produces a flat sequence of <see cref="DiffLine"/>s tagged Removed (only in old), Added (only in
/// new), or Context (common), each carrying its 1-based old/new line number. Common leading/trailing
/// lines are stripped before the O(n*m) table, so a small edit in a large file stays cheap and diffs
/// as a tiny change surrounded by context (instead of degenerating into an all-removed/all-added
/// replacement). The full, uncapped diff is returned; folding and row-limiting for display are the
/// caller's concern — see <c>DiffHunkBuilder</c>, which folds context first so a change anywhere in a
/// large file survives, then bounds the visible rows.
/// </summary>
public static class LineDiff
{
    // Skip the O(n*m) table when the *differing* middle (after common prefix/suffix stripping) is huge
    // on both sides, falling back to a plain replace block. This only bites a genuinely large,
    // genuinely different edit; near-identical large files are handled cheaply by the stripping.
    private const long MaxDpCells = 4_000_000L;

    /// <summary>
    /// Computes a line-level diff of <paramref name="oldText"/> → <paramref name="newText"/>.
    /// A null/empty <paramref name="oldText"/> (new file) yields an all-Added result.
    /// </summary>
    public static IReadOnlyList<DiffLine> Compute(string? oldText, string newText)
    {
        var oldLines = SplitLines(oldText ?? string.Empty, isEmptySource: string.IsNullOrEmpty(oldText));
        var newLines = SplitLines(newText ?? string.Empty, isEmptySource: string.IsNullOrEmpty(newText));

        var result = new List<DiffLine>();

        // Common prefix: identical leading lines share the same old/new number.
        int prefix = 0;
        int maxCommon = Math.Min(oldLines.Length, newLines.Length);
        while (prefix < maxCommon && oldLines[prefix] == newLines[prefix])
        {
            result.Add(new DiffLine(DiffLineKind.Context, oldLines[prefix], prefix + 1, prefix + 1));
            prefix++;
        }

        // Common suffix: count identical trailing lines not already claimed by the prefix.
        int suffix = 0;
        while (suffix < oldLines.Length - prefix
            && suffix < newLines.Length - prefix
            && oldLines[oldLines.Length - 1 - suffix] == newLines[newLines.Length - 1 - suffix])
        {
            suffix++;
        }

        // The differing middle (may be empty on either side).
        DiffMiddle(result, oldLines, newLines, prefix,
            oldLines.Length - suffix - prefix, newLines.Length - suffix - prefix);

        // Common suffix: emitted in file order. Old/new numbers differ because the middle changed the
        // line offset between the two files.
        for (int k = 0; k < suffix; k++)
        {
            int oi = oldLines.Length - suffix + k;
            int ni = newLines.Length - suffix + k;
            result.Add(new DiffLine(DiffLineKind.Context, oldLines[oi], oi + 1, ni + 1));
        }

        return result;
    }

    /// <summary>
    /// Diffs the differing middle slice — <c>old[offset, offset+n)</c> vs <c>new[offset, offset+m)</c> —
    /// appending the rows with their 1-based absolute line numbers (offset already applied).
    /// </summary>
    private static void DiffMiddle(List<DiffLine> result, string[] oldLines, string[] newLines,
        int offset, int n, int m)
    {
        if (n <= 0 && m <= 0) return;

        // Guard the O(n*m) table for a genuinely huge, genuinely different middle.
        if ((long)n * m > MaxDpCells)
        {
            for (int i = 0; i < n; i++)
                result.Add(new DiffLine(DiffLineKind.Removed, oldLines[offset + i], offset + i + 1, null));
            for (int j = 0; j < m; j++)
                result.Add(new DiffLine(DiffLineKind.Added, newLines[offset + j], null, offset + j + 1));
            return;
        }

        // LCS via the classic DP table over the middle only.
        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                dp[i, j] = oldLines[offset + i] == newLines[offset + j]
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        // x/y are 0-based cursors into the middle; the emitted numbers are 1-based absolute (offset + cursor + 1).
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (oldLines[offset + x] == newLines[offset + y])
            {
                result.Add(new DiffLine(DiffLineKind.Context, oldLines[offset + x], offset + x + 1, offset + y + 1));
                x++; y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                result.Add(new DiffLine(DiffLineKind.Removed, oldLines[offset + x], offset + x + 1, null));
                x++;
            }
            else
            {
                result.Add(new DiffLine(DiffLineKind.Added, newLines[offset + y], null, offset + y + 1));
                y++;
            }
        }
        while (x < n) { result.Add(new DiffLine(DiffLineKind.Removed, oldLines[offset + x], offset + x + 1, null)); x++; }
        while (y < m) { result.Add(new DiffLine(DiffLineKind.Added, newLines[offset + y], null, offset + y + 1)); y++; }
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
