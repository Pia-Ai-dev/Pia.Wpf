using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>The latch is what makes a mid-session Privacy change restart-worthy, so both places that take
/// the decision have to record the value they took it from.</summary>
[Collection("TokenizationLatchStatic")]
public class TokenizationLatchTests : IDisposable
{
    public TokenizationLatchTests() => TokenizationLatch.Reset();

    public void Dispose() => TokenizationLatch.Reset();

    private static TokenizingAiClientService CreateDecorator()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IServiceScopeFactory)).Returns(Substitute.For<IServiceScopeFactory>());

        var inner = Substitute.For<IAiClientService>();
        inner.SendRequestAsync(Arg.Any<AiProvider>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(new AiCompletionResult("answered", 0));

        return new TokenizingAiClientService(
            inner,
            serviceProvider,
            settings,
            NullLogger<TokenizingAiClientService>.Instance);
    }

    private static AiProvider Provider() =>
        new() { Name = "t", Endpoint = "http://localhost", ProviderType = AiProviderType.OpenAI };

    private static PrivacySettings Customised() =>
        new() { PiiKeywords = [new PiiKeywordEntry { Keyword = "acme", Category = "Custom" }] };

    [Fact]
    public async Task ReadingThePrivacySettingLatchesTheValueItRead()
    {
        var tokenMap = Substitute.For<ITokenMapService>();
        tokenMap.TokenizeStructuredResult(Arg.Any<string>()).Returns(ci => (string)ci[0]);
        tokenMap.Detokenize(Arg.Any<string>()).Returns(ci => (string)ci[0]);

        TokenMapAmbient.Current = tokenMap;
        try
        {
            await CreateDecorator().SendRequestAsync(Provider(), "hi", TestContext.Current.CancellationToken);
        }
        finally
        {
            TokenMapAmbient.Current = null;
        }

        Assert.True(TokenizationLatch.IsLatched);
        Assert.False(TokenizationLatch.IsStale(new PrivacySettings()));
        Assert.True(TokenizationLatch.IsStale(new PrivacySettings { TokenizationEnabled = false }));
    }

    [Fact]
    public async Task AMissingTokenMapLatchesToo()
    {
        // It resolves no map again for the rest of the process, so the decision is just as final.
        await CreateDecorator().SendRequestAsync(Provider(), "hi", TestContext.Current.CancellationToken);

        Assert.True(TokenizationLatch.IsLatched);
        // The setting was never read, so nothing can be shown to still match it.
        Assert.True(TokenizationLatch.IsStale(new PrivacySettings()));
    }

    [Fact]
    public void AnUndecidedProcessIsNeverStale()
    {
        Assert.False(TokenizationLatch.IsLatched);
        Assert.False(TokenizationLatch.IsStale(new PrivacySettings { TokenizationEnabled = false }));
    }

    [Fact]
    public void OnlyADifferentValueIsStale()
    {
        TokenizationLatch.Latch(Customised());

        Assert.False(TokenizationLatch.IsStale(Customised()));
        Assert.True(TokenizationLatch.IsStale(new PrivacySettings()));
    }
}
