using System.Net;
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
/// One test per arm the tool loop delegates to, driven through the public iterator with a fake chat client.
/// The arms are private, so each is reached the only way production reaches it.
/// </summary>
public class AiClientServiceToolLoopArmTests
{
    [Fact]
    public async Task StreamingRound_RejectedForCarryingTools_RetriesOnceWithoutThem()
    {
        var harness = new Harness { SupportsStreaming = true };
        var attempt = 0;
        harness.ChatClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => attempt++ == 0
                ? ThrowsOnFirstMoveNext(BadRequest())
                : TextRound("answered without tools"));

        var items = await harness.RunAsync(WithTools());

        Assert.Equal(2, attempt);
        Assert.Equal("answered without tools", Assert.Single(items.OfType<TextDelta>()).Text);
        // The retry is what re-asks for options, and it asks for them tool-free.
        Assert.Equal([true, false], harness.HasToolsRequested);
    }

    [Fact]
    public async Task NonStreamingRound_RejectedForCarryingTools_RetriesOnceWithoutThem()
    {
        var harness = new Harness { SupportsStreaming = false };
        var attempt = 0;
        harness.ChatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => attempt++ == 0
                ? throw BadRequest()
                : Task.FromResult(Answer("answered without tools")));

        var items = await harness.RunAsync(WithTools());

        Assert.Equal(2, attempt);
        Assert.Equal("answered without tools", Assert.Single(items.OfType<TextDelta>()).Text);
        Assert.Equal([true, false], harness.HasToolsRequested);
    }

    [Fact]
    public async Task ToolCall_IsDispatched_AndItsResultFeedsTheNextRound()
    {
        var harness = new Harness { SupportsStreaming = true };
        var round = 0;
        harness.ChatClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                harness.Sent.Add([.. ci.ArgAt<IEnumerable<ChatMessage>>(0)]);
                return round++ == 0 ? ToolCallRound() : TextRound("done");
            });

        await harness.RunAsync(WithTools());

        Assert.Equal("read_file", Assert.Single(harness.Dispatched));

        // The dispatch appends the assistant's call AND the tool's result, so round 1 sees both.
        var result = Assert.Single(harness.Sent[1].SelectMany(m => m.Contents.OfType<FunctionResultContent>()));
        Assert.Equal("call-1", result.CallId);
        Assert.Equal("tool output", result.Result);
    }

    [Fact]
    public async Task ToolRoundsExhausted_SpendOneToolFreeWrapUpRound()
    {
        // One round, and it spends that round on a tool call — so the loop runs out with no final answer.
        var harness = new Harness { SupportsStreaming = true, MaxToolRounds = 1 };
        harness.ChatClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => ToolCallRound());
        harness.ChatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Answer("best effort")));

        var items = await harness.RunAsync(WithTools());

        // The wrap-up is the only non-streaming call on this provider, so one call is the whole assertion.
        await harness.ChatClient.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        Assert.Equal("best effort", Assert.Single(items.OfType<TextDelta>()).Text);
        Assert.True(Assert.Single(items.OfType<Finished>()).ToolRoundsExhausted);
    }

    /// <summary>A gate that stops the loop ends the exchange on the round it stopped in — no further provider
    /// round-trip, and no tool-free wrap-up.</summary>
    [Fact]
    public async Task ToolHandler_RequestsStop_FinishesTheExchangeAfterOneRound()
    {
        // A tool call on EVERY round, so an unstopped loop would run to MaxToolRounds and spend a wrap-up.
        var harness = new Harness
        {
            SupportsStreaming = true,
            MaxToolRounds = 3,
            OnDispatch = ctx => ctx.Stop?.RequestStop(),
        };
        harness.ChatClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => ToolCallRound());
        harness.ChatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Answer("best effort")));

        var items = await harness.RunAsync(WithTools());

        harness.ChatClient.Received(1).GetStreamingResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        await harness.ChatClient.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        Assert.Equal("read_file", Assert.Single(harness.Dispatched));

        // The stopped round is still handed over intact: an unpaired call would be re-seeded into the next
        // provider request, which many providers reject outright.
        var exchange = Assert.Single(items.OfType<ToolRoundExchange>());
        Assert.Equal(1, exchange.Round);
        var contents = exchange.Messages.SelectMany(m => m.Contents).ToList();
        Assert.Equal("call-1", Assert.Single(contents.OfType<FunctionCallContent>()).CallId);
        Assert.Equal("call-1", Assert.Single(contents.OfType<FunctionResultContent>()).CallId);

        Assert.False(Assert.Single(items.OfType<Finished>()).ToolRoundsExhausted);
    }

    /// <summary>The routine failure this arm exists for: the round that ends the loop carries no text at
