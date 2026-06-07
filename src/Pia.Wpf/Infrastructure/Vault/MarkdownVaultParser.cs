using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pia.Models.Vault;
using YamlDotNet.Serialization;

namespace Pia.Infrastructure.Vault;

/// <summary>
/// Parses a Pia-managed vault markdown file per Memory Vault Format spec v1 (§2/§3/§6/Appendix A).
/// Never mutates the input: <see cref="VaultDocument.RawText"/> is the exact original text, enabling
/// byte-range splice edits (§3.1). Line endings are never normalized.
/// </summary>
public sealed class MarkdownVaultParser
{
    // §3 boundary predicate: exactly two '#', one space, then at least one char of heading text.
    // Applied to logical-line content (terminator removed), so single-line mode is correct.
    private static readonly Regex SectionBoundary = new(@"^## (.+)$", RegexOptions.Compiled);

    // §6 step 4: maximal runs of non-[a-z0-9] -> single '-'.
    private static readonly Regex NonSlugRun = new("[^a-z0-9]+", RegexOptions.Compiled);

    // §3 ASCII whitespace set for heading trimming: {TAB, LF, VT, FF, CR, SPACE}.
    private static readonly char[] AsciiWhitespace = { '\t', '\n', '\u000B', '\u000C', '\r', ' ' };

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder().Build();

    /// <summary>Parse the exact file text into a <see cref="VaultDocument"/>.</summary>
    public VaultDocument Parse(string text)
    {
        // Split into logical lines, retaining the start char-offset of each line in the original text.
        var lines = SplitLogicalLines(text);

        // ---- 1. Frontmatter (Appendix A §1) ----
        var frontmatter = new Dictionary<string, string>(StringComparer.Ordinal);
        var contentLineIndex = 0;
        if (lines.Count > 0 && IsDelimiter(lines[0].Content))
        {
            var closing = -1;
            for (var i = 1; i < lines.Count; i++)
            {
                if (IsDelimiter(lines[i].Content))
                {
                    closing = i;
                    break;
                }
            }

            if (closing > 0)
            {
                var block = string.Join("\n", lines.GetRange(1, closing - 1).Select(l => l.Content));
                ParseFrontmatter(block, frontmatter);
                // Content region begins on the line after the closing delimiter line.
                contentLineIndex = closing + 1;
            }
            // If no closing delimiter is found, treat the whole text as content (frontmatter empty).
        }

        var contentStart = contentLineIndex < lines.Count ? lines[contentLineIndex].Start : text.Length;

        // ---- 2. Preamble + 3. Sections (Appendix A §2/§3) ----
        // First locate the boundary line indices within the content region.
        var boundaryLineIndices = new List<int>();
        for (var i = contentLineIndex; i < lines.Count; i++)
        {
            if (SectionBoundary.IsMatch(lines[i].Content))
            {
                boundaryLineIndices.Add(i);
            }
        }

        // Preamble = content-region text up to (not including) the first boundary line, or EOF.
        var firstBoundaryStart = boundaryLineIndices.Count > 0
            ? lines[boundaryLineIndices[0]].Start
            : text.Length;
        var preamble = text[contentStart..firstBoundaryStart];

        var sections = new List<VaultSection>();
        var slugCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var b = 0; b < boundaryLineIndices.Count; b++)
        {
            var lineIdx = boundaryLineIndices[b];
            var line = lines[lineIdx];

            var heading = SectionBoundary.Match(line.Content).Groups[1].Value.Trim(AsciiWhitespace);
            var slug = DedupeSlug(Slugify(heading), slugCounts);

            // BodyStart = char index just after the heading line's terminator
            // (RawText.Length if the heading line is the file's last line with no trailing '\n').
            var bodyStart = line.End;

            // BodyEnd = char index just before the next boundary line's first '#',
            // or RawText.Length for the final section.
            var bodyEnd = b + 1 < boundaryLineIndices.Count
                ? lines[boundaryLineIndices[b + 1]].Start
                : text.Length;

            var body = text[bodyStart..bodyEnd];
            sections.Add(new VaultSection(heading, slug, body, bodyStart, bodyEnd));
        }

        return new VaultDocument(frontmatter, preamble, sections, text);
    }

    private static void ParseFrontmatter(string block, Dictionary<string, string> into)
    {
        if (string.IsNullOrWhiteSpace(block))
        {
            return;
        }

        var map = YamlDeserializer.Deserialize<Dictionary<string, object?>>(block);
        if (map is null)
        {
            return;
        }

        foreach (var (key, value) in map)
        {
            // Scalars -> ToString(); non-scalars (lists/maps) keep their raw text representation.
            into[key] = value switch
            {
                null => string.Empty,
                string s => s,
                _ => value.ToString() ?? string.Empty,
            };
        }
    }

    /// <summary>§6 slug algorithm (steps 1-6, without dedupe).</summary>
    private static string Slugify(string heading)
    {
        var decomposed = heading.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        var lowered = sb.ToString().ToLowerInvariant();
        var slug = NonSlugRun.Replace(lowered, "-").Trim('-');
        return slug.Length == 0 ? "section" : slug;
    }

    /// <summary>§6 step 7 collision suffix, applied to post-fallback slugs in document order.</summary>
    private static string DedupeSlug(string slug, Dictionary<string, int> counts)
    {
        if (counts.TryGetValue(slug, out var count))
        {
            counts[slug] = count + 1;
            return $"{slug}-{count + 1}";
        }

        counts[slug] = 1;
        return slug;
    }

    /// <summary>§2 delimiter test: content (one optional trailing '\r' removed) equals "---".</summary>
    private static bool IsDelimiter(string lineContent)
    {
        var content = lineContent.EndsWith('\r') ? lineContent[..^1] : lineContent;
        return content == "---";
    }

    private static List<LogicalLine> SplitLogicalLines(string text)
    {
        var lines = new List<LogicalLine>();
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\n')
            {
                // Content is start..i; line terminator is the '\n' (CR handled by IsDelimiter/regex).
                lines.Add(new LogicalLine(text[start..i], start, i + 1));
                i++;
                start = i;
            }
            else
            {
                i++;
            }
        }

        // Trailing line with no '\n' terminator (only if there is residual content).
        if (start < text.Length)
        {
            lines.Add(new LogicalLine(text[start..], start, text.Length));
        }

        return lines;
    }

    /// <summary>A logical line: its content (terminator excluded), and char offsets into RawText.</summary>
    private readonly record struct LogicalLine(string Content, int Start, int End);
}
