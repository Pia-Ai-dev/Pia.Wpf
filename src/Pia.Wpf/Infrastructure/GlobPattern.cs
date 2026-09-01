using System.Text;
using System.Text.RegularExpressions;

namespace Pia.Infrastructure;

/// <summary>Glob→regex translation shared by the ignore matcher and the file tools' path patterns.</summary>
internal static class GlobPattern
{
    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    /// <summary>
    /// Compiles a glob with gitignore anchoring: a bare name matches a trailing path segment at any
    /// depth, while a leading or interior <c>/</c> anchors the pattern to the search base.
    /// </summary>
    internal static Regex Compile(string glob)
    {
        // Candidates are always forward-slashed, and both rewrites have to land before the anchoring
        // decision below: a backslash spelling would otherwise read as unanchored, and a folder-shaped
        // "docs/" would compile to a pattern no file path can satisfy.
        var line = glob.Replace('\\', '/');
        if (line.EndsWith('/')) line += "**";

        bool anchored = false;
        if (line.StartsWith('/'))
        {
            anchored = true;
            line = line[1..];
        }
        else if (line.Contains('/'))
        {
            anchored = true;
        }

        var body = TranslateGlob(line);
        var pattern = anchored ? $"^{body}$" : $"^(?:.*/)?{body}$";
        return new Regex(pattern, Options);
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
    internal static string TranslateGlob(string glob)
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
