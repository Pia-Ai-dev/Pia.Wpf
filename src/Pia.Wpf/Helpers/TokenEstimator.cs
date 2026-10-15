namespace Pia.Helpers;

/// <summary>A conservative token estimate from character runs, so numbers and CJK cost what tokenizers charge.</summary>
public static class TokenEstimator
{
    // Calibrated on TestData/TokenCorpus: the PDF-table sample allows nothing below 1.0 within 5 % of o200k.
    internal const double LetterWeight = 1.0;

    public static int Estimate(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(Raw(text.AsSpan()));

    /// <summary>The longest prefix within <paramref name="maxTokens"/>, cut at a line break unless whole lines would fill less than half of it.</summary>
    public static TextCut CutToBudget(string text, int maxTokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTokens);

        var lines = text.Split('\n');
        var total = lines.Length > 1 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;

        // Whole-text check first: per-line costs charge a CRLF's '\r' separately, so a text granted exactly
        // its own Estimate would otherwise lose its last lines.
        if (Raw(text.AsSpan()) <= maxTokens) return new TextCut(text, total, total, EndsMidLine: false);

        double used = 0;
        var end = 0;
        var shown = 0;
        while (shown < total)
        {
            var cost = Raw(lines[shown].AsSpan()) + (shown == 0 ? 0 : 1);
            if (used + cost > maxTokens) break;
            used += cost;
            end += (shown == 0 ? 0 : 1) + lines[shown].Length;
            shown++;
        }

        if (shown == total) return new TextCut(text, total, total, EndsMidLine: false);

        // Otherwise a blank line or a banner before one huge line (a minified file, an export) stands in for the file.
        if (used * 2 < maxTokens)
        {
            var separator = shown == 0 ? 0 : 1;
            var keep = PrefixWithin(lines[shown], maxTokens - used - separator);
            return new TextCut(text[..(end + separator + keep)], shown + 1, total, EndsMidLine: true);
        }

        return new TextCut(text[..end], shown, total, EndsMidLine: false);
    }

    private static int PrefixWithin(string line, double maxTokens)
    {
        int lo = 0, hi = line.Length;
        while (lo < hi)
        {
            var mid = lo + (hi - lo + 1) / 2;
            if (Raw(line.AsSpan(0, mid)) <= maxTokens) lo = mid;
            else hi = mid - 1;
        }
        return lo > 0 && char.IsHighSurrogate(line[lo - 1]) ? lo - 1 : lo;
    }

    private static double Raw(ReadOnlySpan<char> text)
    {
        double tokens = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (IsCjk(c))
            {
                tokens += 1;
                i++;
            }
            else if (char.IsLetter(c))
            {
                var start = i;
                while (i < text.Length && char.IsLetter(text[i]) && !IsCjk(text[i])) i++;
                tokens += LetterWeight * Math.Ceiling((i - start) / 4.0);
            }
            else if (char.IsDigit(c))
            {
                var start = i;
                while (i < text.Length && char.IsDigit(text[i])) i++;
                tokens += Math.Ceiling((i - start) / 3.0);
            }
            else if (c == ' ' && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]))
            {
                // A single space rides on the token after it.
                i++;
            }
            else if (char.IsWhiteSpace(c))
            {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                tokens += 1;
            }
            else
            {
                var start = i;
                while (i < text.Length && !char.IsLetterOrDigit(text[i]) && !char.IsWhiteSpace(text[i]) && !IsCjk(text[i])) i++;
                tokens += Math.Ceiling((i - start) / 2.0);
            }
        }
        return tokens;
    }

    private static bool IsCjk(char c) =>
        c is (>= '\u3040' and <= '\u30FF') or (>= '\u3400' and <= '\u4DBF') or (>= '\u4E00' and <= '\u9FFF')
            or (>= '\uAC00' and <= '\uD7AF') or (>= '\uF900' and <= '\uFAFF') or (>= '\uFF00' and <= '\uFFEF');
}

public readonly record struct TextCut(string Text, int ShownLines, int TotalLines, bool EndsMidLine)
{
    public bool Truncated => EndsMidLine || ShownLines < TotalLines;
}
