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
        if (provider is null) { Assert.Skip("PIA_TEST_VLLM_ENDPOINT not set"); return; }

        var result = await _fixture.BuildClient().SendRequestAsync(provider, "Say 'ok'.", TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(result.Text));
    }
}
