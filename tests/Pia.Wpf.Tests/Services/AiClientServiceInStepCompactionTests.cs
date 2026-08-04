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
/// Covers the in-step tool-loop compaction insertion in <c>AiClientService.GetChatCompletionWithToolsAsync</c>
/// (the <c>round &gt; 0 &amp;&amp; contextBudget is { } budget</c> guard).
/// <para>
/// WHY THIS FILE EXISTS: that insertion had no coverage at any level. The executor-level suites
/// (<c>HeadlessTurnExecutorTests</c>, <c>ChatSessionStepTurnTests</c>) substitute <c>IAiClientService</c>
/// outright, so they can only assert that a budget was RELAYED — never what the client does with it. A wrong
/// guard, a wrong round index, or a swapped variable there is invisible to those tests and to the compactor's
/// own unit tests. This file drives the REAL <see cref="AiClientService"/> against a scripted
/// <see cref="IChatClient"/> that can run a multi-round tool loop, and inspects the message list actually
/// handed to the provider on each round. Same seam as <c>AiClientServiceProtectedRouteTests</c>.
/// </para>
/// </summary>
public class AiClientServiceInStepCompactionTests
{
    /// <summary>Roughly one token per 4 chars, matching the library's bytes/4 estimator.</summary>
    private static string Bulk(int approximateTokens) => new('x', approximateTokens * 4);

