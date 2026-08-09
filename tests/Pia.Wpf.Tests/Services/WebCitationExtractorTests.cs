using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

public class WebCitationExtractorTests
{
    // Each chip marker round-trips through Markdig as a hyperlink rendering
    // the literal text `[N]`. This helper builds the exact markdown source
    // the extractor emits so tests stay readable.
    // url is SourceRef.Url, which is declared string? — a null would interpolate to an empty
    // href and fail the caller's assertion, which is the behavior we want.
    private static string Marker(int number, string? url) => $"[\\[{number}\\]]({url})";

    [Fact]
    public void Extract_EmptyText_ReturnsEmptyAndNoSources()
    {
        var (cleaned, sources) = WebCitationExtractor.Extract(string.Empty);

        Assert.Equal(string.Empty, cleaned);
        Assert.Empty(sources);
    }

    [Fact]
    public void Extract_PlainText_ReturnsTextUnchangedAndNoSources()
    {
        const string text = "Tesla's stock is volatile this week.";

        var (cleaned, sources) = WebCitationExtractor.Extract(text);

        Assert.Equal(text, cleaned);
        Assert.Empty(sources);
    }

    [Fact]
    public void Extract_BrokenTeslaCitations_StripsRunsAndEmitsChips()
    {
        // The exact shape produced by an OpenAI Responses API web_search reply:
        // reference-style brackets with the URL where the label should be,
        // and the opening `[` of the anchor swallowed by the SDK adapter.
        const string text =
            "Tesla's stock (TSLA) is currently trading around $426.01 on the Nasdaq exchange. " +
            "finance.yahoo.com][https://finance.yahoo.com/quote/TSLA/?t=6a159e5ea9a9ba6c2aa2056f]" +
            "  Bloomberg][https://www.bloomberg.com/quote/TSLA:US]";

        var (cleaned, sources) = WebCitationExtractor.Extract(text);

        Assert.Equal(2, sources.Count);
        Assert.Equal(1, sources[0].Number);
        Assert.Equal("finance.yahoo.com", sources[0].Source);
        Assert.Equal("https://finance.yahoo.com/quote/TSLA/?t=6a159e5ea9a9ba6c2aa2056f", sources[0].Url);
        Assert.Equal(2, sources[1].Number);
        Assert.Equal("bloomberg.com", sources[1].Source);
        Assert.Equal("Bloomberg", sources[1].Meta);
        Assert.Equal("https://www.bloomberg.com/quote/TSLA:US", sources[1].Url);

        Assert.DoesNotContain("][http", cleaned);
        Assert.StartsWith("Tesla's stock", cleaned);
        var tail = $"Nasdaq exchange. {Marker(1, sources[0].Url)} {Marker(2, sources[1].Url)}";
        Assert.EndsWith(tail, cleaned.TrimEnd());
    }

    [Fact]
    public void Extract_WellFormedMarkdownLink_KeepsAnchorAndAppendsClickableMarker()
    {
        const string text = "See [Tesla price](https://finance.yahoo.com/quote/TSLA/) for details.";

        var (cleaned, sources) = WebCitationExtractor.Extract(text);

        Assert.Single(sources);
        Assert.Equal("finance.yahoo.com", sources[0].Source);
        Assert.Equal("Tesla price", sources[0].Meta);
        Assert.Equal("https://finance.yahoo.com/quote/TSLA/", sources[0].Url);
        Assert.Equal($"See Tesla price {Marker(1, sources[0].Url)} for details.", cleaned);
    }

    [Fact]
    public void Extract_DuplicateUrl_EmitsSingleChipAndReusesMarker()
    {
        const string text =
            "First mention [Yahoo](https://finance.yahoo.com/quote/TSLA/). " +
            "Second mention [Yahoo](https://finance.yahoo.com/quote/TSLA/).";

        var (cleaned, sources) = WebCitationExtractor.Extract(text);

        Assert.Single(sources);
        Assert.Equal("https://finance.yahoo.com/quote/TSLA/", sources[0].Url);
        // Both occurrences resolve to the same chip number and same link target.
        var marker = Marker(1, sources[0].Url);
        Assert.Equal($"First mention Yahoo {marker}. Second mention Yahoo {marker}.", cleaned);
    }

    [Fact]
    public void Extract_MetaEqualsHost_ReturnsEmptyMeta()
    {
        // When the anchor text is just the host (e.g. "finance.yahoo.com"),
        // there's no extra info to show beyond the host already in `Source`.
        const string text = "finance.yahoo.com][https://finance.yahoo.com/quote/TSLA/]";

        var (_, sources) = WebCitationExtractor.Extract(text);

        Assert.Single(sources);
        Assert.Equal("finance.yahoo.com", sources[0].Source);
        Assert.Equal(string.Empty, sources[0].Meta);
    }

    [Fact]
    public void Extract_BareUrlsInBulletList_EmitOneChipPerUrl()
    {
        // The shape produced when an LLM lists trending repos without
        // wrapping URLs in markdown link syntax — previously zero chips
        // were extracted, now every URL becomes a chip.
        const string text =
            "Trending AI agent repos:\n" +
            "1. AutoGPT - https://github.com/Significant-Gravitas/AutoGPT\n" +
            "2. AgentGPT - https://github.com/reworkd/AgentGPT\n" +
            "3. BabyAGI - https://github.com/yoheinakajima/babyagi";

        var (cleaned, sources) = WebCitationExtractor.Extract(text);

        Assert.Equal(3, sources.Count);
        Assert.Equal("github.com", sources[0].Source);
        Assert.Equal("https://github.com/Significant-Gravitas/AutoGPT", sources[0].Url);
        Assert.Equal("https://github.com/reworkd/AgentGPT", sources[1].Url);
        Assert.Equal("https://github.com/yoheinakajima/babyagi", sources[2].Url);

        Assert.Contains($"AutoGPT - {Marker(1, sources[0].Url)}", cleaned);
        Assert.Contains($"AgentGPT - {Marker(2, sources[1].Url)}", cleaned);
        Assert.Contains($"BabyAGI - {Marker(3, sources[2].Url)}", cleaned);
    }

    [Fact]
    public void Extract_BareUrlFollowedByPeriod_KeepsPunctuationOutsideChip()
    {
        const string text = "Visit https://github.com/foo/bar.";

        var (cleaned, sources) = WebCitationExtractor.Extract(text);

        Assert.Single(sources);
        Assert.Equal("https://github.com/foo/bar", sources[0].Url);
        Assert.Equal($"Visit {Marker(1, sources[0].Url)}.", cleaned);
    }

    [Fact]
    public void Extract_MixedShapes_NumbersByDocumentOrder()
    {
        // Bare URL appears first, followed by a well-formed link, followed
        // by a broken reference. Chip numbers should reflect that order.
        const string text =
            "First https://a.example.com then [middle](https://b.example.com) " +
            "and end][https://c.example.com]";

        var (cleaned, sources) = WebCitationExtractor.Extract(text);

        Assert.Equal(3, sources.Count);
        Assert.Equal("a.example.com", sources[0].Source);
        Assert.Equal("b.example.com", sources[1].Source);
        Assert.Equal("c.example.com", sources[2].Source);

        var expected = $"First {Marker(1, sources[0].Url)} then middle {Marker(2, sources[1].Url)} and {Marker(3, sources[2].Url)}";
        Assert.Equal(expected, cleaned);
    }
}
