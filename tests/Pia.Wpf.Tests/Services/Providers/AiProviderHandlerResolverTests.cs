using System.Net.Http;
using Microsoft.Extensions.AI;
using NSubstitute;
using Pia.Models;
using Pia.Services.Providers;
using Xunit;

namespace Pia.Tests.Services.Providers;

public class AiProviderHandlerResolverTests
{
    [Fact]
    public void Get_ResolvesHandlerByProviderType()
    {
        var openAi = StubHandler(AiProviderType.OpenAI);
        var mistral = StubHandler(AiProviderType.Mistral);
        var resolver = new AiProviderHandlerResolver(new[] { openAi, mistral });

        Assert.Same(openAi, resolver.Get(AiProviderType.OpenAI));
        Assert.Same(mistral, resolver.Get(AiProviderType.Mistral));
    }

    [Fact]
    public void Get_ThrowsForUnknownProviderType()
    {
        var resolver = new AiProviderHandlerResolver(new[] { StubHandler(AiProviderType.OpenAI) });

        var ex = Assert.Throws<NotSupportedException>(() => resolver.Get(AiProviderType.Mistral));
        Assert.Contains("Mistral", ex.Message);
    }

    [Fact]
    public void Constructor_RegistersAllProvidedHandlers()
    {
        var types = new[]
        {
            AiProviderType.OpenAI,
            AiProviderType.AzureOpenAI,
            AiProviderType.Ollama,
            AiProviderType.Mistral,
            AiProviderType.OpenRouter,
            AiProviderType.OpenAICompatible,
            AiProviderType.VLlm,
            AiProviderType.PiaCloud,
        };
        var handlers = types.Select(StubHandler).ToArray();
        var resolver = new AiProviderHandlerResolver(handlers);

        foreach (var t in types)
        {
            Assert.NotNull(resolver.Get(t));
        }
    }

    private static IAiProviderHandler StubHandler(AiProviderType type)
    {
        var h = Substitute.For<IAiProviderHandler>();
        h.ProviderType.Returns(type);
        return h;
    }
}