    /// <summary>
    /// The same shape <c>AgentContextCompactorTests.AgentStepShapedMessages</c> uses, at the size measured to
    /// actually truncate on a 8000/2000 budget (12 prior replies: in=15, out=11). A smaller fixture would let
    /// every assertion below pass vacuously, because the compactor returns the caller's list untouched when
    /// nothing was evicted.
    /// </summary>
    private static List<ChatMessage> OverBudgetRequest(int priorSteps = 12)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are Pia, an agent."),
            new(ChatRole.User, "THE GOAL: ship the context compaction batch."),
        };
        for (var i = 1; i <= priorSteps; i++)
            messages.Add(new ChatMessage(ChatRole.Assistant, $"step {i} reply: {Bulk(500)}"));
        messages.Add(new ChatMessage(ChatRole.User, $"Execute step {priorSteps + 1}"));
        return messages;
    }

    [Fact]
    public async Task Round0_IsNotCompacted_BecauseTheExecutorAlreadyCompactedIt()
    {
        // The guard is `round > 0` on purpose: round 0's list is exactly what HeadlessTurnExecutor /
        // ChatSession already passed through AgentContextCompactor. Compacting it again here would charge the
        // pinned prefix twice. Pin that the first request goes out byte-for-byte as handed in.
        var request = OverBudgetRequest();
        var sent = await RunToolLoopAsync(request, new AgentContextBudget(8_000, 2_000), toolRounds: 1);

        Assert.Equal(request.Count, sent[0].Count);
        for (var i = 0; i < request.Count; i++)
            Assert.Same(request[i], sent[0][i]);
    }

    [Fact]
    public async Task LaterRounds_AreCompacted_WhenABudgetIsSupplied()
    {
        // Round 1 sees round 0's list plus the assistant tool-call message and the tool-result message the
        // loop appended — the growth the executors' own compaction cannot see, because it happens after they
        // hand the request over. That is the whole reason this insertion exists.
        var request = OverBudgetRequest();
        var sent = await RunToolLoopAsync(request, new AgentContextBudget(8_000, 2_000), toolRounds: 1);

        Assert.Equal(2, sent.Count);
        Assert.True(
            sent[1].Count < sent[0].Count + 2,
            $"round 1 must be compacted: round 0 sent {sent[0].Count}, the loop appended 2, " +
            $"and round 1 sent {sent[1].Count} (an uncompacted round 1 would send {sent[0].Count + 2})");

        // The pins still hold on the compacted in-loop request.
        Assert.Equal(ChatRole.System, sent[1][0].Role);
        Assert.Contains("THE GOAL", sent[1][1].Text);
        Assert.Contains(sent[1], m => m.Text.Contains("Execute step 13", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LaterRounds_AreNotCompacted_WhenNoBudgetIsConfigured()
    {
        // Compaction is opt-in and OFF by default: an unconfigured provider yields no budget, and interactive
        // chat (ChatSession.RunTurnAsync) deliberately passes none. Without this assertion a regression that
        // dropped the `contextBudget is { }` half of the guard would silently start compacting every ordinary
        // chat turn that runs a tool.
        var request = OverBudgetRequest();
        var sent = await RunToolLoopAsync(request, contextBudget: null, toolRounds: 1);

        Assert.Equal(2, sent.Count);
        Assert.Equal(sent[0].Count + 2, sent[1].Count); // grew by exactly the appended pair, nothing evicted
    }

    [Fact]
    public async Task CompactedToolLoop_NeverSendsAToolCallWithoutItsResult()
    {
        // The on-the-wire failure no other test in the tree can see. workingMessages is the ONLY list in Pia
        // that holds FunctionCallContent / FunctionResultContent, so it is the only place tool-result eviction
        // can do any work — and providers reject a request that carries an assistant tool_call whose matching
        // tool result was dropped (OpenAI answers 400). Several rounds with large results so the 0.45
        // tool-eviction trigger genuinely fires.
        var request = OverBudgetRequest();
        var sent = await RunToolLoopAsync(
            request, new AgentContextBudget(8_000, 2_000), toolRounds: 4, toolResultTokens: 400);

        Assert.Equal(5, sent.Count);

        // The pairing check below is only meaningful if compaction ACTUALLY evicted something — otherwise it
        // passes trivially on an untouched list and proves nothing. Round N would carry
        // request.Count + 2N messages uncompacted (one assistant tool-call + one tool result per prior round),
        // so pin that the last round came in under that.
        Assert.True(
            sent[4].Count < request.Count + 8,
            $"eviction never fired, so the pairing assertion would be vacuous: round 4 sent {sent[4].Count} " +
            $"and an uncompacted round 4 would send {request.Count + 8}");

        foreach (var (roundMessages, round) in sent.Select((m, i) => (m, i)))
        {
            var callIds = roundMessages
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                .Select(c => c.CallId)
                .ToList();
            var resultIds = roundMessages
                .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
                .Select(r => r.CallId)
                .ToHashSet();

            foreach (var callId in callIds)
            {
                Assert.True(
                    resultIds.Contains(callId),
                    $"round {round} sent tool call '{callId}' with no matching FunctionResultContent — a " +
                    "provider rejects this request with HTTP 400. Compaction evicted the result but kept the call.");
            }
        }
    }

    /// <summary>
    /// The dispatch context's round is 1-BASED, observed on the REAL loop.
    /// <para>
    /// This fact lives in this file — whose other facts are about compaction — because
    /// <see cref="RunToolLoopAsync"/> is the only harness in the tree that drives
    /// <c>AiClientService</c>'s tool loop for more than one round. Every other suite hand-feeds a
    /// <c>new ToolDispatchContext(1)</c> from its own driver, so it can only assert its own literal back, and
    /// the sole production construction (<c>new ToolDispatchContext(round + 1)</c>) was therefore unpinned:
    /// both plausible regressions kept the whole suite green.
    /// </para>
    /// <para>
    /// The EXACT sequence is asserted, because that is what discriminates: passing the 0-based <c>round</c>
    /// gives <c>[0, 1]</c> and a constant gives <c>[1, 1]</c>. No budget is supplied — compaction has nothing
    /// to do with the round number, and the small request keeps this fact independent of the fixture size the
    /// compaction facts need.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheDispatchContextCarriesTheOneBasedRound()
    {
        var rounds = new List<int>();

        await RunToolLoopAsync(
            [new ChatMessage(ChatRole.User, "go")],
            contextBudget: null,
            toolRounds: 2,
            observedRounds: rounds);

        Assert.Equal(new[] { 1, 2 }, rounds.ToArray());
    }

    /// <summary>
    /// Drives the real service through <paramref name="toolRounds"/> tool-calling rounds followed by a final
    /// text answer, and returns the message list handed to the provider on each round (snapshotted at call
    /// time, because the service mutates and reassigns one list).
    /// </summary>
    /// <param name="observedRounds">Collects <c>ToolDispatchContext.Round</c> as the loop dispatches, in
    /// dispatch order. The tool handler is the only place that value is observable at all.</param>
    private static async Task<List<List<ChatMessage>>> RunToolLoopAsync(
        List<ChatMessage> request,
        AgentContextBudget? contextBudget,
        int toolRounds,
        int toolResultTokens = 10,
        List<int>? observedRounds = null)
    {
        var sent = new List<List<ChatMessage>>();
        var round = 0;

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                sent.Add([.. ci.ArgAt<IEnumerable<ChatMessage>>(0)]);
                return round++ < toolRounds ? ToolCallRound($"call-{round}") : FinalAnswer();
            });

        var handler = Substitute.For<IAiProviderHandler>();
        handler.ProviderType.Returns(AiProviderType.OpenAI);
        handler.CreateChatClientAsync(
                Arg.Any<AiProvider>(), Arg.Any<string?>(), Arg.Any<HttpClient>(),
                Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(chatClient));
        handler.CreateChatOptions(Arg.Any<AiProvider>(), Arg.Any<bool>()).Returns(_ => new ChatOptions());

        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());

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
            SupportsStreaming = true,
            SupportsToolCalling = true,
        };

        await foreach (var _ in sut.GetChatCompletionWithToolsAsync(
            request,
            provider,
            tools: null,
            toolHandler: (_, dispatch) =>
            {
                observedRounds?.Add(dispatch.Round);
                return Task.FromResult<object?>(Bulk(toolResultTokens));
            },
            mode: null,
            cancellationToken: TestContext.Current.CancellationToken,
            contextBudget: contextBudget))
        {
            // Drain: the assertions are on what reached the provider, not on what came back.
        }

        return sent;
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
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("final answer")] };
        await Task.Yield();
    }
}
