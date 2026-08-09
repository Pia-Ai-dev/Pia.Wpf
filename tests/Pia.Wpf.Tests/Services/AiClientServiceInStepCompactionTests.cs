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

public class AiClientServiceInStepCompactionTests
{
    /// <summary>Roughly one token per 4 chars, matching the library's bytes/4 estimator.</summary>
    private static string Bulk(int approximateTokens) => new('x', approximateTokens * 4);

    /// <summary>Sized to actually truncate on an 8000/2000 budget; a smaller fixture passes every assertion vacuously.</summary>
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
        // Round 0's list has already been through AgentContextCompactor by the executor, so compacting it
        // again here would charge the pinned prefix twice.
        var request = OverBudgetRequest();
        var sent = await RunToolLoopAsync(request, new AgentContextBudget(8_000, 2_000), toolRounds: 1);

        Assert.Equal(request.Count, sent[0].Count);
        for (var i = 0; i < request.Count; i++)
            Assert.Same(request[i], sent[0][i]);
    }

    [Fact]
    public async Task LaterRounds_AreCompacted_WhenABudgetIsSupplied()
    {
        // Round 1 adds the tool-call and tool-result messages: growth the executor's own compaction cannot see.
        var request = OverBudgetRequest();
        var sent = await RunToolLoopAsync(request, new AgentContextBudget(8_000, 2_000), toolRounds: 1);

        Assert.Equal(2, sent.Count);
        Assert.True(
            sent[1].Count < sent[0].Count + 2,
            $"round 1 must be compacted: round 0 sent {sent[0].Count}, the loop appended 2, " +
            $"and round 1 sent {sent[1].Count} (an uncompacted round 1 would send {sent[0].Count + 2})");

        Assert.Equal(ChatRole.System, sent[1][0].Role);
        Assert.Contains("THE GOAL", sent[1][1].Text);
        Assert.Contains(sent[1], m => m.Text.Contains("Execute step 13", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LaterRounds_AreNotCompacted_WhenNoBudgetIsConfigured()
    {
        // Compaction is opt-in: an unconfigured provider yields no budget, and interactive chat passes none.
        var request = OverBudgetRequest();
        var sent = await RunToolLoopAsync(request, contextBudget: null, toolRounds: 1);

        Assert.Equal(2, sent.Count);
        Assert.Equal(sent[0].Count + 2, sent[1].Count);
    }

    [Fact]
    public async Task CompactedToolLoop_NeverSendsAToolCallWithoutItsResult()
    {
        // Several rounds with large results, so the tool-result eviction trigger genuinely fires.
        var request = OverBudgetRequest();
        var sent = await RunToolLoopAsync(
            request, new AgentContextBudget(8_000, 2_000), toolRounds: 4, toolResultTokens: 400);

        Assert.Equal(5, sent.Count);

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

    /// <summary>Drives the real service through a tool loop, snapshotting what was sent on each round.</summary>
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
                Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
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
