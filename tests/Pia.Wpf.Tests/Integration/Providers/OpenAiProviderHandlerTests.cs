using Microsoft.Extensions.AI;
using Pia.Models;
using Xunit;
using ReasoningEffort = Pia.Models.ReasoningEffort;

namespace Pia.Wpf.Tests.Integration.Providers;

public class OpenAiProviderHandlerTests
{
    private readonly ProviderIntegrationFixture _fixture = new();

    private static AiProvider? TryBuildProvider(ReasoningEffort effort = ReasoningEffort.None)
    {
        var (endpoint, key, model) = ProviderTestEnvironment.OpenAi();
        if (string.IsNullOrEmpty(key)) return null;
        return new AiProvider
        {
            Name = "OpenAI Integration",
            ProviderType = AiProviderType.OpenAI,
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
        var provider = TryBuildProvider(ReasoningEffort.Minimal);
        if (provider is null) { Assert.Skip("PIA_TEST_OPENAI_KEY not set"); return; }

        var client = _fixture.BuildClient();
        var result = await client.SendRequestAsync(provider, "Reply with the single word: ready.", TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(result.Text));
    }

    [Fact]
    public async Task StreamChatCompletionAsync_YieldsAtLeastOneDelta()
    {
        var provider = TryBuildProvider(ReasoningEffort.Minimal);
        if (provider is null) { Assert.Skip("PIA_TEST_OPENAI_KEY not set"); return; }

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
        var provider = TryBuildProvider(ReasoningEffort.Minimal);
        if (provider is null) { Assert.Skip("PIA_TEST_OPENAI_KEY not set"); return; }

        Assert.True(await _fixture.BuildClient().TestStreamingAsync(provider, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TestToolCallingAsync_ReturnsTrue()
    {
        var provider = TryBuildProvider(ReasoningEffort.Minimal);
        if (provider is null) { Assert.Skip("PIA_TEST_OPENAI_KEY not set"); return; }

        Assert.True(await _fixture.BuildClient().TestToolCallingAsync(provider, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Regression test for the original 400/422 bug. OpenAI honours
    /// `reasoning_effort` natively; this call should succeed.
    /// </summary>
    [Fact]
    public async Task SendRequestAsync_WithMediumReasoning_DoesNotFail()
    {
        var provider = TryBuildProvider(ReasoningEffort.Medium);
        if (provider is null) { Assert.Skip("PIA_TEST_OPENAI_KEY not set"); return; }

        var result = await _fixture.BuildClient().SendRequestAsync(provider, "Say 'ok'.", TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(result.Text));
    }
}
