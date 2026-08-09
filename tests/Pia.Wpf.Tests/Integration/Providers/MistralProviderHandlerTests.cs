using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.Tests.TestInfrastructure;
using Xunit;
using ReasoningEffort = Pia.Models.ReasoningEffort;

namespace Pia.Tests.Integration.Providers;

[Trait("Category", "Network")]
public class MistralProviderHandlerTests
{
    private readonly ProviderIntegrationFixture _fixture = new();

    private static AiProvider? TryBuildProvider(
        ReasoningEffort effort = ReasoningEffort.None,
        string? modelOverride = null)
    {
        var (endpoint, key, model) = ProviderTestEnvironment.Mistral();
        if (string.IsNullOrEmpty(key)) return null;
        return new AiProvider
        {
            Name = "Mistral Integration",
            ProviderType = AiProviderType.Mistral,
            Endpoint = endpoint,
            ModelName = modelOverride ?? model,
            EncryptedApiKey = key,
            SupportsToolCalling = true,
            SupportsStreaming = true,
            TimeoutSeconds = 60,
            ReasoningEffort = effort,
        };
    }

    [LiveApiFact]
    public async Task SendRequestAsync_ReturnsCompletion()
    {
        var provider = TryBuildProvider();
        if (provider is null) { Assert.Skip("PIA_TEST_MISTRAL_KEY not set"); return; }

        var result = await _fixture.BuildClient().SendRequestAsync(provider, "Reply with the single word: ready.", TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(result.Text));
    }

    [LiveApiFact]
    public async Task StreamChatCompletionAsync_YieldsAtLeastOneDelta()
    {
        var provider = TryBuildProvider();
        if (provider is null) { Assert.Skip("PIA_TEST_MISTRAL_KEY not set"); return; }

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

    [LiveApiFact]
    public async Task TestStreamingAsync_ReturnsTrue()
    {
        var provider = TryBuildProvider();
        if (provider is null) { Assert.Skip("PIA_TEST_MISTRAL_KEY not set"); return; }

        Assert.True(await _fixture.BuildClient().TestStreamingAsync(provider, TestContext.Current.CancellationToken));
    }

    [LiveApiTheory]
    [InlineData(ReasoningEffort.None)]
    [InlineData(ReasoningEffort.High)]
    public async Task SendRequestAsync_EachEffortLevel_Succeeds(ReasoningEffort effort)
    {
        var provider = TryBuildProvider(effort);
        if (provider is null) { Assert.Skip("PIA_TEST_MISTRAL_KEY not set"); return; }

        var result = await _fixture.BuildClient().SendRequestAsync(provider, "Say 'ok'.", TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(result.Text));
    }

    /// <summary>
    /// Regression: on a non-reasoning-capable model (mistral-large), the
    /// handler must NOT send `reasoning_effort` at all, even if the user
    /// configured one. Otherwise Mistral returns 422.
    /// </summary>
    [LiveApiFact]
    public async Task SendRequestAsync_WithIncompatibleModel_OmitsReasoningField()
    {
        var provider = TryBuildProvider(ReasoningEffort.High, modelOverride: "mistral-large-latest");
        if (provider is null) { Assert.Skip("PIA_TEST_MISTRAL_KEY not set"); return; }

        var result = await _fixture.BuildClient().SendRequestAsync(provider, "Say 'ok'.", TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(result.Text));
    }

    [LiveApiFact]
    public async Task SendRequestAsync_WithWebSearch_ReturnsCompletion()
    {
        var (endpoint, key, model) = ProviderTestEnvironment.Mistral();
        var agentId = ProviderTestEnvironment.MistralAgentId();
        if (string.IsNullOrEmpty(key)) { Assert.Skip("PIA_TEST_MISTRAL_KEY not set"); return; }
        if (string.IsNullOrEmpty(agentId)) { Assert.Skip("PIA_TEST_MISTRAL_AGENT_ID not set"); return; }

        var provider = new AiProvider
        {
            Name = "Mistral Agent Integration",
            ProviderType = AiProviderType.Mistral,
            Endpoint = endpoint,
            ModelName = model,
            EncryptedApiKey = key,
            SupportsToolCalling = true,
            SupportsStreaming = true,
            TimeoutSeconds = 60,
            EnableWebSearch = true,
            MistralAgentId = agentId,
        };

        var result = await _fixture.BuildClient().SendRequestAsync(provider, "Reply with the single word: ready.", TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(result.Text));
    }

    [LiveApiFact]
    public async Task StreamChatCompletionAsync_WithWebSearch_YieldsAtLeastOneDelta()
    {
        var (endpoint, key, model) = ProviderTestEnvironment.Mistral();
        var agentId = ProviderTestEnvironment.MistralAgentId();
        if (string.IsNullOrEmpty(key)) { Assert.Skip("PIA_TEST_MISTRAL_KEY not set"); return; }
        if (string.IsNullOrEmpty(agentId)) { Assert.Skip("PIA_TEST_MISTRAL_AGENT_ID not set"); return; }

        var provider = new AiProvider
        {
            Name = "Mistral Agent Integration (stream)",
            ProviderType = AiProviderType.Mistral,
            Endpoint = endpoint,
            ModelName = model,
            EncryptedApiKey = key,
            SupportsToolCalling = true,
            SupportsStreaming = true,
            TimeoutSeconds = 60,
            EnableWebSearch = true,
            MistralAgentId = agentId,
        };

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

}
