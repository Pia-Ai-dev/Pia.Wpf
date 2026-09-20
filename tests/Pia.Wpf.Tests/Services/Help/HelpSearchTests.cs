using Pia.Services.Help;
using Xunit;

namespace Pia.Tests.Services.Help;

/// <summary>
/// The questions users actually ask. Each one used to be answered by a web search or an invention, so
/// each is pinned to the page that answers it rather than to a search-engine-shaped notion of relevance.
/// </summary>
public sealed class HelpSearchTests
{
    [Theory]
    [InlineData("can I perform agentic tasks", "guides/agent-runs")]
    [InlineData("can you change the way you answer", "guides/personas")]
    [InlineData("how can I change the tts output language", "guides/speech")]
    [InlineData("where do I set up a second provider", "guides/ai-providers")]
    [InlineData("how do I stop a reply that is generating", "guides/assistant")]
    [InlineData("what happens to my data", "guides/where-your-data-goes")]
    [InlineData("schedule something to run every morning", "guides/scheduled-jobs")]
    [InlineData("can Pia edit files on my computer", "guides/coding-tools")]
    [InlineData("does my data leave my computer", "guides/where-your-data-goes")]
    public void ADrivingQuestionFindsThePageThatAnswersIt(string question, string expectedPage)
    {
        using var service = new HelpSearchService();

        var hits = service.Search(question, 5);

        Assert.True(
            hits.Take(3).Any(h => h.Reference.StartsWith(expectedPage, StringComparison.OrdinalIgnoreCase)),
            $"'{question}' should reach {expectedPage} in the top 3, got: {string.Join(", ", hits.Select(h => h.Reference))}");
    }

    [Theory]
    [InlineData("text-to-speech: what's TTS?")]
    [InlineData("agent mode (what is it)")]
    [InlineData("\"quoted phrase\" AND OR NOT")]
    [InlineData("*")]
    [InlineData("a -- b ++ c")]
    public void PunctuationDoesNotThrowAnFts5SyntaxError(string query)
    {
        using var service = new HelpSearchService();

        var hits = service.Search(query, 5);

        Assert.NotNull(hits);
    }

    [Fact]
    public void AQueryWithNoSearchableWordsReturnsNothingRatherThanEverything()
    {
        using var service = new HelpSearchService();

        Assert.Empty(service.Search("!!! ???", 5));
        Assert.Empty(service.Search("   ", 5));
    }

    [Fact]
    public void EveryHitCarriesASnippetAndALinkTheUserCanCheck()
    {
        using var service = new HelpSearchService();

        var hits = service.Search("download a voice", 5);

        Assert.NotEmpty(hits);
        foreach (var hit in hits)
        {
            Assert.False(string.IsNullOrWhiteSpace(hit.PageTitle));
            Assert.False(string.IsNullOrWhiteSpace(hit.Snippet));
            Assert.StartsWith("https://docs.pia-ai.de/", hit.Url, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheCorpusIsEnglishSoAnUntranslatedQuestionMissesRatherThanAnsweringWrongly()
    {
        using var service = new HelpSearchService();

        // The tool description tells the model to translate; this pins that the fallback is a miss,
        // not a confident hit on an unrelated page.
        var hits = service.Search("Wie ändere ich die Ausgabesprache der Sprachausgabe?", 5);

        Assert.DoesNotContain(hits, h => h.Reference.StartsWith("guides/todos", StringComparison.Ordinal));
    }

    [Fact]
    public void SectionsSplitOnHeadingsSoAHitNamesTheSectionNotJustThePage()
    {
        using var service = new HelpSearchService();

        var page = service.Pages.Single(p => p.Path == "guides/speech");
        var sections = HelpSectionParser.Split(page);

        Assert.True(sections.Count > 3, $"expected the speech guide to split into several sections, got {sections.Count}");
        Assert.Contains(sections, s => s.Heading.Contains("Text-to-Speech", StringComparison.OrdinalIgnoreCase));
        Assert.All(sections, s => Assert.StartsWith("guides/speech", s.Reference, StringComparison.Ordinal));
    }

    [Fact]
    public void AHeadingInsideAFencedCodeBlockDoesNotStartASection()
    {
        var page = new HelpPage("test/page", "Test", "", "https://docs.pia-ai.de/test/", """
            Intro text.

            ## Real Heading

            ```bash
            # not a heading
            ## also not a heading
            ```

            Tail text.
            """);

        var sections = HelpSectionParser.Split(page);

        Assert.Equal(2, sections.Count);
        Assert.Contains("## also not a heading", sections[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoIdenticalHeadingsOnAPageGetDistinctReferences()
    {
        var page = new HelpPage("test/page", "Test", "", "https://docs.pia-ai.de/test/", """
            ## Settings

            First.

            ## Settings

            Second.
            """);

        var sections = HelpSectionParser.Split(page);

        Assert.Equal(2, sections.Count);
        Assert.NotEqual(sections[0].Reference, sections[1].Reference);
    }
}
