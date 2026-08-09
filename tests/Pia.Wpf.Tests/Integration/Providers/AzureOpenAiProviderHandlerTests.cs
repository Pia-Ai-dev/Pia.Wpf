using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.Tests.TestInfrastructure;
using Xunit;
using ReasoningEffort = Pia.Models.ReasoningEffort;

namespace Pia.Tests.Integration.Providers;

[Trait("Category", "Network")]
public class AzureOpenAiProviderHandlerTests
{
    private readonly ProviderIntegrationFixture _fixture = new();

    private static AiProvider? TryBuildProvider(ReasoningEffort effort = ReasoningEffort.None)
    {
        var (endpoint, key, deployment) = ProviderTestEnvironment.AzureOpenAi();
        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(key)) return null;
        return new AiProvider
        {
            Name = "Azure OpenAI Integration",
            ProviderType = AiProviderType.AzureOpenAI,
            Endpoint = endpoint,
            AzureDeploymentName = deployment,
            ModelName = deployment,
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
        if (provider is null) { Assert.Skip("PIA_TEST_AZURE_ENDPOINT/KEY not set"); return; }

        var result = await _fixture.BuildClient().SendRequestAsync(provider, "Reply with the single word: ready.", TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(result.Text));
    }

    [LiveApiFact]
    public async Task TestStreamingAsync_ReturnsTrue()
    {
        var provider = TryBuildProvider();
        if (provider is null) { Assert.Skip("PIA_TEST_AZURE_ENDPOINT/KEY not set"); return; }

        Assert.True(await _fixture.BuildClient().TestStreamingAsync(provider, TestContext.Current.CancellationToken));
    }

    [LiveApiTheory]
    [InlineData(ReasoningEffort.None)]
    [InlineData(ReasoningEffort.Medium)]
    [InlineData(ReasoningEffort.High)]
    public async Task SendRequestAsync_EachEffortLevel_Succeeds(ReasoningEffort effort)
    {
        var provider = TryBuildProvider(effort);
        if (provider is null) { Assert.Skip("PIA_TEST_AZURE_ENDPOINT/KEY not set"); return; }

        var result = await _fixture.BuildClient().SendRequestAsync(provider, "Say 'ok'.", TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(result.Text));
    }
}
