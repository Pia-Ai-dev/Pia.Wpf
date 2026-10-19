using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Pia.Models;

namespace Pia.Services;

/// <summary>Turns citation URLs outside code into numbered <c>[N]</c> markers backed by <see cref="SourceRef"/> chips.</summary>
public static class WebCitationExtractor
{
    private static readonly MarkdownPipeline CodeLocatingPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UsePreciseSourceLocation()
        .Build();

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

    private static readonly (Regex Pattern, string Replacement)[] CollapseRules =
    [
        (new Regex(@"[ \t]{2,}", RegexOptions.Compiled), " "),
        (new Regex(@" +([,.;:!?\)])", RegexOptions.Compiled), "$1"),
        (new Regex(@"\(\s+", RegexOptions.Compiled), "("),
        (new Regex(@"[^\S\n]+(?=\n|\z)", RegexOptions.Compiled), string.Empty),
    ];

    private enum MatchKind { WellFormed, Broken, Bare }

    private sealed record MatchInfo(int Start, int Length, int UrlStart, string Url, string AnchorText, MatchKind Kind);

    public static (string CleanedText, IReadOnlyList<SourceRef> Sources) Extract(string text)
    {
        if (string.IsNullOrEmpty(text))
            return (text, Array.Empty<SourceRef>());

        var matches = CollectMatches(text);
        if (matches.Count > 0)
        {
            var code = FindCodeRanges(text);
            matches.RemoveAll(m => Overlaps(code, m.UrlStart, m.Url.Length));
        }
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
            matches.Add(new MatchInfo(m.Index, m.Length, m.Groups["url"].Index,
                m.Groups["url"].Value, m.Groups["text"].Value.Trim(), MatchKind.WellFormed));

        foreach (Match m in BrokenReferenceLink.Matches(text))
            matches.Add(new MatchInfo(m.Index, m.Length, m.Groups["url"].Index,
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
            matches.Add(new MatchInfo(m.Index, len, m.Index, url[..len], string.Empty, MatchKind.Bare));
        }

        return matches;
    }

    // Inclusive offsets, as the renderer's own Markdig parse sees code.
    private static List<(int Start, int End)> FindCodeRanges(string text)
    {
        var ranges = new List<(int Start, int End)>();
        foreach (var node in Markdown.Parse(text, CodeLocatingPipeline).Descendants())
        {
            switch (node)
            {
                case CodeBlock block:
                    // An unclosed fence's Span covers only its opening line; the last content line ends it.
                    var end = block.Lines.Count > 0
                        ? Math.Max(block.Span.End, block.Lines.Lines[block.Lines.Count - 1].Slice.End)
                        : block.Span.End;
                    ranges.Add((IndentStart(text, block.Span.Start), end));
                    break;
                case CodeInline inline:
                    ranges.Add((inline.Span.Start, inline.Span.End));
                    break;
            }
        }
        return ranges;
    }

    // A fence nested in a list item is placed by its indent, so that indent belongs to the block.
    private static int IndentStart(string text, int start)
    {
        while (start > 0 && text[start - 1] is ' ' or '\t')
            start--;
        return start;
    }

    private static bool Overlaps(List<(int Start, int End)> ranges, int start, int length)
    {
        var end = start + length - 1;
        return ranges.Exists(r => start <= r.End && end >= r.Start);
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
        foreach (var (pattern, replacement) in CollapseRules)
        {
            // Every replacement shifts offsets, so each rule locates code in the text it is about to edit.
            var code = FindCodeRanges(text);
            text = pattern.Replace(text, m => Overlaps(code, m.Index, m.Length) ? m.Value : m.Result(replacement));
        }
        return text;
    }
}
