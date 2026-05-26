using Pia.Services;
using Xunit;

namespace Pia.Wpf.Tests.Unit;

public class WebCitationExtractorTests
{
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

        Assert.DoesNotContain("][", cleaned);
        Assert.DoesNotContain("https://", cleaned);
        Assert.StartsWith("Tesla's stock", cleaned);
        Assert.EndsWith("Nasdaq exchange.", cleaned.TrimEnd());
    }

    [Fact]
    public void Extract_WellFormedMarkdownLink_StripsAndEmitsChip()
    {
        const string text = "See [Tesla price](https://finance.yahoo.com/quote/TSLA/) for details.";

        var (cleaned, sources) = WebCitationExtractor.Extract(text);

        Assert.Single(sources);
        Assert.Equal("finance.yahoo.com", sources[0].Source);
        Assert.Equal("Tesla price", sources[0].Meta);
        Assert.Equal("https://finance.yahoo.com/quote/TSLA/", sources[0].Url);
        Assert.Equal("See for details.", cleaned);
    }

    [Fact]
    public void Extract_DuplicateUrl_EmitsSingleChip()
    {
        const string text =
            "First mention [Yahoo](https://finance.yahoo.com/quote/TSLA/). " +
            "Second mention [Yahoo](https://finance.yahoo.com/quote/TSLA/).";

        var (cleaned, sources) = WebCitationExtractor.Extract(text);

        Assert.Single(sources);
        Assert.Equal("https://finance.yahoo.com/quote/TSLA/", sources[0].Url);
        Assert.DoesNotContain("https://", cleaned);
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
}
