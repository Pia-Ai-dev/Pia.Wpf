using System.Diagnostics;
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

/// <summary>
/// What the provider timeout bounds in <see cref="AiClientService.GetChatCompletionWithToolsAsync"/>: one
/// round-trip, re-armed each round. A budget spanning the whole call cancels a provider that answered every
/// round within seconds, because the step's wall clock also carries the tool dispatches between them.
/// </summary>
public class AiClientServiceRoundTimeoutTests
{
    private const int TimeoutSeconds = 1;

    private sealed class NoOpPermit : IDisposable
    {
        public void Dispose() { }
    }

    private static AiProvider TestProvider() => new()
    {
        Name = "t",
        Endpoint = "http://localhost",
        ProviderType = AiProviderType.OpenAI,
        SupportsStreaming = true,
        SupportsToolCalling = true,
        TimeoutSeconds = TimeoutSeconds,
    };

    private static AiClientService Build(IChatClient chatClient)
    {
        var handler = Substitute.For<IAiProviderHandler>();
        handler.ProviderType.Returns(AiProviderType.OpenAI);
        handler.CreateChatClientAsync(
                Arg.Any<AiProvider>(), Arg.Any<string?>(), Arg.Any<HttpClient>(),
                Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(chatClient));
        handler.CreateChatOptions(Arg.Any<AiProvider>(), Arg.Any<bool>()).Returns(_ => new ChatOptions());

        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());

        var throttle = Substitute.For<IProviderRequestThrottle>();
        throttle.AcquireAsync(Arg.Any<AiProvider>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IDisposable>(new NoOpPermit()));

        return new AiClientService(
            new DpapiHelper(NullLogger<DpapiHelper>.Instance),
            httpFactory,
            settings,
            new AiProviderHandlerResolver([handler]),
            Substitute.For<IAuthService>(),
            NullLogger<AiClientService>.Instance,
            Substitute.For<IProviderRequestThrottle>());
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ToolCallRound(string callId)
    {
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new FunctionCallContent(callId, "some_tool", new Dictionary<string, object?>())],
        };
        await Task.Yield();
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> FinalAnswer()
    {
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("final")] };
        await Task.Yield();
    }

    private static IChatClient StreamingClient(int toolRounds)
    {
        var round = 0;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => round++ < toolRounds ? ToolCallRound($"call-{round}") : FinalAnswer());
        return chatClient;
    }

    /// <summary>
    /// Ten instant rounds whose dispatches add up to well past the timeout. Each round is charged only its own
    /// request plus the dispatch it asked for; the next round starts on a fresh budget.
    /// </summary>
    [Fact]
    public async Task ToolDispatchTimeAcrossRounds_DoesNotTimeOutTheStep()
    {
        var sut = Build(StreamingClient(toolRounds: 10));
        var answered = false;
        var stopwatch = Stopwatch.StartNew();

        await foreach (var item in sut.GetChatCompletionWithToolsAsync(
            [new ChatMessage(ChatRole.User, "go")],
            TestProvider(),
            tools: null,
            toolHandler: async (_, _) =>
            {
                await Task.Delay(150, TestContext.Current.CancellationToken);
                return "ok";
            },
            cancellationToken: TestContext.Current.CancellationToken))
        {
            if (item is TextDelta { Text: "final" }) answered = true;
        }

        stopwatch.Stop();
        Assert.True(answered, "the loop never reached its final answer");
        // Non-vacuity: a run that finished inside one budget would pass under a per-CALL timeout too.
        Assert.True(stopwatch.Elapsed > TimeSpan.FromSeconds(TimeoutSeconds),
            $"the run took {stopwatch.ElapsedMilliseconds}ms, which never exceeds the {TimeoutSeconds}s budget");
    }

    /// <summary>
    /// The other half: re-arming per round must not stop a round that really does outlast the budget from
    /// timing out.
    /// </summary>
    [Fact]
    public async Task ARoundThatOutlastsItsOwnBudget_StillTimesOut()
    {
        static async IAsyncEnumerable<ChatResponseUpdate> NeverAnswers(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            yield break;
        }

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call => NeverAnswers(call.Arg<CancellationToken>()));

        var sut = Build(chatClient);

        await Assert.ThrowsAsync<LlmTimeoutException>(async () =>
        {
            await foreach (var _ in sut.GetChatCompletionWithToolsAsync(
                [new ChatMessage(ChatRole.User, "go")],
                TestProvider(),
                cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        });
    }
}
