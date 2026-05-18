using Microsoft.Extensions.AI;
using Pia.Models;
using Xunit;
using ReasoningEffort = Pia.Models.ReasoningEffort;

namespace Pia.Wpf.Tests.Integration.Providers;

public class VLlmProviderHandlerTests
{
    private readonly ProviderIntegrationFixture _fixture = new();

    private static AiProvider? TryBuildProvider(ReasoningEffort effort = ReasoningEffort.None)
    {
        if (string.IsNullOrEmpty(ProviderTestEnvironment.GetEnv("PIA_TEST_VLLM_ENDPOINT"))) return null;
        var (endpoint, model) = ProviderTestEnvironment.VLlm();
        return new AiProvider
        {
            Name = "vLLM Integration",
            ProviderType = AiProviderType.VLlm,
            Endpoint = endpoint,
            ModelName = model,
            EncryptedApiKey = null,
            SupportsToolCalling = false,
            SupportsStreaming = true,
            TimeoutSeconds = 120,
            ReasoningEffort = effort,
        };
    }

    [Fact]
    public async Task SendRequestAsync_ReturnsCompletion()
    {
        var provider = TryBuildProvider();
        if (provider is null) { Assert.Skip("PIA_TEST_VLLM_ENDPOINT not set"); return; }

        var result = await _fixture.BuildClient().SendRequestAsync(provider, "Reply with the single word: ready.", TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(result.Text));
    }

    [Fact]
    public async Task StreamChatCompletionAsync_YieldsAtLeastOneDelta()
    {
        var provider = TryBuildProvider();
        if (provider is null) { Assert.Skip("PIA_TEST_VLLM_ENDPOINT not set"); return; }

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

    /// <summary>
    /// Regression: vLLM rejects unknown fields like `reasoning_effort` on some
    /// models. The handler must strip that field and inject
    /// `chat_template_kwargs.enable_thinking` for Qwen3-family templates.
    /// </summary>
    [Fact]
    public async Task SendRequestAsync_WithMediumReasoning_DoesNotFail()
    {
        var provider = TryBuildProvider(ReasoningEffort.Medium);
        if (provider is null) { Assert.Skip("PIA_TEST_VLLM_ENDPOINT not set"); return; }

        var result = await _fixture.BuildClient().SendRequestAsync(provider, "Say 'ok'.", TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(result.Text));
    }
}
