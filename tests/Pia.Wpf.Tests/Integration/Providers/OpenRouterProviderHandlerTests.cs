using Microsoft.Extensions.AI;
using Pia.Models;
using Xunit;
using ReasoningEffort = Pia.Models.ReasoningEffort;

namespace Pia.Wpf.Tests.Integration.Providers;

public class OpenRouterProviderHandlerTests
{
    private readonly ProviderIntegrationFixture _fixture = new();

    private static AiProvider? TryBuildProvider(ReasoningEffort effort = ReasoningEffort.None)
    {
        var (endpoint, key, model) = ProviderTestEnvironment.OpenRouter();
        if (string.IsNullOrEmpty(key)) return null;
        return new AiProvider
        {
            Name = "OpenRouter Integration",
            ProviderType = AiProviderType.OpenRouter,
            Endpoint = endpoint,
            ModelName = model,
            EncryptedApiKey = key,
            SupportsToolCalling = true,
            SupportsStreaming = true,
            TimeoutSeconds = 60,
            ReasoningEffort = effort,
        };
    }

    [Fact]
    public async Task SendRequestAsync_ReturnsCompletion()
    {
        var provider = TryBuildProvider();
        if (provider is null) { Assert.Skip("PIA_TEST_OPENROUTER_KEY not set"); return; }

        var result = await _fixture.BuildClient().SendRequestAsync(provider, "Reply with the single word: ready.", TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(result.Text));
    }

    [Fact]
    public async Task StreamChatCompletionAsync_YieldsAtLeastOneDelta()
    {
        var provider = TryBuildProvider();
        if (provider is null) { Assert.Skip("PIA_TEST_OPENROUTER_KEY not set"); return; }

        var client = _fixture.BuildClient();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Reply with the single word: ready."),
        };

        var any = false;
        await foreach (var token in client.StreamChatCompletionAsync(messages, provider, cancellationToken: TestContext.Current.CancellationToken))
        {
            if (!string.IsNullOrEmpty(token)) { any = true; break; }
        }
        Assert.True(any);
    }

    [Fact]
    public async Task TestStreamingAsync_ReturnsTrue()
    {
        var provider = TryBuildProvider();
        if (provider is null) { Assert.Skip("PIA_TEST_OPENROUTER_KEY not set"); return; }

        Assert.True(await _fixture.BuildClient().TestStreamingAsync(provider, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(ReasoningEffort.None)]
    [InlineData(ReasoningEffort.Minimal)]
    [InlineData(ReasoningEffort.Low)]
    [InlineData(ReasoningEffort.Medium)]
    [InlineData(ReasoningEffort.High)]
    [InlineData(ReasoningEffort.XHigh)]
    public async Task SendRequestAsync_EachEffortLevel_Succeeds(ReasoningEffort effort)
    {
        var provider = TryBuildProvider(effort);
        if (provider is null) { Assert.Skip("PIA_TEST_OPENROUTER_KEY not set"); return; }

        var result = await _fixture.BuildClient().SendRequestAsync(provider, "Say 'ok'.", TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(result.Text));
    }
}
