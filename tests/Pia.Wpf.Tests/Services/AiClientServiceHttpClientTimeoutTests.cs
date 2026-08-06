using System.Net.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Providers;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The HttpClient handed to a provider must not carry HttpClient's 100s default timeout: the per-method
/// timeoutCts is the only timeout authority on LLM calls, and a transport timeout firing first surfaces as
/// a bare TaskCanceledException instead of the LlmTimeoutException every catch downstream understands.
/// </summary>
public class AiClientServiceHttpClientTimeoutTests
{
    private sealed class NoOpPermit : IDisposable
    {
        public void Dispose() { }
    }

    [Fact]
    public async Task StreamingCompletion_HandsTheProviderAClient_WithoutTheDefaultTimeout()
    {
        HttpClient? captured = null;
        var handler = Substitute.For<IAiProviderHandler>();
        handler.ProviderType.Returns(AiProviderType.OpenAI);
        handler.CreateChatClientAsync(
                Arg.Any<AiProvider>(), Arg.Any<string?>(), Arg.Any<HttpClient>(),
                Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured = ci.ArgAt<HttpClient>(2);
                return Task.FromResult(TextClient());
            });
        handler.CreateChatOptions(Arg.Any<AiProvider>(), Arg.Any<bool>()).Returns(_ => new ChatOptions());

        // The factory's client arrives with the framework default (100s) — exactly what the SUT must remove.
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());

        var throttle = Substitute.For<IProviderRequestThrottle>();
        throttle.AcquireAsync(Arg.Any<AiProvider>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IDisposable>(new NoOpPermit()));

        var sut = new AiClientService(
            new DpapiHelper(NullLogger<DpapiHelper>.Instance),
            httpFactory,
            settings,
            new AiProviderHandlerResolver([handler]),
            Substitute.For<IAuthService>(),
            NullLogger<AiClientService>.Instance,
            throttle);

        var provider = new AiProvider
        {
            Name = "t",
            Endpoint = "http://localhost",
            ProviderType = AiProviderType.OpenAI,
            SupportsStreaming = true,
            SupportsToolCalling = false,
        };

        var sawText = false;
        await foreach (var item in sut.GetChatCompletionWithToolsAsync(
            [new ChatMessage(ChatRole.User, "hi")], provider, cancellationToken: CancellationToken.None))
        {
            if (item is TextDelta) sawText = true;
        }

        Assert.True(sawText);
        Assert.NotNull(captured);
        Assert.Equal(Timeout.InfiniteTimeSpan, captured!.Timeout);
    }

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
}
