using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.Wiki;
using Xunit;

namespace Pia.Tests.Wiki;

/// <summary>
/// Unit tests for <see cref="AiIngestExtractionService"/> — the defensive JSON/line parser that turns
/// model output into <c>ExtractedTopic</c> records, and which provider the discovery call goes to.
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

    [Fact]
    public async Task DiscoverTopicsAsync_asks_the_assistant_mode_provider_not_the_first_in_the_list()
    {
        var listFirst = new AiProvider { Name = "Pia Cloud", Endpoint = "http://cloud" };
        var assistant = new AiProvider { Name = "DeepSeek", Endpoint = "http://deepseek" };
        var providers = Substitute.For<IProviderService>();
        providers.GetDefaultProviderAsync().Returns(listFirst);
        providers.GetDefaultProviderForModeAsync(WindowMode.Assistant).Returns(assistant);

        var ai = Substitute.For<IAiClientService>();
        ai.SendRequestAsync(Arg.Any<AiProvider>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
            .Returns(new AiCompletionResult("[]", 0));

        var svc = new AiIngestExtractionService(
            ai, providers, NullLogger<AiIngestExtractionService>.Instance);

        await svc.DiscoverTopicsAsync("some raw text", "charter", TestContext.Current.CancellationToken);

        await ai.Received(1).SendRequestAsync(
            assistant, Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<string>());
        await ai.DidNotReceive().SendRequestAsync(
            listFirst, Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<string>());
    }
}
