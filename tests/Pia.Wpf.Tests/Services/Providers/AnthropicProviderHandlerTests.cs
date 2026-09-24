using Pia.Models;
using Pia.Services.Providers;
using Xunit;

namespace Pia.Tests.Services.Providers;

public class AnthropicProviderHandlerTests
{
    private static AiProvider Provider(int? maxOutputTokens = null) => new()
    {
        Name = "Claude",
        ProviderType = AiProviderType.Anthropic,
        Endpoint = "https://api.anthropic.com",
        ModelName = "claude-opus-5",
        MaxOutputTokens = maxOutputTokens,
    };

    // Left unset the SDK adapter sends its own 1024, which truncates every answer.
    [Fact]
    public void CreateChatOptions_WithoutAProviderCap_StillBoundsOutputWellAbove1024()
    {
        var options = new AnthropicProviderHandler().CreateChatOptions(Provider(), hasTools: false);

        Assert.Equal(8192, options.MaxOutputTokens);
    }

    [Fact]
    public void CreateChatOptions_UsesTheProvidersOwnCapWhenSet()
    {
        var options = new AnthropicProviderHandler().CreateChatOptions(Provider(64_000), hasTools: false);

        Assert.Equal(64_000, options.MaxOutputTokens);
    }

    [Fact]
    public void ProviderType_IsAnthropic()
    {
        Assert.Equal(AiProviderType.Anthropic, new AnthropicProviderHandler().ProviderType);
    }
}
