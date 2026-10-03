using Pia.Models;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

public class AiClientServicePromptCacheLabelTests
{
    private static AiProvider Provider(AiProviderType type, bool providerCache = false) =>
        new() { Name = "p", Endpoint = "https://example.invalid", ProviderType = type, EnablePromptCache = providerCache };

    [Fact]
    public void AnAnthropicToolRequest_WithTheSettingOn_IsTurnedOnByTheCaller()
    {
        Assert.Equal(AiClientService.ToolLoopPromptCache,
            AiClientService.PromptCacheLabel(Provider(AiProviderType.Anthropic), toolLoopCacheEnabled: true, useTools: true));
    }

    [Fact]
    public void AProviderThatAlreadyCaches_IsLeftToItsOwnFlag()
    {
        Assert.Equal("provider",
            AiClientService.PromptCacheLabel(Provider(AiProviderType.Anthropic, providerCache: true), true, true));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void WithoutTheSettingOrWithoutTools_TheCacheStaysOff(bool settingOn, bool useTools)
    {
        Assert.Equal("off", AiClientService.PromptCacheLabel(Provider(AiProviderType.Anthropic), settingOn, useTools));
    }

    [Theory]
    [InlineData(AiProviderType.OpenAI)]
    [InlineData(AiProviderType.PiaCloud)]
    [InlineData(AiProviderType.Ollama)]
    public void OtherProviders_AreNotApplicable(AiProviderType type)
    {
        Assert.Equal("n/a", AiClientService.PromptCacheLabel(Provider(type), true, true));
    }
}
