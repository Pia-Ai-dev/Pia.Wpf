using Microsoft.Extensions.AI;
using Pia.Models;
using Xunit;
using ReasoningEffort = Pia.Models.ReasoningEffort;

namespace Pia.Wpf.Tests.Integration.Providers;

public class OllamaProviderHandlerTests
{
    private readonly ProviderIntegrationFixture _fixture = new();

    private static AiProvider? TryBuildProvider(ReasoningEffort effort = ReasoningEffort.None)
    {
        // Ollama requires no API key, so we gate on PIA_TEST_OLLAMA_ENDPOINT
        // being explicitly set. The default localhost endpoint isn't auto-
        // assumed: CI usually doesn't run Ollama.
        if (string.IsNullOrEmpty(ProviderTestEnvironment.GetEnv("PIA_TEST_OLLAMA_ENDPOINT"))) return null;
        var (endpoint, model) = ProviderTestEnvironment.Ollama();
        return new AiProvider
        {
            Name = "Ollama Integration",
            ProviderType = AiProviderType.Ollama,
            Endpoint = endpoint,
            ModelName = model,
            EncryptedApiKey = null,
            SupportsToolCalling = true,
            SupportsStreaming = true,
            TimeoutSeconds = 120,
            ReasoningEffort = effort,
        };
    }

    [Fact]
    public async Task SendRequestAsync_ReturnsCompletion()
    {
        var provider = TryBuildProvider();
        if (provider is null) { Assert.Skip("PIA_TEST_OLLAMA_ENDPOINT not set"); return; }

        var result = await _fixture.BuildClient().SendRequestAsync(provider, "Reply with the single word: ready.", TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(result.Text));
    }

    [Fact]
    public async Task StreamChatCompletionAsync_YieldsAtLeastOneDelta()
    {
        var provider = TryBuildProvider();
        if (provider is null) { Assert.Skip("PIA_TEST_OLLAMA_ENDPOINT not set"); return; }

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
    public async Task SendRequestAsync_WithMediumReasoning_DoesNotFail()
    {
        var provider = TryBuildProvider(ReasoningEffort.Medium);
        if (provider is null) { Assert.Skip("PIA_TEST_OLLAMA_ENDPOINT not set"); return; }

        var result = await _fixture.BuildClient().SendRequestAsync(provider, "Say 'ok'.", TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(result.Text));
    }
}
