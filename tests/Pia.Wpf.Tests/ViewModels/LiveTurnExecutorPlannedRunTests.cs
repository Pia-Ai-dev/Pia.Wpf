using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.Tests.Services;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// The interactive Planned wiring (§13.11/§13.12 cancellation + §16 R11/R13): a real
/// <see cref="ChatSession"/> driven through a real <see cref="LiveTurnExecutor"/> + real
/// <see cref="AgentRunOrchestrator"/> against a real SQLite <see cref="AgentRunService"/>. Proves the
/// pieces the pure-fake orchestrator test cannot: the session's pre-added streaming placeholder is
/// removed at run start, <c>ChatSession.Cancel()</c> mid-step really stops the in-flight step via the
/// linked CTS (R13), and <c>EndRunAsync</c> settles the terminal <see cref="ChatState"/> + raises
/// <see cref="ChatSession.TurnCompleted"/> — including NOT settling a Failed run as Completed (§13.5.2).
/// </summary>
public sealed class LiveTurnExecutorPlannedRunTests
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly IPluginService _plugins = Substitute.For<IPluginService>();
    private readonly IActionCardBuilder _cards = Substitute.For<IActionCardBuilder>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly ITokenMapService _tokenMap = Substitute.For<ITokenMapService>();
    private readonly IToolPermissionService _permissions = Substitute.For<IToolPermissionService>();

    public LiveTurnExecutorPlannedRunTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
    }

    private static Persona Persona() => new() { Name = "Pia", SystemPrompt = "sys" };
    private static AiProvider Provider() => new() { Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };

    private ChatSession CreateSession() => new(
        _tokenMap, _ai, _plugins, _cards, _permissions, _loc, NullLogger.Instance, _ => false);

    private static List<AgentStep> MakeSteps(params string[] intents)
    {
        var result = new List<AgentStep>();
        for (var i = 0; i < intents.Length; i++)
            result.Add(new AgentStep { Id = Guid.Empty, Ordinal = i, Title = intents[i], Intent = intents[i], Status = AgentStepStatus.Pending });
        return result;
    }

    private sealed class FakePlanner : IAgentPlanner
    {
        public Queue<PlanResult> Plans { get; } = new();
        public Queue<PlanResult> Replans { get; } = new();

        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(Plans.Count > 0 ? Plans.Dequeue() : PlanResult.Fallback);

        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(Replans.Count > 0 ? Replans.Dequeue() : PlanResult.Fallback);
    }

    /// <summary>Real SQLite run store + chat store, mirroring AgentRunOrchestratorTests' harness.</summary>
    private sealed class Harness : IDisposable
    {
        public readonly SqliteContext Ctx;
        public readonly AgentRunService Runs;
        public readonly AssistantChatService Chats;
        private readonly string _dir;

        public Harness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Ctx = new SqliteContext(Path.Combine(_dir, "history.db"));
            Runs = new AgentRunService(Ctx, NullLogger<AgentRunService>.Instance);
            Chats = new AssistantChatService(Ctx, Runs);
        }

        public async Task<AgentRun> NewRunAsync(string goal)
        {
            var chatId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            await Chats.SaveAsync(new SyncAssistantChat
            {
                Id = chatId,
                SchemaVersion = 1,
                Title = "t",
                CreatedAt = now,
                UpdatedAt = now,
                LastAccessedAt = now,
                WindowMode = WindowMode.Assistant.ToString(),
                Messages = [],
            });
            return await Runs.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: goal));
        }

        public void Dispose()
        {
            Runs.Dispose();
            Ctx.Dispose();
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }
    }

    private void ReturnsStream(Func<CancellationToken, IAsyncEnumerable<ChatStreamItem>> factory) =>
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => factory((CancellationToken)ci[5]));

    private static async IAsyncEnumerable<ChatStreamItem> Stream(params ChatStreamItem[] items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<ChatStreamItem> ThrowingStream(Exception ex)
    {
        await Task.Yield();
        throw ex;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    /// <summary>Fires ChatSession.Cancel() mid-step, then honors the linked run token by blocking on it.</summary>
    private static async IAsyncEnumerable<ChatStreamItem> CancelThenBlock(ChatSession session, CancellationToken ct)
    {
        session.Cancel(); // user hits Stop while the step exchange is streaming
        await Task.Delay(Timeout.Infinite, ct); // the R13-linked run token must cancel this in-flight step
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    /// <summary>Sets a plain UI SynchronizationContext, constructs the live executor bound to the session, restores.</summary>
    private LiveTurnExecutor BuildLiveExecutor(ChatSession session, Func<ChatSession, bool> isActive)
    {
        var prev = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        try
        {
            return new LiveTurnExecutor(
                session, isActive,
                new PersonaAttribution(Guid.NewGuid(), "Pia", "🤖"),
                Provider(),
                new AssistantTurnSetup("system", null, false, false),
                tokenizationEnabled: false);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prev);
        }
    }

    private static (AssistantMessage user, AssistantMessage placeholder) SeedSessionForPlannedRun(ChatSession session, string goal)
    {
        // Mirror ChatSessionManager: user goal + an empty streaming assistant placeholder, then Running + a live CTS.
        var user = new AssistantMessage(ChatRole.User, goal);
        session.Messages.Add(user);
        var placeholder = new AssistantMessage(ChatRole.Assistant) { IsStreaming = true };
        session.Messages.Add(placeholder);
        session.BeginTurn();
        session.SetState(ChatState.Running);
        return (user, placeholder);
    }

    [Fact]
    public async Task PlannedRun_SessionCancelDuringStep_LinkedCts_StopsInFlightStep_SettlesCancelled()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1", "s2"), false));

        var session = CreateSession();
        var (_, placeholder) = SeedSessionForPlannedRun(session, "goal");
        ReturnsStream(ct => CancelThenBlock(session, ct));

        TurnCompletedEventArgs? completed = null;
        var completedCount = 0;
        session.TurnCompleted += (_, e) => { completed = e; Interlocked.Increment(ref completedCount); };

        var live = BuildLiveExecutor(session, _ => false);
        var orchestrator = new AgentRunOrchestrator(h.Runs, planner, new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance);

        await orchestrator.RunAsync(run, live, Persona(), Provider(), RunProfile.Interactive, session.Cts!.Token);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Cancelled, final!.State);          // R13: cancel propagated through the linked CTS
        Assert.DoesNotContain(placeholder, session.Messages);        // BeginRunAsync removed the streaming placeholder
        Assert.Equal(2, session.Messages.Count);                     // [user goal] + only step 1's assistant message (s2 never ran)
        Assert.Equal(ChatState.Idle, session.State);                 // EndRunAsync settled the terminal state (not Error/Running)
        Assert.Null(session.Cts);                                     // EndRunAsync disposed the run CTS
        Assert.Equal(1, completedCount);                             // TurnCompleted raised exactly once
        Assert.NotNull(completed);
        Assert.False(completed!.Succeeded);                          // a cancelled run is not a success
    }

    [Fact]
    public async Task PlannedRun_AllStepsSucceed_RemovesPlaceholder_SettlesCompleted_TurnCompletedSucceeded()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1", "s2"), false));

        var session = CreateSession();
        var (_, placeholder) = SeedSessionForPlannedRun(session, "goal");
        ReturnsStream(_ => Stream(new TextDelta("reply"), new Finished(null, "m")));

        TurnCompletedEventArgs? completed = null;
        session.TurnCompleted += (_, e) => completed = e;

        var live = BuildLiveExecutor(session, _ => false); // background chat → producedContent settles Completed
        var orchestrator = new AgentRunOrchestrator(h.Runs, planner, new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance);

        await orchestrator.RunAsync(run, live, Persona(), Provider(), RunProfile.Interactive, session.Cts!.Token);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.DoesNotContain(placeholder, session.Messages);        // placeholder removed at run start
        Assert.Equal(3, session.Messages.Count);                     // [user goal] + one assistant message per step
        Assert.Equal(ChatState.Completed, session.State);            // EndRunAsync mirrored the unread-success terminal state
        Assert.NotNull(completed);
        Assert.True(completed!.Succeeded);
    }

    [Fact]
    public async Task PlannedRun_ParkedAtBudget_PersistsPerStep_WithoutTurnCompletedOrTerminalSettle()
    {
        // E2: the interactive transcript used to reach the store ONLY via TurnCompleted → the manager's
        // PersistAsync, and the budget pause deliberately raises neither (OnPausedAsync is non-terminal) —
        // so a parked chat kept at most the goal row. The executor now asks for a persist after every
        // completed step; the request must carry the transcript so far and must NOT settle the run.
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1", "s2", "s3", "s4"), false));

        var session = CreateSession();
        SeedSessionForPlannedRun(session, "goal");
        ReturnsStream(_ => Stream(new TextDelta("reply"), new Finished(null, "m")));

        var persistSnapshots = new List<int>();
        session.PersistRequested += (_, _) => persistSnapshots.Add(session.Messages.Count);
        var completedCount = 0;
        session.TurnCompleted += (_, _) => Interlocked.Increment(ref completedCount);
        var states = new List<ChatState>();
        session.StateChanged += (_, e) => states.Add(e.NewState);

        var live = BuildLiveExecutor(session, _ => false);
        var orchestrator = new AgentRunOrchestrator(h.Runs, planner, new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance);
        var budget = new RunProfile(MaxSteps: 2, MaxReplans: 0, WallClock: TimeSpan.FromMinutes(20));

        await orchestrator.RunAsync(run, live, Persona(), Provider(), budget, session.Cts!.Token);

        var parked = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.WaitingForInput, parked!.State);

        // One persist request per completed step, each seeing the transcript grown by that step's reply.
        Assert.Equal(new[] { 2, 3 }, persistSnapshots.ToArray());

        // Non-terminal: no TurnCompleted, no Completed/Error — the run is parked, not finished (guardrail 5).
        Assert.Equal(0, completedCount);
        Assert.DoesNotContain(ChatState.Completed, states);
        Assert.DoesNotContain(ChatState.Error, states);
        Assert.Equal(ChatState.Idle, session.State); // OnPausedAsync released the session
        Assert.Null(session.Cts);
    }

    [Fact]
    public async Task PlannedRun_InterimPersistHandlerThrows_DoesNotFailTheStepOrTheRun()
    {
        // Guardrail 1: interim persistence is bookkeeping. A throwing subscriber is swallowed + logged and
        // the run drains to a clean Completed.
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1", "s2"), false));

        var session = CreateSession();
        SeedSessionForPlannedRun(session, "goal");
        ReturnsStream(_ => Stream(new TextDelta("reply"), new Finished(null, "m")));

        var raised = 0;
        session.PersistRequested += (_, _) => { Interlocked.Increment(ref raised); throw new InvalidOperationException("persist boom"); };

        var live = BuildLiveExecutor(session, _ => false);
        var orchestrator = new AgentRunOrchestrator(h.Runs, planner, new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance);

        await orchestrator.RunAsync(run, live, Persona(), Provider(), RunProfile.Interactive, session.Cts!.Token);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.All(final.Plan, s => Assert.Equal(AgentStepStatus.Done, s.Status));
        Assert.Equal(2, raised);
        Assert.Equal(3, session.Messages.Count); // [user goal] + one assistant message per step
    }

    [Fact]
    public async Task SingleTurnFallback_RaisesNoInterimPersist_TheTerminalTurnCompletedCoversIt()
    {
        // Parity note with HeadlessTurnExecutor: the R10 degrade path runs EndRunAsync immediately, and
        // that raises TurnCompleted → the manager persists. An interim write there would only duplicate it.
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner(); // empty queue → PlanResult.Fallback → single-turn fallback

        var session = CreateSession();
        SeedSessionForPlannedRun(session, "goal");
        ReturnsStream(_ => Stream(new TextDelta("reply"), new Finished(null, "m")));

        var persists = 0;
        session.PersistRequested += (_, _) => Interlocked.Increment(ref persists);
        var completedCount = 0;
        session.TurnCompleted += (_, _) => Interlocked.Increment(ref completedCount);

        var live = BuildLiveExecutor(session, _ => false);
        var orchestrator = new AgentRunOrchestrator(h.Runs, planner, new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance);

        await orchestrator.RunAsync(run, live, Persona(), Provider(), RunProfile.Interactive, session.Cts!.Token);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.Equal(0, persists);
        Assert.Equal(1, completedCount);
    }

    [Fact]
    public async Task PlannedRun_StepFails_DoesNotSettleCompleted_TurnCompletedNotSucceeded()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1"), false));
        // Replans queue empty → replan degrades to Fallback → the run Fails.

        var session = CreateSession();
        SeedSessionForPlannedRun(session, "goal");
        // The step exchange throws → RunStepTurnAsync writes "Error: boom" into its assistant message
        // (a NON-empty Content) and returns Succeeded=false.
        ReturnsStream(_ => ThrowingStream(new InvalidOperationException("boom")));

        TurnCompletedEventArgs? completed = null;
        session.TurnCompleted += (_, e) => completed = e;

        var live = BuildLiveExecutor(session, _ => false);
        var orchestrator = new AgentRunOrchestrator(h.Runs, planner, new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance);

        await orchestrator.RunAsync(run, live, Persona(), Provider(), RunProfile.Interactive, session.Cts!.Token);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Failed, final!.State);
        // §13.5.2 / §16 R4: even though the last assistant message carries the catch handler's error text,
        // a Failed run must NOT settle Completed / raise TurnCompleted(Succeeded=true).
        Assert.NotEqual(ChatState.Completed, session.State);
        Assert.Equal(ChatState.Idle, session.State);
        Assert.NotNull(completed);
        Assert.False(completed!.Succeeded);
    }
}
