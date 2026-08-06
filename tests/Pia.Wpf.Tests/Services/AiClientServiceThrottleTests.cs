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
/// T1-2's WIRING, which <c>ProviderRequestThrottleTests</c> cannot see: where in
/// <see cref="AiClientService"/> the permit is taken and — the fact that actually matters — where it is given
/// back.
/// <para>
/// The load-bearing one is <see cref="NoPermitIsHeldWhileTheToolHandlerRuns"/>. A permit taken once per CALL
/// instead of once per ROUND compiles, passes every throttle unit test, and holds the provider's permit across
/// an interactive approval card — so one human staring at a dialog would stop every background run on that
/// provider. Nothing else in the suite would notice.
/// </para>
/// </summary>
public class AiClientServiceThrottleTests
{
    /// <summary>
    /// Records the bracket. <see cref="Held"/> is the interesting value: it is asserted from INSIDE the tool
    /// handler, i.e. at the one instant the permit must not be held.
    /// </summary>
    private sealed class RecordingThrottle : IProviderRequestThrottle
    {
        private int _held;

        public int Acquires { get; private set; }
        public int Releases { get; private set; }
        public int Held => Volatile.Read(ref _held);
        public List<Guid> Keys { get; } = [];

        public Task<IDisposable> AcquireAsync(AiProvider provider, CancellationToken ct)
        {
            Acquires++;
            Keys.Add(provider.Id);
            Interlocked.Increment(ref _held);
            return Task.FromResult<IDisposable>(new Permit(this));
        }

        private sealed class Permit : IDisposable
        {
            private RecordingThrottle? _owner;
            internal Permit(RecordingThrottle owner) => _owner = owner;

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner is null) return;
                owner.Releases++;
                Interlocked.Decrement(ref owner._held);
            }
        }
    }

    private static AiProvider TestProvider(bool streaming) => new()
    {
        Name = "t",
        Endpoint = "http://localhost",
        ProviderType = AiProviderType.OpenAI,
        SupportsStreaming = streaming,
        SupportsToolCalling = true,
    };

    private static (AiClientService Sut, RecordingThrottle Throttle) Build(IChatClient chatClient)
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

        var throttle = new RecordingThrottle();
        var sut = new AiClientService(
            new DpapiHelper(NullLogger<DpapiHelper>.Instance),
            httpFactory,
            settings,
            new AiProviderHandlerResolver([handler]),
            Substitute.For<IAuthService>(),
            NullLogger<AiClientService>.Instance,
            throttle);
        return (sut, throttle);
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
    /// Two tool rounds plus the final answer = three provider round-trips = three permits, each given back.
    /// A per-call permit would record exactly one.
    /// </summary>
    [Fact]
    public async Task ThePermitIsTakenOncePerRound_AndAlwaysGivenBack()
    {
        var (sut, throttle) = Build(StreamingClient(toolRounds: 2));
        var provider = TestProvider(streaming: true);

        await foreach (var _ in sut.GetChatCompletionWithToolsAsync(
            [new ChatMessage(ChatRole.User, "go")],
            provider,
            tools: null,
            toolHandler: (_, _) => Task.FromResult<object?>("ok"),
            cancellationToken: TestContext.Current.CancellationToken))
        {
            // Drain: the assertions are about the permit bracket, not the content.
        }

        Assert.Equal(3, throttle.Acquires);
        Assert.Equal(3, throttle.Releases);
        Assert.Equal(0, throttle.Held);
        Assert.All(throttle.Keys, key => Assert.Equal(provider.Id, key));
    }

    /// <summary>
    /// THE fact. The tool handler is where an interactive approval card is awaited, so a permit held here is a
    /// permit held for however long a person takes to answer.
    /// </summary>
    [Fact]
    public async Task NoPermitIsHeldWhileTheToolHandlerRuns()
    {
        var (sut, throttle) = Build(StreamingClient(toolRounds: 1));
        var heldDuringDispatch = new List<int>();

        await foreach (var _ in sut.GetChatCompletionWithToolsAsync(
            [new ChatMessage(ChatRole.User, "go")],
            TestProvider(streaming: true),
            tools: null,
            toolHandler: (_, _) =>
            {
                heldDuringDispatch.Add(throttle.Held);
                return Task.FromResult<object?>("ok");
            },
            cancellationToken: TestContext.Current.CancellationToken))
        {
        }

        Assert.NotEmpty(heldDuringDispatch); // non-vacuity: a handler that never ran would assert nothing
        Assert.All(heldDuringDispatch, held => Assert.Equal(0, held));
    }

    /// <summary>
    /// The non-streaming twin of the same bracket: <c>GetResponseAsync</c> materializes the whole answer, so the
    /// permit is released before the text is yielded to the consumer.
    /// </summary>
    [Fact]
    public async Task TheNonStreamingPath_ReleasesBeforeItYields()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "answer")])));

        var (sut, throttle) = Build(chatClient);
        var heldAtYield = new List<int>();

        await foreach (var item in sut.GetChatCompletionWithToolsAsync(
            [new ChatMessage(ChatRole.User, "go")],
            TestProvider(streaming: false),
            cancellationToken: TestContext.Current.CancellationToken))
        {
            if (item is TextDelta) heldAtYield.Add(throttle.Held);
        }

        Assert.Equal(1, throttle.Acquires);
        Assert.Equal(1, throttle.Releases);
        Assert.NotEmpty(heldAtYield);
        Assert.All(heldAtYield, held => Assert.Equal(0, held));
    }

    /// <summary>
    /// The single-shot path <c>AgentPlanner</c>/<c>AgentVerifier</c> use. One request, one permit — including
    /// the tool-disabled retry, which is the same round-trip re-attempted and must not queue twice.
    /// </summary>
    [Fact]
    public async Task GetChatResponseAsync_TakesExactlyOnePermit()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "answer")])));

        var (sut, throttle) = Build(chatClient);

        await sut.GetChatResponseAsync(
            [new ChatMessage(ChatRole.User, "go")],
            TestProvider(streaming: false),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, throttle.Acquires);
        Assert.Equal(1, throttle.Releases);
        Assert.Equal(0, throttle.Held);
    }

    /// <summary>
    /// A provider fault must not strand the permit — the release is in a <c>finally</c>, not on the happy path.
    /// </summary>
    [Fact]
    public async Task AThrowingRoundTrip_StillGivesThePermitBack()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns<Task<ChatResponse>>(_ => throw new HttpRequestException("boom"));

        var (sut, throttle) = Build(chatClient);

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in sut.GetChatCompletionWithToolsAsync(
                [new ChatMessage(ChatRole.User, "go")],
                TestProvider(streaming: false),
                cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        });

        Assert.Equal(1, throttle.Acquires);
        Assert.Equal(1, throttle.Releases);
        Assert.Equal(0, throttle.Held);
    }
}
