using Pia.Models;
using Pia.Services.Providers;
using Xunit;

namespace Pia.Tests.Services.Providers;

public class ProviderPolicyTests
{
    [Fact]
    public void WithoutAPolicy_EveryProviderIsAllowed()
    {
        var settings = new AppSettings();

        Assert.True(ProviderPolicy.IsAllowed(Provider(AiProviderType.OpenAI, "https://api.openai.com/v1"), settings));
        Assert.True(ProviderPolicy.IsAllowed(Provider(AiProviderType.Ollama, "not a url"), null));
    }

    [Fact]
    public void AnEmptyList_RestrictsNothing()
    {
        var settings = new AppSettings { AllowedProviderTypes = [], AllowedProviderEndpoints = [] };

        Assert.True(ProviderPolicy.IsAllowed(Provider(AiProviderType.Anthropic, "https://api.anthropic.com"), settings));
    }

    [Theory]
    [InlineData(AiProviderType.AzureOpenAI, true)]
    [InlineData(AiProviderType.OpenAI, false)]
    [InlineData(AiProviderType.PiaCloud, true)]
    public void TheTypeList_IsMatchedByName_AndNeverShutsOutPiaCloud(AiProviderType type, bool allowed)
    {
        var settings = new AppSettings { AllowedProviderTypes = [" azureopenai "] };

        Assert.Equal(allowed, ProviderPolicy.IsTypeAllowed(type, settings));
    }

    [Theory]
    [InlineData("https://llm.corp.example/v1", true)]
    [InlineData("https://eu.openai.azure.com", true)]
    [InlineData("https://openai.azure.com", false)]
    [InlineData("https://api.openai.com/v1", false)]
    [InlineData("https://llm.corp.example.attacker.test", false)]
    [InlineData("not a url", false)]
    public void TheEndpointList_MatchesExactHostsAndSubdomainWildcards(string endpoint, bool allowed)
    {
        var settings = new AppSettings { AllowedProviderEndpoints = ["llm.corp.example", "*.openai.azure.com"] };

        Assert.Equal(allowed, ProviderPolicy.IsEndpointAllowed(AiProviderType.OpenAICompatible, endpoint, settings));
    }

    [Fact]
    public void AnEndpointPatternWrittenAsAUrl_IsReducedToItsHost()
    {
        var settings = new AppSettings { AllowedProviderEndpoints = ["https://llm.corp.example:8443/v1"] };

        Assert.True(ProviderPolicy.IsEndpointAllowed(AiProviderType.OpenAICompatible, "http://llm.corp.example/v1", settings));
    }

    [Fact]
    public void PiaCloud_HasNoEndpointToCheck()
    {
        var settings = new AppSettings { AllowedProviderEndpoints = ["llm.corp.example"] };

        Assert.True(ProviderPolicy.IsAllowed(Provider(AiProviderType.PiaCloud, ""), settings));
    }

    private static AiProvider Provider(AiProviderType type, string endpoint) =>
        new() { Name = "p", ProviderType = type, Endpoint = endpoint };
}
