using System.Text;

namespace Pia.Services.Search;

/// <summary>
/// Turns free text into an FTS5 <c>MATCH</c> expression. Callers choose the shape: AND for typeahead,
/// where every typed word must appear, OR for ranked search, where a question's rarest word should
/// still find the page.
/// </summary>
public static class FtsQueryBuilder
{
    /// <summary>
    /// Lowercase, then keep only letters and digits. Lowercasing also neutralises FTS5's uppercase
    /// AND/OR/NOT operators, which a trailing <c>*</c> would otherwise turn into a syntax error.
    /// </summary>
    public static string SanitizeToken(string token)
    {
        var sb = new StringBuilder(token.Length);
        foreach (var ch in token)
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>Splits on every non-alphanumeric, so "text-to-speech:" yields three searchable words.</summary>
    public static IReadOnlyList<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else if (sb.Length > 0)
            {
                tokens.Add(sb.ToString());
                sb.Clear();
            }
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens;
    }

    /// <summary>
    /// Whitespace-split prefix terms, implicitly ANDed. Quoting each token would make it an exact-token
    /// phrase query, so a partially typed word would never match.
    /// </summary>
    public static string PrefixAnd(string text)
    {
        var tokens = text
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SanitizeToken)
            .Where(t => t.Length > 0)
            .Select(t => t + "*");
        return string.Join(' ', tokens);
    }

    /// <summary>Prefix terms joined with OR, for a ranked search where partial overlap is still a hit.</summary>
    public static string PrefixOr(IEnumerable<string> tokens)
    {
        var terms = tokens
            .Select(SanitizeToken)
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Select(t => t + "*");
        return string.Join(" OR ", terms);
    }
}
