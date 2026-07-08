using Pia.Services.Wiki;
using Xunit;

namespace Pia.Tests.Wiki;

/// <summary>
/// Unit tests for <see cref="AiIngestExtractionService.ParseTopics"/> — the defensive JSON/line parser
/// that turns model output into <c>ExtractedTopic</c> records. No provider required.
/// </summary>
public class AiIngestExtractionServiceTests
{
    [Fact]
    public void ParseTopics_reads_subject_and_category_json()
    {
        var topics = AiIngestExtractionService.ParseTopics(
            """[{"subject":"Pia","category":"product"},{"subject":"GDPR","category":"regulation"}]""");
        Assert.Equal(2, topics.Count);
        Assert.Equal("Pia", topics[0].Subject);
        Assert.Equal("regulation", topics[1].Category);
    }

    [Fact]
    public void ParseTopics_defaults_missing_category_to_concept()
    {
        var topics = AiIngestExtractionService.ParseTopics("""[{"subject":"WPF"}]""");
        Assert.Equal("concept", topics[0].Category);
    }

    [Fact]
    public void ParseTopics_returns_empty_for_empty_json_array()
    {
        Assert.Empty(AiIngestExtractionService.ParseTopics("[]"));
    }
}
