using System.Text;
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
        var rules = new List<Rule>();
        foreach (var line in lines)
        {
            var rule = ParseLine(line);
            if (rule is not null) rules.Add(rule);
        }
        return new GitignoreMatcher(rules);
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

        var body = TranslateGlob(line);

        // Anchored patterns match the full relative path from the root; unanchored ones match a
        // trailing path segment at any depth (so "bin" matches "bin" and "src/bin" but never the
        // substring "cabinet").
        var pattern = anchored ? $"^{body}$" : $"^(?:.*/)?{body}$";
        var regex = new Regex(pattern, Options);
        return new Rule(regex, negation, dirOnly);
    }

    /// <summary>
    /// Translates a gitignore glob fragment into a regex body:
    /// <list type="bullet">
    ///   <item><c>*</c> → any run of non-separator chars; <c>?</c> → one non-separator char.</item>
    ///   <item><c>**</c> is separator-crossing only when slash-delimited (<c>**/</c>, <c>/**</c>,
    ///   <c>/**/</c>); a bare interior <c>a**b</c> degrades to a single <c>*</c>, matching git.</item>
    ///   <item><c>[...]</c> / <c>[!...]</c> character classes are preserved (negated classes also
    ///   exclude <c>/</c>).</item>
    ///   <item>everything else is escaped as a literal.</item>
    /// </list>
    /// </summary>
    private static string TranslateGlob(string glob)
    {
        var sb = new StringBuilder(glob.Length * 2);
        int i = 0;
        while (i < glob.Length)
        {
            char c = glob[i];
            if (c == '*')
            {
                int start = i;
                while (i < glob.Length && glob[i] == '*') i++; // consume the run of '*'
                int runLen = i - start;
                bool precededBySlashOrStart = start == 0 || glob[start - 1] == '/';
                bool followedBySlashOrEnd = i >= glob.Length || glob[i] == '/';

                if (runLen >= 2 && precededBySlashOrStart && followedBySlashOrEnd)
                {
                    if (i < glob.Length && glob[i] == '/')
                    {
                        sb.Append("(?:.*/)?"); // "**/" — zero or more directories
                        i++;                    // consume the '/'
                    }
                    else
                    {
                        sb.Append(".*");        // trailing "**" — anything, including separators
                    }
                }
                else
                {
                    sb.Append("[^/]*");         // "*" (or a non-slash-delimited "**") — one segment
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
                i++;
            }
            else if (c == '[')
            {
                int close = FindClassEnd(glob, i);
                if (close < 0)
                {
                    sb.Append(Regex.Escape("[")); // unterminated class → literal '['
                    i++;
                }
                else
                {
                    AppendClass(sb, glob, i, close);
                    i = close + 1;
                }
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
                i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Finds the index of the <c>]</c> closing the class opened at <paramref name="open"/>, honoring
    /// the fnmatch rule that a <c>]</c> immediately after <c>[</c> (or <c>[!</c>) is a literal member.
    /// Returns -1 for an unterminated class.
    /// </summary>
    private static int FindClassEnd(string s, int open)
    {
        int k = open + 1;
        if (k < s.Length && s[k] == '!') k++;
        if (k < s.Length && s[k] == ']') k++; // leading ']' is a literal member, not the terminator
        while (k < s.Length && s[k] != ']') k++;
        return k < s.Length ? k : -1;
    }

    private static void AppendClass(StringBuilder sb, string s, int open, int close)
    {
        var inner = s.Substring(open + 1, close - (open + 1));
        bool negated = inner.StartsWith('!') || inner.StartsWith('^');
        if (negated) inner = inner[1..];

        if (inner.Length == 0)
        {
            // "[]" / "[!]" — no members; emit the raw text as a literal rather than an invalid class.
            sb.Append(Regex.Escape(s.Substring(open, close - open + 1)));
            return;
        }

        // Escape the class-structural characters so a member like ']' or '\' can't break out of the
        // class; a range hyphen is preserved. A negated class must also exclude the path separator,
        // otherwise "[!a]" would match "/" and let the pattern span directories.
        var body = inner.Replace("\\", "\\\\").Replace("]", "\\]").Replace("[", "\\[");
        sb.Append('[');
        if (negated) sb.Append('^');
        sb.Append(body);
        if (negated) sb.Append('/');
        sb.Append(']');
    }
}
