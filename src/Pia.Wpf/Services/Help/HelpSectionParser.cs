using System.Text;

namespace Pia.Services.Help;

/// <summary>Splits a guide page into the H2/H3 sections that are indexed and returned individually.</summary>
public static class HelpSectionParser
{
    public static IReadOnlyList<HelpSection> Split(HelpPage page)
    {
        var sections = new List<HelpSection>();
        var usedReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var body = new StringBuilder();
        var chapter = string.Empty;
        var heading = string.Empty;
        var inFence = false;
        string? fenceMarker = null;

        void Emit()
        {
            var text = body.ToString().Trim();
            body.Clear();

            // A heading with no prose under it is still worth indexing: the heading words are the match.
            if (text.Length == 0 && heading.Length == 0) return;

            var reference = Unique(page.Path, heading, usedReferences);
            sections.Add(new HelpSection(reference, page.Path, page.Title, heading, text, page.Url));
        }

        foreach (var line in page.Body.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            var fence = FenceMarker(trimmed);

            if (inFence)
            {
                if (fence is not null && fenceMarker is not null && fence[0] == fenceMarker[0] && fence.Length >= fenceMarker.Length)
                {
                    inFence = false;
                    fenceMarker = null;
                }
                body.Append(trimmed).Append('\n');
                continue;
            }

            if (fence is not null)
            {
                inFence = true;
                fenceMarker = fence;
                body.Append(trimmed).Append('\n');
                continue;
            }

            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                Emit();
                chapter = trimmed[3..].Trim();
                heading = chapter;
                continue;
            }

            if (trimmed.StartsWith("### ", StringComparison.Ordinal))
            {
                Emit();
                var child = trimmed[4..].Trim();
                heading = chapter.Length == 0 ? child : chapter + " > " + child;
                continue;
            }

            body.Append(trimmed).Append('\n');
        }

        Emit();
        return sections;
    }

    /// <summary>The opening or closing run of backticks/tildes on a line, or null when the line is not a fence.</summary>
    private static string? FenceMarker(string line)
    {
        var text = line.TrimStart();
        if (text.Length < 3) return null;

        var marker = text[0];
        if (marker != '`' && marker != '~') return null;

        var run = 0;
        while (run < text.Length && text[run] == marker) run++;
        return run >= 3 ? text[..run] : null;
    }

    private static string Unique(string pagePath, string heading, HashSet<string> used)
    {
        var slug = Slugify(heading);
        var candidate = slug.Length == 0 ? pagePath : pagePath + "#" + slug;

        var suffix = 2;
        while (!used.Add(candidate))
        {
            candidate = (slug.Length == 0 ? pagePath : pagePath + "#" + slug) + "-" + suffix;
            suffix++;
        }
        return candidate;
    }

    internal static string Slugify(string heading)
    {
        var sb = new StringBuilder(heading.Length);
        foreach (var ch in heading)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }
}
