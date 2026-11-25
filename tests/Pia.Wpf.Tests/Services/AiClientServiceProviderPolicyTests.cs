using System.Net.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Exceptions;
using Pia.Services.Interfaces;
using Pia.Services.Providers;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>The policy is checked ahead of every provider handler, so a refused provider never gets a request
/// and a switched-off web search never reaches the wire.</summary>
public class AiClientServiceProviderPolicyTests
{
    private readonly IAiProviderHandler _handler = Substitute.For<IAiProviderHandler>();
    private readonly AppSettings _settings = new();
    private AiProvider? _handedOver;

    public AiClientServiceProviderPolicyTests()
    {
        _handler.ProviderType.Returns(AiProviderType.OpenAI);
        _handler.CreateChatClientAsync(
                Arg.Any<AiProvider>(), Arg.Any<string?>(), Arg.Any<HttpClient>(),
                Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _handedOver = ci.ArgAt<AiProvider>(0);
                return Task.FromResult(TextClient());
            });
        _handler.CreateChatOptions(Arg.Any<AiProvider>(), Arg.Any<bool>()).Returns(_ => new ChatOptions());
    }

    [Fact]
    public async Task AProviderThePolicyRefuses_GetsNoRequest()
    {
        _settings.AllowedProviderTypes = [nameof(AiProviderType.AzureOpenAI)];

        await Assert.ThrowsAsync<ProviderBlockedByPolicyException>(() => DrainAsync(Provider()));

        await _handler.DidNotReceiveWithAnyArgs().CreateChatClientAsync(
            default!, default, default!, default, default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WithWebSearchSwitchedOffByPolicy_TheProviderIsHandedOverWithoutIt()
    {
        _settings.AllowProviderWebSearch = false;
        var provider = Provider();

        await DrainAsync(provider);

        Assert.NotNull(_handedOver);
        Assert.False(_handedOver!.UsesWebSearch);
        Assert.True(provider.EnableWebSearch);
    }

    [Fact]
    public async Task WithoutAPolicy_WebSearchReachesTheHandler()
    {
        await DrainAsync(Provider());

        Assert.True(_handedOver!.UsesWebSearch);
    }

    private async Task DrainAsync(AiProvider provider)
    {
        await foreach (var _ in CreateSut().GetChatCompletionWithToolsAsync(
            [new ChatMessage(ChatRole.User, "hi")], provider, cancellationToken: TestContext.Current.CancellationToken))
        {
        }
    }

    private AiClientService CreateSut()
    {
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(_settings);
        var throttle = Substitute.For<IProviderRequestThrottle>();
        throttle.AcquireAsync(Arg.Any<AiProvider>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IDisposable>(new NoOpPermit()));

        return new AiClientService(
            new DpapiHelper(NullLogger<DpapiHelper>.Instance),
            httpFactory,
            settings,
            new AiProviderHandlerResolver([_handler]),
            Substitute.For<IAuthService>(),
            NullLogger<AiClientService>.Instance,
            throttle);
    }

    private static AiProvider Provider() => new()
    {
        Name = "t",
        Endpoint = "https://api.openai.com/v1",
        ProviderType = AiProviderType.OpenAI,
        SupportsStreaming = true,
        SupportsToolCalling = false,
        EnableWebSearch = true,
    };

    private static IChatClient TextClient()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => TextStream());
        return chatClient;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> TextStream()
    {
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("final")] };
        await Task.Yield();
    }

    private sealed class NoOpPermit : IDisposable
    {
        public void Dispose() { }
    }
}