/// all, which a scheduled run would otherwise report as "the model gave no answer".</summary>
    [Fact]
    public async Task EmptyFinalRound_SpendsOneToolFreeReAsk()
    {
        var harness = new Harness { SupportsStreaming = true };
        harness.ChatClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => EmptyRound());
        harness.ChatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Answer("second time lucky")));

        var items = await harness.RunAsync(WithTools());

        await harness.ChatClient.Received(1).GetResponseAsync(
            Arg.Is<IEnumerable<ChatMessage>>(m => m.Last().Text == AiClientService.EmptyAnswerNudge),
            Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        Assert.Equal("second time lucky", Assert.Single(items.OfType<TextDelta>()).Text);
        // Rounds were not exhausted — the loop chose to stop, so the flag must not claim otherwise.
        Assert.False(Assert.Single(items.OfType<Finished>()).ToolRoundsExhausted);
    }

    /// <summary>The observed shape: a reasoning model spends the round thinking and emits no answer.</summary>
    [Fact]
    public async Task ReasoningOnlyFinalRound_SpendsOneToolFreeReAsk()
    {
        var harness = new Harness { SupportsStreaming = true };
        harness.ChatClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => ReasoningRound("weighing the release notes"));
        harness.ChatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Answer("8.0.29 is still the latest")));

        var items = await harness.RunAsync(WithTools());

        Assert.Equal("8.0.29 is still the latest", Assert.Single(items.OfType<TextDelta>()).Text);
        Assert.Equal("weighing the release notes", Assert.Single(items.OfType<ReasoningDelta>()).Text);
    }

    [Fact]
    public async Task EmptyFinalRound_ReAskAlsoEmpty_CompletesWithoutASecondReAsk()
    {
        var harness = new Harness { SupportsStreaming = true };
        harness.ChatClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => EmptyRound());
        harness.ChatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Answer(string.Empty)));

        var items = await harness.RunAsync(WithTools());

        await harness.ChatClient.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        harness.ChatClient.Received(1).GetStreamingResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        Assert.Empty(items.OfType<TextDelta>());
        Assert.Single(items.OfType<Finished>());
    }

    /// <summary>A round that answers is never re-asked — the re-ask must not cost every turn a round.</summary>
    [Fact]
    public async Task AnsweredRound_SpendsNoReAsk()
    {
        var harness = new Harness { SupportsStreaming = true };
        harness.ChatClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => TextRound("answered"));

        await harness.RunAsync(WithTools());

        await harness.ChatClient.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    private static List<AITool> WithTools() => [AIFunctionFactory.Create(() => "ok", "read_file", "reads a file")];

    private static HttpRequestException BadRequest() =>
        new("tool calling is not supported", null, HttpStatusCode.BadRequest);

    private static ChatResponse Answer(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    private static async IAsyncEnumerable<ChatResponseUpdate> ThrowsOnFirstMoveNext(Exception error)
    {
        await Task.Yield();
        // Guarded so the yield below stays reachable — an iterator needs one, and unreachable code warns.
        if (error is not null) throw error;
        yield break;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> TextRound(string text)
    {
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent(text)] };
        await Task.Yield();
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> EmptyRound()
    {
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, FinishReason = ChatFinishReason.Stop };
        await Task.Yield();
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ReasoningRound(string thinking)
    {
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextReasoningContent(thinking)],
            FinishReason = ChatFinishReason.Stop,
        };
        await Task.Yield();
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ToolCallRound()
    {
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?> { ["path"] = "A.cs" })],
        };
        await Task.Yield();
    }

    private sealed class Harness
    {
        public IChatClient ChatClient { get; } = Substitute.For<IChatClient>();

        public bool SupportsStreaming { get; init; }

        public int MaxToolRounds { get; init; } = 24;

        public List<bool> HasToolsRequested { get; } = [];

        public List<string> Dispatched { get; } = [];

        public List<List<ChatMessage>> Sent { get; } = [];

        /// <summary>Runs inside the tool handler, which otherwise discards the dispatch context.</summary>
        public Action<ToolDispatchContext>? OnDispatch { get; init; }

        public async Task<List<ChatStreamItem>> RunAsync(IList<AITool>? tools)
        {
            var handler = Substitute.For<IAiProviderHandler>();
            handler.ProviderType.Returns(AiProviderType.OpenAI);
            handler.CreateChatClientAsync(
                    Arg.Any<AiProvider>(), Arg.Any<string?>(), Arg.Any<HttpClient>(),
                    Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(ChatClient));
            handler.CreateChatOptions(Arg.Any<AiProvider>(), Arg.Any<bool>()).Returns(ci =>
            {
                HasToolsRequested.Add(ci.ArgAt<bool>(1));
                return new ChatOptions();
            });

            var httpFactory = Substitute.For<IHttpClientFactory>();
            httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());

            var settings = Substitute.For<ISettingsService>();
            settings.GetSettingsAsync().Returns(new AppSettings { MaxToolRoundsPerStep = MaxToolRounds });

            var sut = new AiClientService(
                new DpapiHelper(NullLogger<DpapiHelper>.Instance),
                httpFactory,
                settings,
                new AiProviderHandlerResolver([handler]),
                Substitute.For<IAuthService>(),
                NullLogger<AiClientService>.Instance,
                new ProviderRequestThrottle(settings, NullLogger<ProviderRequestThrottle>.Instance));

            var provider = new AiProvider
            {
                Name = "t",
                Endpoint = "http://localhost",
                ProviderType = AiProviderType.OpenAI,
                SupportsStreaming = SupportsStreaming,
                SupportsToolCalling = true,
            };

            var items = new List<ChatStreamItem>();
            await foreach (var item in sut.GetChatCompletionWithToolsAsync(
                [new ChatMessage(ChatRole.User, "go")],
                provider,
                tools,
                toolHandler: (call, ctx) =>
                {
                    Dispatched.Add(call.Name);
                    OnDispatch?.Invoke(ctx);
                    return Task.FromResult<object?>("tool output");
                },
                mode: null,
                cancellationToken: TestContext.Current.CancellationToken))
            {
                items.Add(item);
            }

            return items;
        }
    }
}
