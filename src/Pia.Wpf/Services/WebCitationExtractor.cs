using System.Text;
using System.Text.RegularExpressions;
using Pia.Models;

namespace Pia.Services;

/// <summary>
/// Pulls citation URLs out of an assistant message body and returns a cleaned
/// body alongside numbered <see cref="SourceRef"/> entries that the UI surfaces
/// as <c>PiaSourceChip</c> pills under the message.
/// </summary>
/// <remarks>
/// Web-search-enabled providers (OpenAI Responses API, OpenRouter, PiaCloud)
/// often embed citations in malformed shapes that Markdig refuses to render
/// as hyperlinks — e.g. <c>finance.yahoo.com][https://…]</c> (missing the
/// leading bracket of a reference-style link). We strip those runs out of the
/// body and re-surface the URLs as chips.
/// </remarks>
public static class WebCitationExtractor
{
    // [text](https://url)
    private static readonly Regex WellFormedLink = new(
        @"\[(?<text>[^\[\]\n]+?)\]\((?<url>https?://[^\s)]+)\)",
        RegexOptions.Compiled);

    // Broken reference-style with the URL where the label should be, with the
    // opening `[` of the anchor optionally swallowed by the provider:
    //   [text][https://url]   or   text][https://url]
    // Anchor text is non-whitespace, capped at 80 chars to stop the engine
    // from back-tracking across an entire sentence to find a `]`.
    private static readonly Regex BrokenReferenceLink = new(
        @"\[?(?<text>[^\s\[\]]{1,80})\]\[(?<url>https?://[^\]\s]+)\]",
        RegexOptions.Compiled);

    public static (string CleanedText, IReadOnlyList<SourceRef> Sources) Extract(string text)
    {
        if (string.IsNullOrEmpty(text))
            return (text, Array.Empty<SourceRef>());

        var byUrl = new Dictionary<string, (string Text, int FirstIndex)>(StringComparer.OrdinalIgnoreCase);

        // Run well-formed first so it claims `[text](url)` before the broken
        // pattern (which is permissive enough to grab a sliced span otherwise).
        // Each pass replaces matched runs with the empty string so the next
        // regex sees only what's left.
        var rewritten = ApplyPattern(WellFormedLink, text, byUrl);
        rewritten = ApplyPattern(BrokenReferenceLink, rewritten, byUrl);

        if (byUrl.Count == 0)
            return (text, Array.Empty<SourceRef>());

        var ordered = byUrl
            .OrderBy(kv => kv.Value.FirstIndex)
            .Select((kv, i) => BuildSourceRef(i + 1, kv.Key, kv.Value.Text))
            .ToList();

        var collapsed = CollapseWhitespace(rewritten);

        return (collapsed, ordered);
    }

    private static string ApplyPattern(
        Regex pattern,
        string input,
        Dictionary<string, (string Text, int FirstIndex)> byUrl)
    {
        var sb = new StringBuilder(input.Length);
        var lastEnd = 0;

        foreach (Match m in pattern.Matches(input))
        {
            var url = m.Groups["url"].Value;
            var anchorText = m.Groups["text"].Value.Trim();

            if (!byUrl.ContainsKey(url))
                byUrl[url] = (anchorText, m.Index);

            sb.Append(input, lastEnd, m.Index - lastEnd);
            lastEnd = m.Index + m.Length;
        }

        sb.Append(input, lastEnd, input.Length - lastEnd);
        return sb.ToString();
    }

    private static SourceRef BuildSourceRef(int number, string url, string anchorText)
    {
        var host = TryGetHost(url) ?? anchorText;
        var meta = string.Equals(anchorText, host, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : anchorText;
        return new SourceRef(number, host, meta, url);
    }

    private static string? TryGetHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            host = host[4..];
        return host;
    }

    private static string CollapseWhitespace(string text)
    {
        // Conservative post-cleanup: remove orphan punctuation spacing the
        // citation removal left behind, and squash runs of spaces.
        var s = Regex.Replace(text, @"[ \t]{2,}", " ");
        s = Regex.Replace(s, @" +([,.;:!?\)])", "$1");
        s = Regex.Replace(s, @"\(\s+", "(");
        // Trim each line so we don't leave trailing spaces where a citation
        // sat at end-of-line.
        var lines = s.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimEnd();
        return string.Join('\n', lines);
    }
}
