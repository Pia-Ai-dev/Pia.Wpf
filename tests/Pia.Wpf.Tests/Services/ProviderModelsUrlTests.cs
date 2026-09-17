using Pia.Models;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

public class ProviderModelsUrlTests
{
    [Theory]
    [InlineData("https://api.anthropic.com", "https://api.anthropic.com/v1/models?limit=1000")]
    [InlineData("https://api.anthropic.com/", "https://api.anthropic.com/v1/models?limit=1000")]
    [InlineData("https://api.anthropic.com/v1", "https://api.anthropic.com/v1/models?limit=1000")]
    [InlineData("https://api.anthropic.com/v1/", "https://api.anthropic.com/v1/models?limit=1000")]
    public void Anthropic_lists_under_v1_whether_or_not_the_endpoint_carries_it(string endpoint, string expected)
        => Assert.Equal(expected, ProviderService.BuildModelsUrl(endpoint, AiProviderType.Anthropic));

    [Theory]
    [InlineData(AiProviderType.OpenAI, "https://api.openai.com/v1", "https://api.openai.com/v1/models")]
    [InlineData(AiProviderType.OpenAICompatible, "https://host/v1/", "https://host/v1/models")]
    [InlineData(AiProviderType.Ollama, "http://localhost:11434/v1", "http://localhost:11434/api/tags")]
    [InlineData(AiProviderType.Ollama, "http://localhost:11434", "http://localhost:11434/api/tags")]
    public void Other_providers_keep_their_own_paths(AiProviderType providerType, string endpoint, string expected)
        => Assert.Equal(expected, ProviderService.BuildModelsUrl(endpoint, providerType));
}
