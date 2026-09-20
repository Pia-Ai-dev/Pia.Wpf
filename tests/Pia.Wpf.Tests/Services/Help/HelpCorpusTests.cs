using Pia.Services.Help;
using Xunit;

namespace Pia.Tests.Services.Help;

/// <summary>
/// The corpus is an embedded resource refreshed by hand, so a packaging slip or a bad refresh is only
/// visible here — everything downstream degrades quietly to "no results".
/// </summary>
public sealed class HelpCorpusTests
{
    [Fact]
    public void TheEmbeddedCorpusLoads()
    {
        using var service = new HelpSearchService();

        Assert.True(service.IsAvailable, "the embedded help corpus did not load — check the EmbeddedResource in Pia.Wpf.csproj");
        Assert.True(service.PageCount >= 30, $"expected the desktop guide's pages, found {service.PageCount}");
        Assert.NotEqual("unknown", service.SourceCommit);
    }

    [Fact]
    public void EveryPageCarriesATitleAUrlAndABody()
    {
        using var service = new HelpSearchService();

        foreach (var page in service.Pages)
        {
            Assert.False(string.IsNullOrWhiteSpace(page.Title), $"{page.Path} has no title");
            Assert.StartsWith("https://docs.pia-ai.de/", page.Url, StringComparison.Ordinal);
            Assert.True(page.Body.Length > 100, $"{page.Path} has a suspiciously short body");
        }
    }

    [Fact]
    public void TheBuilderHeaderIsStrippedSoEveryPageDoesNotMatchEveryQuery()
    {
        using var service = new HelpSearchService();

        foreach (var page in service.Pages)
        {
            Assert.DoesNotContain("Source: https://docs.pia-ai.de", page.Body, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("guides/speech")]
    [InlineData("guides/agent-runs")]
    [InlineData("guides/personas")]
    public void APagePathResolves(string path)
    {
        using var service = new HelpSearchService();

        var section = service.Read(path);

        Assert.NotNull(section);
        Assert.Equal(path, section.PagePath);
    }

    [Fact]
    public void ASearchHitsReferenceResolvesBackToTheSectionItCameFrom()
    {
        using var service = new HelpSearchService();

        var hits = service.Search("select a downloaded voice", 5);

        Assert.NotEmpty(hits);
        foreach (var hit in hits)
        {
            var section = service.Read(hit.Reference);
            Assert.NotNull(section);
            Assert.Equal(hit.Reference, section.Reference);
        }
    }

    [Fact]
    public void AnUnknownReferenceReturnsNullRatherThanTheWrongPage()
    {
        using var service = new HelpSearchService();

        Assert.Null(service.Read("guides/there-is-no-such-page"));
    }

    [Fact]
    public void ABarePageNameResolvesBecauseTheModelOftenShortensIt()
    {
        using var service = new HelpSearchService();

        var section = service.Read("speech");

        Assert.NotNull(section);
        Assert.Equal("guides/speech", section.PagePath);
    }
}
