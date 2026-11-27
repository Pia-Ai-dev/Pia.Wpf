using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Tests.Architecture;
using Xunit;

namespace Pia.Tests.Infrastructure;

public class NoRedirectHttpClientTests
{
    [Fact]
    public void TheNoRedirectClient_FollowsNoRedirect_AndKeepsTheHandlerChain()
    {
        using var provider = BuildProvider();

        var chain = Chain(provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(HttpClientNames.NoRedirect));

        Assert.False(chain.OfType<HttpClientHandler>().Single().AllowAutoRedirect);
        Assert.Single(chain.OfType<RateLimitRetryHandler>());
    }

    [Fact]
    public void TheDefaultClient_StillFollowsRedirects()
    {
        using var provider = BuildProvider();

        var chain = Chain(provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(""));

        Assert.True(chain.OfType<HttpClientHandler>().Single().AllowAutoRedirect);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = BootstrapperGraphValidationTests.BuildConfiguredServices();
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        services.Replace(ServiceDescriptor.Singleton(settings));
        return services.BuildServiceProvider();
    }

    private static List<HttpMessageHandler> Chain(HttpMessageHandler handler)
    {
        var chain = new List<HttpMessageHandler>();
        for (HttpMessageHandler? h = handler; h is not null; h = (h as DelegatingHandler)?.InnerHandler)
            chain.Add(h);
        return chain;
    }
}
