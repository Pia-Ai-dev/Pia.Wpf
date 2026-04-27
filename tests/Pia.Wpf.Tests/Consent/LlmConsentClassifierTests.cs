using System.Net.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Services.Consent;
using Xunit;

namespace Pia.Wpf.Tests.Consent;

public sealed class LlmConsentClassifierTests
{
    [Fact]
    public async Task NonEuEndpoint_ReturnsAmbiguousZero_WithoutCallingClient()
    {
        var client = Substitute.For<IChatClient>();
        var sut = new LlmConsentClassifier(client, isEuEndpoint: false, NullLogger<LlmConsentClassifier>.Instance);

        var result = await sut.ClassifyAsync("ja", "Darf ich aufzeichnen?");

        Assert.Equal(ConsentDecision.Ambiguous, result.Decision);
        Assert.Equal(0.0f, result.Confidence);
        await client.DidNotReceiveWithAnyArgs().GetResponseAsync(default!, default, default);
    }

    [Fact]
    public async Task ValidJsonGrant_ParsesIntoGrant()
    {
        var sut = new LlmConsentClassifier(
            BuildClientReturning("{\"decision\":\"grant\",\"confidence\":0.92,\"reason\":\"clear yes\"}"),
            isEuEndpoint: true,
            NullLogger<LlmConsentClassifier>.Instance);

        var result = await sut.ClassifyAsync("ja klar", "p");
        Assert.Equal(ConsentDecision.Grant, result.Decision);
        Assert.Equal(0.92f, result.Confidence, 2);
    }

    [Fact]
    public async Task ValidJsonDeny_ParsesIntoDeny()
    {
        var sut = new LlmConsentClassifier(
            BuildClientReturning("{\"decision\":\"deny\",\"confidence\":0.81,\"reason\":\"refused\"}"),
            isEuEndpoint: true,
            NullLogger<LlmConsentClassifier>.Instance);

        var result = await sut.ClassifyAsync("auf keinen fall", "p");
        Assert.Equal(ConsentDecision.Deny, result.Decision);
    }

    [Fact]
    public async Task MalformedJson_ClampsToAmbiguousZero()
    {
        var sut = new LlmConsentClassifier(
            BuildClientReturning("totally not json"),
            isEuEndpoint: true,
            NullLogger<LlmConsentClassifier>.Instance);

        var result = await sut.ClassifyAsync("hmm", "p");
        Assert.Equal(ConsentDecision.Ambiguous, result.Decision);
        Assert.Equal(0.0f, result.Confidence);
    }

    [Fact]
    public async Task ClientThrows_NeverPropagates_ReturnsAmbiguous()
    {
        var client = Substitute.For<IChatClient>();
        client
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns<Task<ChatResponse>>(_ => throw new HttpRequestException("boom"));

        var sut = new LlmConsentClassifier(client, isEuEndpoint: true, NullLogger<LlmConsentClassifier>.Instance);

        var result = await sut.ClassifyAsync("ja", "p");
        Assert.Equal(ConsentDecision.Ambiguous, result.Decision);
        Assert.Equal(0.0f, result.Confidence);
    }

    [Fact]
    public async Task JsonWithSurroundingProse_StillParses()
    {
        var sut = new LlmConsentClassifier(
            BuildClientReturning("Sure! {\"decision\":\"grant\",\"confidence\":0.7} done"),
            isEuEndpoint: true,
            NullLogger<LlmConsentClassifier>.Instance);

        var result = await sut.ClassifyAsync("ja", "p");
        Assert.Equal(ConsentDecision.Grant, result.Decision);
    }

    private static IChatClient BuildClientReturning(string text)
    {
        var client = Substitute.For<IChatClient>();
        client
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))));
        return client;
    }
}
