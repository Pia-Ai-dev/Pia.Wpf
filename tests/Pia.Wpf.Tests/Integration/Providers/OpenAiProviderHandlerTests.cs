using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.Tests.TestInfrastructure;
using Xunit;
using ReasoningEffort = Pia.Models.ReasoningEffort;

namespace Pia.Tests.Integration.Providers;

[Trait("Category", "Network")]
public class OpenAiProviderHandlerTests
{
    private readonly ProviderIntegrationFixture _fixture = new();

    private static AiProvider? TryBuildProvider(
        ReasoningEffort effort = ReasoningEffort.None,
        bool enableWebSearch = false)
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
            EnableWebSearch = enableWebSearch,
        };
    }

    [LiveApiFact]
    public async Task SendRequestAsync_ReturnsCompletion()
    {
        var provider = TryBuildProvider();
        if (provider is null) { Assert.Skip("PIA_TEST_OPENAI_KEY not set"); return; }

        var client = _fixture.BuildClient();
        var result = await client.SendRequestAsync(provider, "Reply with the single word: ready.", TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(result.Text));
    }

    [LiveApiFact]
    public async Task StreamChatCompletionAsync_YieldsAtLeastOneDelta()
    {
        var provider = TryBuildProvider();
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

    [LiveApiFact]
    public async Task TestStreamingAsync_ReturnsTrue()
    {
        var provider = TryBuildProvider();
        if (provider is null) { Assert.Skip("PIA_TEST_OPENAI_KEY not set"); return; }

        Assert.True(await _fixture.BuildClient().TestStreamingAsync(provider, TestContext.Current.CancellationToken));
    }

    [LiveApiFact]
    public async Task TestToolCallingAsync_ReturnsTrue()
    {
        var provider = TryBuildProvider();
        if (provider is null) { Assert.Skip("PIA_TEST_OPENAI_KEY not set"); return; }

        Assert.True(await _fixture.BuildClient().TestToolCallingAsync(provider, TestContext.Current.CancellationToken));
    }

    [LiveApiTheory]
    [InlineData(ReasoningEffort.None)]
    [InlineData(ReasoningEffort.Medium)]
    [InlineData(ReasoningEffort.High)]
    public async Task SendRequestAsync_EachEffortLevel_Succeeds(ReasoningEffort effort)
    {
        var provider = TryBuildProvider(effort);
        if (provider is null) { Assert.Skip("PIA_TEST_OPENAI_KEY not set"); return; }

        var result = await _fixture.BuildClient().SendRequestAsync(provider, "Say 'ok'.", TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(result.Text));
    }

    [LiveApiFact]
    public async Task SendRequestAsync_WithWebSearch_ReturnsCompletion()
    {
        var provider = TryBuildProvider(enableWebSearch: true);
        if (provider is null) { Assert.Skip("PIA_TEST_OPENAI_KEY not set"); return; }

        var result = await _fixture.BuildClient().SendRequestAsync(provider, "What is today's date? Answer briefly.", TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(result.Text));
    }
}
