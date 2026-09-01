using System.Text.RegularExpressions;

namespace Pia.Infrastructure;

/// <summary>
/// A small, hand-rolled matcher for a practical subset of <c>.gitignore</c> syntax, used to keep
/// build/VCS/dependency noise out of the file tools and the <c>@Files</c> picker. Not a full gitignore
/// implementation (no nested per-directory ignore files), but it covers what shipped defaults and a
/// typical user/repo ignore file need: comments, blank lines, <c>!</c> negation (last-match-wins),
/// leading-<c>/</c> anchoring, trailing-<c>/</c> directory-only patterns, the <c>*</c> / <c>?</c> /
/// <c>**</c> wildcards, and <c>[...]</c> / <c>[!...]</c> character classes (so the stock
/// VisualStudio <c>.gitignore</c>, built from <c>[Dd]ebug/</c> / <c>[Rr]elease/</c>, works).
/// <para>
/// Paths handed to <see cref="IsIgnored"/> must be relative to the SAME directory the rules were
/// read from (forward-slash separators), so anchored patterns line up. Matching is case-insensitive
/// to mirror the Windows filesystem and the ordinal-ignore-case comparisons used elsewhere in the
/// file tools. Rule regexes are compiled with <see cref="RegexOptions.NonBacktracking"/> — the
/// translated subset uses only character classes and non-capturing groups, so this is fully
/// compatible and guarantees linear-time matching (no catastrophic backtracking from a hostile
/// ignore line, which would otherwise freeze the synchronous <c>@Files</c> enumeration).
/// </para>
/// </summary>
public sealed class GitignoreMatcher
{
    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    private sealed record Rule(Regex Regex, bool IsNegation, bool DirOnly);

    private readonly IReadOnlyList<Rule> _rules;

    private GitignoreMatcher(IReadOnlyList<Rule> rules) => _rules = rules;

    /// <summary>
    /// True when no usable patterns were parsed (every line was blank/comment/invalid). Exposed for
    /// tests — it distinguishes "all lines dropped by the parser" from "rules parsed but non-matching".
    /// </summary>
    public bool IsEmpty => _rules.Count == 0;

    /// <summary>Builds a matcher from raw ignore-file lines (order-significant for negation).</summary>
    public static GitignoreMatcher FromLines(IEnumerable<string> lines)
    {
        return new GitignoreMatcher(lines.Select(ParseLine).OfType<Rule>().ToList());
    }

    /// <summary>
    /// True when <paramref name="relativePath"/> (relative to the ignore root, any separator) is
    /// excluded. Last matching rule wins, so a later <c>!pattern</c> re-includes an earlier match.
    /// Directory-only rules apply only when <paramref name="isDirectory"/> is true; callers prune
    /// ignored directories during the walk, so files beneath them are never enumerated.
    /// </summary>
    public bool IsIgnored(string relativePath, bool isDirectory)
    {
        if (_rules.Count == 0 || string.IsNullOrEmpty(relativePath)) return false;

        var path = relativePath.Replace('\\', '/').TrimStart('/');
        if (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        if (path.Length == 0) return false;

        bool ignored = false;
        foreach (var rule in _rules)
        {
            if (rule.DirOnly && !isDirectory) continue;
            if (rule.Regex.IsMatch(path))
                ignored = !rule.IsNegation;
        }
        return ignored;
    }

    private static Rule? ParseLine(string raw)
    {
        // Strip a trailing CR (defensive — callers should split on both \r\n and \n) and trailing
        // whitespace, then skip blanks and comments. Escaped comment/whitespace forms are not supported.
        var line = raw.TrimEnd('\r').TrimEnd(' ', '\t');
        if (line.Length == 0 || line[0] == '#') return null;

        bool negation = false;
        if (line[0] == '!')
        {
            negation = true;
            line = line[1..];
        }

        bool dirOnly = false;
        if (line.EndsWith('/'))
        {
            dirOnly = true;
            line = line[..^1];
        }

        bool anchored = false;
        if (line.StartsWith('/'))
        {
            anchored = true;
            line = line[1..];
        }
        else if (line.Contains('/'))
        {
            // A pattern with an interior slash is anchored to the ignore root (gitignore rule).
            anchored = true;
        }

        if (line.Length == 0) return null;

        var body = GlobPattern.TranslateGlob(line);

        // Anchored patterns match the full relative path from the root; unanchored ones match a
        // trailing path segment at any depth (so "bin" matches "bin" and "src/bin" but never the
        // substring "cabinet").
        var pattern = anchored ? $"^{body}$" : $"^(?:.*/)?{body}$";
        var regex = new Regex(pattern, Options);
        return new Rule(regex, negation, dirOnly);
    }
}
