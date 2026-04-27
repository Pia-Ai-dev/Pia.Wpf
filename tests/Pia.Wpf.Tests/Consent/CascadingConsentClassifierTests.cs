using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Services.Consent;
using Xunit;

namespace Pia.Wpf.Tests.Consent;

public sealed class CascadingConsentClassifierTests
{
    [Fact]
    public async Task RuleConfidenceAboveThreshold_DoesNotCallLlm()
    {
        var rule = new RuleBasedConsentClassifier();
        var client = Substitute.For<IChatClient>();
        var llm = new LlmConsentClassifier(client, isEuEndpoint: true, NullLogger<LlmConsentClassifier>.Instance);
        var sut = new CascadingConsentClassifier(rule, llm, NullLogger<CascadingConsentClassifier>.Instance);

        // "ja" → rule returns Grant 0.95.
        var result = await sut.ClassifyAsync("ja", "p");

        Assert.Equal(ConsentDecision.Grant, result.Decision);
        Assert.Equal(0.95f, result.Confidence, 2);
        await client.DidNotReceiveWithAnyArgs().GetResponseAsync(default!, default, default);
    }

    [Fact]
    public async Task RuleLowConfidence_BothAmbiguous_StaysAmbiguous()
    {
        var rule = new RuleBasedConsentClassifier();
        var client = ClientReturning("{\"decision\":\"ambiguous\",\"confidence\":0.4}");
        var llm = new LlmConsentClassifier(client, isEuEndpoint: true, NullLogger<LlmConsentClassifier>.Instance);
        var sut = new CascadingConsentClassifier(rule, llm, NullLogger<CascadingConsentClassifier>.Instance);

        var result = await sut.ClassifyAsync("hmm naja", "p");

        Assert.Equal(ConsentDecision.Ambiguous, result.Decision);
    }

    [Fact]
    public async Task RuleAmbiguous_LlmGrants_PromotesToGrant()
    {
        var rule = new RuleBasedConsentClassifier();
        var client = ClientReturning("{\"decision\":\"grant\",\"confidence\":0.88}");
        var llm = new LlmConsentClassifier(client, isEuEndpoint: true, NullLogger<LlmConsentClassifier>.Instance);
        var sut = new CascadingConsentClassifier(rule, llm, NullLogger<CascadingConsentClassifier>.Instance);

        // Vague text — rule returns Ambiguous, LLM (mocked) says grant.
        var result = await sut.ClassifyAsync("schon ja vermutlich", "p");

        Assert.Equal(ConsentDecision.Grant, result.Decision);
        Assert.True(result.Confidence > 0.88f);
    }

    [Fact]
    public async Task EmptyInput_TriggersLlmFallback()
    {
        var rule = new RuleBasedConsentClassifier();
        var client = ClientReturning("{\"decision\":\"deny\",\"confidence\":0.92}");
        var llm = new LlmConsentClassifier(client, isEuEndpoint: true, NullLogger<LlmConsentClassifier>.Instance);
        var sut = new CascadingConsentClassifier(rule, llm, NullLogger<CascadingConsentClassifier>.Instance);

        var result = await sut.ClassifyAsync("???", "p");

        // Empty/garbage gives rule low confidence; LLM is consulted; here it says deny.
        Assert.Equal(ConsentDecision.Deny, result.Decision);
        await client.ReceivedWithAnyArgs(1).GetResponseAsync(default!, default, default);
    }

    private static IChatClient ClientReturning(string text)
    {
        var client = Substitute.For<IChatClient>();
        client
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))));
        return client;
    }
}
