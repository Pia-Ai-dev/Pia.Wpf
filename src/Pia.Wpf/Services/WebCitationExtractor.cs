using System.Text;
using System.Text.RegularExpressions;
using Pia.Models;

namespace Pia.Services;

/// <summary>
/// Pulls citation URLs out of an assistant message body and returns a cleaned
/// body alongside numbered <see cref="SourceRef"/> entries that the UI surfaces
/// as <c>PiaSourceChip</c> pills under the message. Each matched URL is
/// replaced in place with a <c>[N]</c> marker so readers can map a sentence
/// back to the chip that backs it.
/// </summary>
/// <remarks>
/// Web-search-enabled providers (OpenAI Responses API, OpenRouter, PiaCloud)
/// embed citations in several shapes — well-formed Markdown links, malformed
/// reference-style runs that Markdig refuses to render (<c>finance.yahoo.com][https://…]</c>,
/// missing the leading bracket), or just bare URLs at the end of a bullet.
/// We capture all three.
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

    // Bare URL — anything starting with http(s):// that isn't already inside
    // a markdown link run (those are claimed by the patterns above and win
    // overlap resolution because they start earlier in the text). The char
    // class excludes brackets and parens to keep the URL from bleeding into
    // surrounding punctuation; trailing `.`/`,`/`;`/etc. are trimmed below so
    // the period at the end of a sentence stays in the text.
    private static readonly Regex BareUrl = new(
        @"\bhttps?://[^\s<>\[\]()""']+",
        RegexOptions.Compiled);

    private const string TrailingPunct = ".,;:!?";

    private enum MatchKind { WellFormed, Broken, Bare }

    private sealed record MatchInfo(int Start, int Length, string Url, string AnchorText, MatchKind Kind);

    public static (string CleanedText, IReadOnlyList<SourceRef> Sources) Extract(string text)
    {
        if (string.IsNullOrEmpty(text))
            return (text, Array.Empty<SourceRef>());

        var matches = CollectMatches(text);
        if (matches.Count == 0)
            return (text, Array.Empty<SourceRef>());

        var resolved = ResolveOverlaps(matches);

        var byUrl = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<(string Url, string AnchorText)>();
        foreach (var m in resolved)
        {
            if (byUrl.ContainsKey(m.Url)) continue;
            byUrl[m.Url] = ordered.Count + 1;
            ordered.Add((m.Url, m.AnchorText));
        }

        var rewritten = BuildMarkedText(text, resolved, byUrl);
        var sources = ordered
            .Select((s, i) => BuildSourceRef(i + 1, s.Url, s.AnchorText))
            .ToList();

        return (CollapseWhitespace(rewritten), sources);
    }

    private static List<MatchInfo> CollectMatches(string text)
    {
        var matches = new List<MatchInfo>();

        foreach (Match m in WellFormedLink.Matches(text))
            matches.Add(new MatchInfo(m.Index, m.Length,
                m.Groups["url"].Value, m.Groups["text"].Value.Trim(), MatchKind.WellFormed));

        foreach (Match m in BrokenReferenceLink.Matches(text))
            matches.Add(new MatchInfo(m.Index, m.Length,
                m.Groups["url"].Value, m.Groups["text"].Value.Trim(), MatchKind.Broken));

        foreach (Match m in BareUrl.Matches(text))
        {
            var url = m.Value;
            var len = m.Length;
            // Don't swallow sentence-ending punctuation into the URL —
            // shorten the match so the period/comma stays in the cleaned text.
            while (len > 0 && TrailingPunct.Contains(url[len - 1]))
                len--;
            if (len == 0) continue;
            matches.Add(new MatchInfo(m.Index, len, url[..len], string.Empty, MatchKind.Bare));
        }

        return matches;
    }

    private static List<MatchInfo> ResolveOverlaps(List<MatchInfo> matches)
    {
        // Earlier start wins; on ties prefer well-formed > broken > bare so a
        // bracketed link beats the bare URL hiding inside it.
        matches.Sort((a, b) =>
        {
            var c = a.Start.CompareTo(b.Start);
            return c != 0 ? c : a.Kind.CompareTo(b.Kind);
        });

        var resolved = new List<MatchInfo>(matches.Count);
        var consumedTo = 0;
        foreach (var m in matches)
        {
            if (m.Start < consumedTo) continue;
            resolved.Add(m);
            consumedTo = m.Start + m.Length;
        }
        return resolved;
    }

    private static string BuildMarkedText(
        string text,
        List<MatchInfo> resolved,
        Dictionary<string, int> byUrl)
    {
        var sb = new StringBuilder(text.Length);
        var lastEnd = 0;
        foreach (var m in resolved)
        {
            sb.Append(text, lastEnd, m.Start - lastEnd);
            var n = byUrl[m.Url];
            if (m.Kind == MatchKind.WellFormed && !string.IsNullOrEmpty(m.AnchorText))
            {
                sb.Append(m.AnchorText).Append(' ');
            }
            AppendMarkerLink(sb, n, m.Url);
            lastEnd = m.Start + m.Length;
        }
        sb.Append(text, lastEnd, text.Length - lastEnd);
        return sb.ToString();
    }

    // Emit the [N] marker as a real markdown link so Markdig renders it as a
    // hyperlink — the brackets are escaped so they survive as literal display
    // text rather than being parsed as another link/reference shape.
    private static void AppendMarkerLink(StringBuilder sb, int number, string url)
    {
        sb.Append("[\\[").Append(number).Append("\\]](").Append(url).Append(')');
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
        var s = Regex.Replace(text, @"[ \t]{2,}", " ");
        s = Regex.Replace(s, @" +([,.;:!?\)])", "$1");
        s = Regex.Replace(s, @"\(\s+", "(");
        var lines = s.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimEnd();
        return string.Join('\n', lines);
    }
}
