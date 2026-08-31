using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Navigation;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.MeetingAttendee;
using Pia.Shared.Models;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>Drives real sessions through the live executor with the cancel sink <c>ChatSessionManager</c>
/// registers: a linked CTS cannot release a step parked on the action-card gate's tokenless TCS.</summary>
public sealed class AgentRunOrchestratorUserPauseLiveTests
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly IPluginService _plugins = Substitute.For<IPluginService>();
    private readonly IActionCardBuilder _cards = Substitute.For<IActionCardBuilder>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly ITokenMapService _tokenMap = Substitute.For<ITokenMapService>();
    private readonly IToolPermissionService _permissions = Substitute.For<IToolPermissionService>();

    public AgentRunOrchestratorUserPauseLiveTests()
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
        {
            result.Add(new AgentStep
            {
                Id = Guid.Empty,
                Ordinal = i,
                Title = intents[i],
                Intent = intents[i],
                Status = AgentStepStatus.Pending,
            });
        }

        return result;
    }

    private sealed class FakePlanner : IAgentPlanner
    {
        public Queue<PlanResult> Plans { get; } = new();

        public int ReplanCalls { get; private set; }

        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(Plans.Count > 0 ? Plans.Dequeue() : PlanResult.Fallback);

        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
        {
            ReplanCalls++;
            return Task.FromResult(PlanResult.Fallback);
        }
    }

    /// <summary>Real SQLite run + chat store, plus the real steering pair.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly string _dir;

        public Harness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "PiaLivePause_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Ctx = new SqliteContext(Path.Combine(_dir, "history.db"));
            Runs = new AgentRunService(Ctx, NullLogger<AgentRunService>.Instance);
            Chats = new AssistantChatService(Ctx, Runs);
            Store = new RunSteeringStore();
            Steering = new AgentRunSteeringService(Runs, Store, NullLogger<AgentRunSteeringService>.Instance);
        }

        public SqliteContext Ctx { get; }

        public AgentRunService Runs { get; }

        public AssistantChatService Chats { get; }

        public RunSteeringStore Store { get; }

        public AgentRunSteeringService Steering { get; }

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

        public AgentRunOrchestrator BuildOrchestrator(IAgentPlanner planner, IRunSteeringStore? steering = null) =>
            new(Runs, planner, new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance,
                workspaces: null, childLauncher: null, chats: null, steering: steering);

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
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => factory((CancellationToken)ci[7]));

    private void ReturnsToolCallStream(string toolName) =>
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => StreamWithToolCall(ci.ArgAt<ToolCallHandler?>(3), toolName));

    private static async IAsyncEnumerable<ChatStreamItem> Stream(params ChatStreamItem[] items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.Yield();
        }
    }

    /// <summary>Requests the pause through the real steering service, then honours the token the way a provider
    /// stream does; the return value is asserted by every caller, so a refused pause cannot pass.</summary>
    private static async IAsyncEnumerable<ChatStreamItem> PauseThenBlock(
        AgentRunSteeringService steering, Guid runId, Action<bool> accepted,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // CancellationToken.None deliberately: PauseAsync's token guards only its run READ, and the very next
        // thing it does is fire the sink that cancels `ct`.
        accepted(await steering.PauseAsync(runId, CancellationToken.None));
        await Task.Delay(Timeout.Infinite, ct); // the sink's cancel reaches the in-flight step
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    /// <summary>Invokes the tool handler (which blocks on the action-card gate), then finishes the exchange.</summary>
    private static async IAsyncEnumerable<ChatStreamItem> StreamWithToolCall(
        ToolCallHandler? handler, string toolName)
    {
        if (handler is not null)
            await handler(new FunctionCallContent("call-1", toolName, new Dictionary<string, object?>()), new ToolDispatchContext(1));

        yield return new TextDelta("reply");
        yield return new Finished(null, "m");
        await Task.Yield();
    }

    /// <summary>Sets a UI context, constructs the live executor bound to the session, restores.</summary>
    private static LiveTurnExecutor BuildLiveExecutor(
        ChatSession session, bool supportsTools = false, StepPersonaResolver? stepPersonas = null)
    {
        var prev = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        try
        {
            // Both are needed together: a resolver with no run default never resolves anything.
            return new LiveTurnExecutor(
                session, _ => false,
                new PersonaAttribution(Guid.NewGuid(), "Pia", "🤖"),
                Provider(),
                new AssistantTurnSetup("system", null, supportsTools, false),
                tokenizationEnabled: false,
                stepPersonas: stepPersonas,
                runPersona: stepPersonas is null ? null : Persona());
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prev);
        }
    }

    /// <summary>Mirrors <c>ChatSessionManager</c>'s Planned branch: user goal, Running, a live CTS.</summary>
    private static void SeedSessionForPlannedRun(ChatSession session, string goal)
    {
        session.Messages.Add(new AssistantMessage(ChatRole.User, goal));
        session.BeginTurn();
        session.SetState(ChatState.Running);
    }

    /// <summary>The sink <c>ChatSessionManager</c> registers for an interactive Planned run, and in the card
    /// facts the only route to cancellation.</summary>
    private static Action RegisterSessionSink(RunSteeringStore store, Guid runId, ChatSession session)
    {
        Action sink = () => { try { session.Cancel(); } catch { /* already torn down */ } };
        store.RegisterDispatch(runId, sink);
        return sink;
    }

    // -------------------------------------------------------------------------------------------------
    // Live parity for the pause
    // -------------------------------------------------------------------------------------------------

    /// <summary>Live marshals every step body through a posted callback and its cancel arm RETURNS
    /// <c>Cancelled: true</c> rather than throwing, unlike the headless twin of this fact.</summary>
    [Fact]
    public async Task UserPause_MidStep_LeavesTheRunResumable_OnLive()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1", "s2"), false));

        var session = CreateSession();
        SeedSessionForPlannedRun(session, "goal");
        RegisterSessionSink(h.Store, run.Id, session);

        var accepted = false;
        ReturnsStream(_ => PauseThenBlock(h.Steering, run.Id, v => accepted = v, session.Cts!.Token));

        var live = BuildLiveExecutor(session);
        await h.BuildOrchestrator(planner, steering: h.Store)
            .RunAsync(run, live, Persona(), Provider(), RunProfile.Interactive, session.Cts!.Token);

        Assert.True(accepted); // the pause was accepted, not refused: this fact is not vacuous
        var paused = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Paused, paused!.State);                                   // not Cancelled
        Assert.Null(paused.CompletedAt);                                                     // not settled
        Assert.Equal(AgentRunService.UserPausedReason, RunPauseEnvelope.ReadReason(paused));  // a USER pause
        var aborted = Assert.Single(paused.Plan, s => s.Title == "s1");
        Assert.Equal(AgentStepStatus.Pending, aborted.Status);                               // back in the plan
        var next = await h.Runs.NextPendingStepAsync(run.Id, ct);
        Assert.Equal("s1", next!.Title);          // …and visible to the drain a resume uses
        Assert.Equal(0, planner.ReplanCalls);     // a pause is not a step failure
        Assert.Null(session.Cts);                 // OnPausedAsync released the session (non-terminal)

        // RESUME on the LIVE executor too: claim from Paused, re-arm the session the way a new turn does, and
        // let both steps run. A fact that only checks the state has not checked the thing.
        Assert.True(await h.Runs.TryResumeFromPauseAsync(run.Id, ct));
        var resumed = (await h.Runs.GetAsync(run.Id, ct))!;
        ReturnsStream(_ => Stream(new TextDelta("reply"), new Finished(null, "m")));
        session.BeginTurn();
        session.SetState(ChatState.Running);
        await h.BuildOrchestrator(new FakePlanner(), steering: h.Store)
            .RunAsync(resumed, BuildLiveExecutor(session), Persona(), Provider(), RunProfile.Interactive,
                session.Cts!.Token, resume: true);

        var final = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.NotNull(final.CompletedAt);
        Assert.All(final.Plan, s => Assert.Equal(AgentStepStatus.Done, s.Status)); // the aborted step re-ran
        Assert.Null(RunPauseEnvelope.ReadReason(final));                            // the claim retired the marker
    }

    /// <summary>The step-persona resolve is awaited outside <c>PostAsync</c> and any exchange try/catch, and the
    /// roster ladder rethrows a cancelled OCE — so the abort throws out with the step row already Running(1).</summary>
    [Fact]
    public async Task UserPause_ThatThrowsOutOfTheStepPersonaResolve_LeavesTheRunResumable_OnLive()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");

        var assigned = new Persona { Id = Guid.NewGuid(), Name = "Specialist", SystemPrompt = "spec" };
        var plan = MakeSteps("s1", "s2");
        plan[0].AssignedPersonaId = assigned.Id;   // only an ASSIGNED step reaches the roster read
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(plan, false));

        var session = CreateSession();
        SeedSessionForPlannedRun(session, "goal");
        RegisterSessionSink(h.Store, run.Id, session);

        // The roster read is the throw site: the pause is requested from inside it, so by the time the OCE is
        // raised session.Cancel() has run and the resolver's `when (ct.IsCancellationRequested)` filter matches.
        var accepted = false;
        var personas = Substitute.For<IPersonaService>();
        var settings = Substitute.For<ISettingsService>();
        var appSettings = new AppSettings();
        appSettings.SetAgentPersonaRoster(UserOperatingMode.Personal, [assigned.Id]);
        settings.GetSettingsAsync().Returns(_ => Task.FromResult(appSettings));
        personas.GetPersonasAsync().Returns<IReadOnlyList<Persona>>(_ =>
        {
            accepted = h.Steering.PauseAsync(run.Id, CancellationToken.None).GetAwaiter().GetResult();
            throw new OperationCanceledException();
        });

        var resolver = new StepPersonaResolver(
            personas, Substitute.For<IProviderService>(), Substitute.For<IAssistantPromptComposer>(),
            settings, NullLogger<StepPersonaResolver>.Instance);

        var live = BuildLiveExecutor(session, stepPersonas: resolver);
        await h.BuildOrchestrator(planner, steering: h.Store)
            .RunAsync(run, live, Persona(), Provider(), RunProfile.Interactive, session.Cts!.Token);

        Assert.True(accepted); // non-vacuity: the pause was accepted, not refused

        // No exchange means the abort left ExecuteStepAsync by throwing: a degraded OCE would have run the step
        // and come back cancelled through the in-loop consume, a different path with the same final row.
        _ai.DidNotReceive().GetChatCompletionWithToolsAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
            Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>());
        Assert.DoesNotContain(session.Messages, m => !m.IsUser); // no step reply was ever streamed

        var paused = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Paused, paused!.State);                                   // never Cancelled
        Assert.Null(paused.CompletedAt);
        Assert.Equal(AgentRunService.UserPausedReason, RunPauseEnvelope.ReadReason(paused));
        // The step was left Running(1) by the throw — the catch arm restores it from the hoisted id.
        Assert.Equal(AgentStepStatus.Pending, Assert.Single(paused.Plan, s => s.Title == "s1").Status);
        Assert.Equal("s1", (await h.Runs.NextPendingStepAsync(run.Id, ct))!.Title);
        Assert.Equal(0, planner.ReplanCalls);      // a pause is not a step failure
        Assert.Null(session.Cts);                  // OnPausedAsync released the live session (non-terminal)
        Assert.NotEqual(ChatState.Completed, session.State); // …and EndRunAsync did NOT settle it
        Assert.True(await h.Runs.TryResumeFromPauseAsync(run.Id, ct)); // genuinely claimable
    }

    /// <summary>The step parks on a card gate whose <see cref="TaskCompletionSource{TResult}"/> takes no
    /// <see cref="CancellationToken"/>, so only <c>session.Cancel()</c> can release it.</summary>
    [Fact]
    public async Task UserPause_ReleasesAStepBlockedOnAnActionCard()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1", "s2"), false));

        var pluginId = Guid.NewGuid();
        var executed = false;
        var pending = new PluginToolCall(
            "write_file", pluginId, "files", "files: write_file", null,
            () => { executed = true; return Task.FromResult<object?>("done"); });
        var card = new ActionCardInfo
        {
            Title = "write_file",
            Summary = "write_file",
            Category = ActionCardCategory.Files,
            ToolName = "write_file",
            PluginId = pluginId,
        };

        // No allowlist entry, no standing grant and no run policy ⇒ the gate PROMPTS, which is the normal path
        // for an interactive Planned run's writes.
        _permissions.IsAutoApproveEligible("write_file").Returns(false);
        _permissions.IsGranted(pluginId, "write_file").Returns(false);
        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(_ => ((object?)null, (PluginToolCall?)pending));
        _cards.Build(Arg.Any<PluginToolCall>(), Arg.Any<bool>(), Arg.Any<ToolGateDecision?>(), Arg.Any<ToolClass?>()).Returns(card);
        _cards.ResolveStatusText(Arg.Any<string>()).Returns("running");
        _cards.ResolveSuccessTitle(Arg.Any<string>()).Returns("Done");

        var session = CreateSession();
        SeedSessionForPlannedRun(session, "goal");
        RegisterSessionSink(h.Store, run.Id, session);
        ReturnsToolCallStream("write_file");

        // Off the turn's own thread: the release completes the very TCS the gate is awaiting, and doing that
        // inline from a state-changed handler would resume the tool loop reentrantly.
        Task<bool>? pause = null;
        session.StateChanged += (_, e) =>
        {
            if (e.NewState == ChatState.WaitingForTool)
                pause ??= Task.Run(() => h.Steering.PauseAsync(run.Id, CancellationToken.None), ct);
        };

        var live = BuildLiveExecutor(session, supportsTools: true);
        var runTask = h.BuildOrchestrator(planner, steering: h.Store)
            .RunAsync(run, live, Persona(), Provider(), RunProfile.Interactive, session.Cts!.Token);

        // BOUNDED: if the pause cannot release the card, the step blocks forever and an unbounded await would
        // hang the whole suite instead of failing. Drain it by declining so the assertion reports the cause.
        var settled = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(20), ct)) == runTask;
        if (!settled)
        {
            card.DeclineCommand.Execute(null);
            await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(10), ct));
        }

        Assert.True(settled, "the pause did not release the action card: the step stayed parked on the gate's TCS");
        await runTask;

        Assert.NotNull(pause);
        Assert.True(await pause!);                              // the pause was accepted, not refused
        Assert.Equal(ActionCardState.Declined, card.State);     // released, i.e. resolved without a click
        Assert.False(executed, "the carded write must not have run");

        var paused = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Paused, paused!.State);
        Assert.Null(paused.CompletedAt);
        Assert.Equal(AgentRunService.UserPausedReason, RunPauseEnvelope.ReadReason(paused));
        Assert.Equal(AgentStepStatus.Pending, Assert.Single(paused.Plan, s => s.Title == "s1").Status);
        // A declined card is not a step failure, so nothing may replan and step 2 must not have started either.
        Assert.Equal(0, planner.ReplanCalls);
        Assert.Equal(AgentStepStatus.Pending, Assert.Single(paused.Plan, s => s.Title == "s2").Status);
    }

    /// <summary>The pause takes <c>OnPausedAsync</c>, never <c>EndRunAsync</c>: settling the session terminal
    /// instead would tell the user the turn finished.</summary>
    [Fact]
    public async Task UserPause_DoesNotSettleTheLiveSessionCompleted()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1"), false));

        var session = CreateSession();
        SeedSessionForPlannedRun(session, "goal");
        RegisterSessionSink(h.Store, run.Id, session);
        Assert.True(session.IsStreaming); // the direction the assertion below is only reachable from

        var states = new List<ChatState>();
        session.StateChanged += (_, e) => states.Add(e.NewState);
        var completed = 0;
        session.TurnCompleted += (_, _) => Interlocked.Increment(ref completed);

        var accepted = false;
        ReturnsStream(_ => PauseThenBlock(h.Steering, run.Id, v => accepted = v, session.Cts!.Token));

        await h.BuildOrchestrator(planner, steering: h.Store)
            .RunAsync(run, BuildLiveExecutor(session), Persona(), Provider(), RunProfile.Interactive, session.Cts!.Token);

        Assert.True(accepted);
        Assert.Equal(AgentRunState.Paused, (await h.Runs.GetAsync(run.Id, ct))!.State);

        Assert.Equal(0, completed);                              // no TurnCompleted — the run is not finished
        Assert.DoesNotContain(ChatState.Completed, states);      // …and no terminal chat state
        Assert.DoesNotContain(ChatState.Error, states);
        Assert.Equal(ChatState.Idle, states[^1]);
        Assert.False(session.IsStreaming);                       // Send / Run-in-background re-enable
        Assert.Null(session.Cts);                                // OnPausedAsync disposed the run CTS
    }

    // -------------------------------------------------------------------------------------------------
    // Terminal-intent revocations
    // -------------------------------------------------------------------------------------------------

    /// <summary>Stop is terminal intent, so it revokes a pending pause before cancelling — otherwise the
    /// unwinding loop reads the Stop as a pause and the run the user killed comes back alive.</summary>
    [Fact]
    public async Task StopButton_RevokesAPendingPause_AndTheRunSettlesCancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1", "s2"), false));

        var session = CreateSession();
        SeedSessionForPlannedRun(session, "goal");
        session.SetActiveRun(run.Id); // what the manager stamps, and what the VM reads to know WHICH run to revoke
        RegisterSessionSink(h.Store, run.Id, session);

        var manager = Substitute.For<IChatSessionManager>();
        manager.ActiveSession.Returns(session);
        var vm = CreateAssistantViewModel(manager, h.Store);

        var accepted = false;
        ReturnsStream(_ => PauseThenStopThenBlock(h.Steering, run.Id, v => accepted = v, vm, session.Cts!.Token));

        await h.BuildOrchestrator(planner, steering: h.Store)
            .RunAsync(run, BuildLiveExecutor(session), Persona(), Provider(), RunProfile.Interactive, session.Cts!.Token);

        Assert.True(accepted); // the pause really was recorded, so the revoke had something to revoke

        var final = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Cancelled, final!.State);  // Stop wins over an unconsumed pause
        Assert.NotNull(final.CompletedAt);                    // …terminally: FailAsync stamped it
        Assert.Null(RunPauseEnvelope.ReadReason(final));      // no pause envelope was ever written
        Assert.Equal(AgentStepStatus.Failed, Assert.Single(final.Plan, s => s.Title == "s1").Status);
        Assert.False(h.Store.TryConsumePauseRequest(run.Id), "the request must be gone, not merely unread");
    }

    /// <summary>Both cancels land on the same session; the difference is entirely the revoke.</summary>
    private static async IAsyncEnumerable<ChatStreamItem> PauseThenStopThenBlock(
        AgentRunSteeringService steering, Guid runId, Action<bool> accepted, AssistantViewModel vm,
        [EnumeratorCancellation] CancellationToken ct)
    {
        accepted(await steering.PauseAsync(runId, CancellationToken.None));
        vm.CancelStreamingCommand.Execute(null); // the Stop button
        await Task.Delay(Timeout.Infinite, ct);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    /// <summary>Clearing the conversation abandons the chat and cancels its turn, so it must revoke for the same
    /// reason Stop does; asserted on the store because the conversation being abandoned is the point.</summary>
    [Fact]
    public void ClearConversation_RevokesAPendingPause()
    {
        var runId = Guid.NewGuid();
        var store = new RunSteeringStore();

        var session = CreateSession();
        session.BeginTurn();
        session.SetActiveRun(runId);
        var sink = RegisterSessionSink(store, runId, session);
        Assert.True(store.RecordPauseRequest(runId)); // a pause is pending on a dispatch this process is running

        var manager = Substitute.For<IChatSessionManager>();
        manager.ActiveSession.Returns(session);
        manager.GetOrCreateActiveForNewChat().Returns(_ => CreateSession()); // the fresh chat the command opens
        var vm = CreateAssistantViewModel(manager, store);

        vm.ClearConversationCommand.Execute(null);

        Assert.False(store.TryConsumePauseRequest(runId), "clearing the conversation must not leave a pause behind");

        Assert.False(store.RecordPauseRequest(runId),
            "terminal intent is sticky for the dispatch it was aimed at: a pause pressed while the cancel " +
            "unwinds must be refused, not re-armed");

        // A new dispatch of the same run (a resume, a re-launch) clears the terminal mark when it registers its
        // own sink, and is pausable again.
        void NewSink() { }
        store.RegisterDispatch(runId, NewSink);
        Assert.True(store.RecordPauseRequest(runId),
            "a re-dispatched run must be pausable again — the intent belonged to the cancelled dispatch");
        store.ReleaseDispatch(runId, NewSink);
        store.ReleaseDispatch(runId, sink);
    }

    /// <summary>The composer's "+" is additive — the old chat's turn keeps running — so unlike Clear
    /// conversation it must leave that turn's pending pause alone.</summary>
    [Fact]
    public void NewChat_LeavesAPendingPauseAlone()
    {
        var runId = Guid.NewGuid();
        var store = new RunSteeringStore();

        var session = CreateSession();
        session.BeginTurn();
        session.SetActiveRun(runId);
        var sink = RegisterSessionSink(store, runId, session);
        Assert.True(store.RecordPauseRequest(runId));

        var manager = Substitute.For<IChatSessionManager>();
        manager.ActiveSession.Returns(session);
        manager.GetOrCreateActiveForNewChat().Returns(_ => CreateSession()); // the fresh chat the command opens
        var vm = CreateAssistantViewModel(manager, store);

        vm.NewChatCommand.Execute(null);

        Assert.True(store.TryConsumePauseRequest(runId),
            "opening a new chat must not revoke a pause aimed at the turn it left running");

        store.ReleaseDispatch(runId, sink);
    }

    /// <summary>The production <see cref="AssistantViewModel"/> with everything but the two collaborators these
    /// facts steer through substituted.</summary>
    private AssistantViewModel CreateAssistantViewModel(IChatSessionManager manager, IRunSteeringStore steering)
    {
        // ChatTitleChipViewModel (built in the ctor) requires a captured SynchronizationContext.
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());

        var meeting = new MeetingAttendeeViewModel(
            Substitute.For<IMeetingAttendeeService>(),
            settings,
            Substitute.For<ILocalizationService>(),
            Substitute.For<IFileDialogService>(),
            Substitute.For<IDialogService>(),
            Substitute.For<IMemoryService>(),
            Substitute.For<IIngestScheduler>(),
            Substitute.For<Wpf.Ui.ISnackbarService>(),
            NullLogger<MeetingAttendeeViewModel>.Instance,
            new InlineUiDispatcher());

        var directTranscription = new DirectTranscriptionViewModel(
            Substitute.For<IDirectTranscriptionService>(),
            settings,
            Substitute.For<ILocalizationService>(),
            Substitute.For<IFileDialogService>(),
            Substitute.For<IDialogService>(),
            Substitute.For<IMemoryService>(),
            Substitute.For<IIngestScheduler>(),
            Substitute.For<Wpf.Ui.ISnackbarService>(),
            NullLogger<DirectTranscriptionViewModel>.Instance,
            new InlineUiDispatcher());

        return new AssistantViewModel(
            NullLogger<AssistantViewModel>.Instance,
            _ai,
            Substitute.For<IProviderService>(),
            Substitute.For<IPersonaService>(),
            settings,
            Substitute.For<IOutputService>(),
            _plugins,
            Substitute.For<IVoiceInputService>(),
            Substitute.For<ITtsService>(),
            Substitute.For<IAudioRecordingService>(),
            Substitute.For<ITranscriptionService>(),
            NullLoggerFactory.Instance,
            Substitute.For<global::Wpf.Ui.ISnackbarService>(),
            _loc,
            _tokenMap,
            Substitute.For<IAutocompleteService>(),
            Substitute.For<INavigationService>(),
            Substitute.For<ISuggestionService>(),
            Substitute.For<IAssistantChatService>(),
            meeting,
            directTranscription,
            Substitute.For<IAssistantPromptComposer>(),
            Substitute.For<IProviderCapabilityService>(),
            Substitute.For<IAgentRunService>(),
            Substitute.For<IAgentRunResumeService>(),
            manager,
            Substitute.For<IWorkingDirectoryService>(),
            Substitute.For<IFilesToolHandler>(),
            Substitute.For<IMarkdownExportService>(),
            Substitute.For<IDialogService>(),
            new InlineUiDispatcher(),
            _permissions,
            agentTimelineService: null,
            runWorkspaces: null,
            runSteering: steering);
    }
}
