namespace Pia.Services.Similarity;

/// <summary>
/// Classic edit-distance (insert/delete/substitute, each cost 1) between two strings, compared
/// ordinally (no culture-aware casing/collation — callers that need case-insensitivity must lowercase
/// first, exactly like <c>NamedConsentClassifier</c> does before calling <see cref="WithinOne"/>).
/// </summary>
public static class Levenshtein
{
    /// <summary>
    /// Edit distance between <paramref name="a"/> and <paramref name="b"/>, computed with a two-row
    /// dynamic-programming table (O(min(len)) memory instead of the full O(len*len) matrix).
    /// Null/empty safe: a null argument is treated as an empty string.
    /// </summary>
    public static int Distance(string? a, string? b)
    {
        a ??= string.Empty;
        b ??= string.Empty;

        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        // Keep the shorter string as the "row" so the working arrays are as small as possible.
        if (a.Length > b.Length)
            (a, b) = (b, a);

        var previousRow = new int[a.Length + 1];
        var currentRow = new int[a.Length + 1];

        for (var i = 0; i <= a.Length; i++)
            previousRow[i] = i;

        for (var j = 1; j <= b.Length; j++)
        {
            currentRow[0] = j;
            for (var i = 1; i <= a.Length; i++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var deletion = previousRow[i] + 1;
                var insertion = currentRow[i - 1] + 1;
                var substitution = previousRow[i - 1] + cost;
                currentRow[i] = Math.Min(Math.Min(deletion, insertion), substitution);
            }

            (previousRow, currentRow) = (currentRow, previousRow);
        }

        return previousRow[a.Length];
    }

    /// <summary>
    /// True when <see cref="Distance"/> is 0 or 1. Early-exits on a length gap greater than 1 (which
    /// alone guarantees a distance &gt;= 2) before paying for the DP table.
    /// </summary>
    public static bool WithinOne(string? a, string? b)
    {
        a ??= string.Empty;
        b ??= string.Empty;

        if (Math.Abs(a.Length - b.Length) > 1)
            return false;

        return Distance(a, b) <= 1;
    }
}
