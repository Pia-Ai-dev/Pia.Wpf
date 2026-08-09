using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Helpers;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Flow;
using Pia.Services.Interfaces;
using Pia.Services.Providers;
using Pia.Shared.Models;
using Pia.Tests.Services;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.ViewModels;

// The isolation facts drive the process-global RunWorkspaceRedirects registry, hence the shared collection with
// RunWorkspaceRedirectsTests, whose cap fact deliberately overflows it.
[Collection("RunWorkspaceRedirectsStatic")]
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
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => factory((CancellationToken)ci[7]));

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
    private static async IAsyncEnumerable<ChatStreamItem> CancelThenBlock(
        ChatSession session, [EnumeratorCancellation] CancellationToken ct)
    {
        session.Cancel(); // user hits Stop while the step exchange is streaming
        await Task.Delay(Timeout.Infinite, ct); // the R13-linked run token must cancel this in-flight step
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    /// <summary>Sets a UI SynchronizationContext, constructs the live executor bound to the session, restores.</summary>
    private LiveTurnExecutor BuildLiveExecutor(
        ChatSession session, Func<ChatSession, bool> isActive, RunAutonomyPolicy? policy = null,
        bool supportsTools = false, string? workspaceRoot = null,
        StepPersonaResolver? stepPersonas = null, Persona? runPersona = null,
        SynchronizationContext? ui = null)
    {
        var prev = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ui ?? new SynchronizationContext());
        try
        {
            return new LiveTurnExecutor(
                session, isActive,
                new PersonaAttribution(runPersona?.Id ?? Guid.NewGuid(), runPersona?.Name ?? "Pia", "🤖"),
                Provider(),
                new AssistantTurnSetup("system", null, supportsTools, false),
                tokenizationEnabled: false,
                policy,
                timeline: null,
                workspaceRoot: workspaceRoot,
                stepPersonas: stepPersonas,
                runPersona: runPersona);
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

    // Both production lines that carry the policy to the gate are optional and defaulted, so dropping either
    // compiles and reverts the run to carding every write while its envelope still records the preset classes.
    [Fact]
    public async Task PlannedRun_CarriesTheRunPolicyIntoTheGate_SoACoveredWriteAutoRuns()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1"), false));

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

        // No allowlist entry and NO standing grant: the run policy is the only authority in play.
        _permissions.IsAutoApproveEligible("write_file").Returns(false);
        _permissions.IsGranted(pluginId, "write_file").Returns(false);
        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, (PluginToolCall?)pending));
        _cards.Build(Arg.Any<PluginToolCall>(), Arg.Any<bool>(), Arg.Any<ToolGateDecision?>(), Arg.Any<ToolClass?>()).Returns(card);
        _cards.ResolveStatusText(Arg.Any<string>()).Returns("running");
        _cards.ResolveSuccessTitle(Arg.Any<string>()).Returns("Done");

        var session = CreateSession();
        SeedSessionForPlannedRun(session, "goal");
        var states = new List<ChatState>();
        session.StateChanged += (_, e) => states.Add(e.NewState);

        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => StreamWithToolCall(ci.ArgAt<ToolCallHandler?>(3), "write_file"));

        var live = BuildLiveExecutor(
            session, _ => false,
            RunAutonomyPolicy.FromSettings(new AppSettings { AgentRunAutoApproveBuiltInWrites = true }),
            supportsTools: true);
        var orchestrator = new AgentRunOrchestrator(h.Runs, planner, new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance);

        // BOUNDED on purpose. If the policy does not reach the gate, the gate PROMPTS and the run blocks on a
        // card nobody clicks — a regression would hang the whole suite instead of failing. Drain it by declining
        // so the assertion below reports the real cause.
        var runTask = orchestrator.RunAsync(run, live, Persona(), Provider(), RunProfile.Interactive, session.Cts!.Token);
        var ct = TestContext.Current.CancellationToken;
        var settled = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(20), ct)) == runTask;
        if (!settled)
        {
            card.DeclineCommand.Execute(null);
            await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(10), ct));
        }

        Assert.True(settled, "the run blocked on an action card: the policy never reached the interactive gate");
        await runTask;
        Assert.True(executed, "the policy-covered write must have run without a card click");
        Assert.DoesNotContain(ChatState.WaitingForTool, states);   // never prompted
        // Auto-approval is not silence: the pre-resolved card is still in the transcript (audit trace).
        Assert.Contains(card, session.Messages.Last(m => !m.IsUser).ActionCards);
        // Per-run authority, not a stored one.
        await _permissions.DidNotReceive().GrantAsync(Arg.Any<Guid>(), Arg.Any<string>());
    }

    private static async IAsyncEnumerable<ChatStreamItem> StreamWithToolCall(
        ToolCallHandler? handler, string toolName)
    {
        if (handler is not null)
            await handler(new FunctionCallContent("call-1", toolName, new Dictionary<string, object?>()), new ToolDispatchContext(1));

        yield return new TextDelta("reply");
        yield return new Finished(null, "m");
        await Task.Yield();
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

    /// <summary>A declined plan's clarification question must land in the live session so the session's own next full-replace persist doesn't erase it.</summary>
    [Fact]
    public async Task PlannedRun_DeclinesTheGoal_MirrorsTheQuestionIntoTheLiveSession_AndSurvivesTheNextPersist()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("ggg");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(PlanResult.Decline("what do you mean by ggg?"));

        var session = CreateSession();
        var (_, placeholder) = SeedSessionForPlannedRun(session, "ggg");

        var live = BuildLiveExecutor(session, _ => false);
        var orchestrator = new AgentRunOrchestrator(
            h.Runs, planner, new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance, chats: h.Chats);

        await orchestrator.RunAsync(run, live, Persona(), Provider(), RunProfile.Interactive, session.Cts!.Token);

        var parked = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.WaitingForInput, parked!.State);
        Assert.Equal(AgentRunOrchestrator.NeedsGoalReason, RunPauseEnvelope.ReadReason(parked));

        Assert.DoesNotContain(placeholder, session.Messages);
        var posted = Assert.Single(session.Messages, m => !m.IsUser);
        Assert.Equal("what do you mean by ggg?", posted.Content);
        Assert.Equal(ChatState.Idle, session.State); // OnPausedAsync released the session (a park is not terminal)

        // The mirrored copy must share the durably-written row's id, else the next pull would render the question twice.
        var beforeReplay = await h.Chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.Equal(posted.Id, Assert.Single(beforeReplay!.Messages, m => m.Role == "assistant").Id);

        // Replays the session's own next full-replace persist to confirm the mirrored question survives it.
        await h.Chats.SaveAsync(new SyncAssistantChat
        {
            Id = run.ChatId,
            SchemaVersion = 1,
            Title = "t",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow,
            WindowMode = WindowMode.Assistant.ToString(),
            Messages = [.. session.Messages.Select(AssistantMessageMapper.ToDto)],
        }, TestContext.Current.CancellationToken);

        var stored = await h.Chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.Contains(stored!.Messages, m => m.Role == "assistant" && m.Content == "what do you mean by ggg?");
        Assert.Contains(stored.Messages, m => m.Role == "user" && m.Content == "ggg");
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

    [Theory]
    [InlineData("projects/q3")]
    [InlineData(null)]
    public async Task BeginRunAsync_HandsTheChatWorkingSubpathToTheRunContext(string? workingDirectory)
    {
        // The chat's working subpath is what narrows this run's file sandbox (ChatSession passes it as
        // TaskContext.WorkingSubpath per step), but that ambient is restored in the step's finally and never
        // reaches the orchestrator thread — so the verifier's artifact probe can only find the root the steps
        // wrote into if BeginRunAsync copies it onto the context.
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1"), false));

        var session = CreateSession();
        session.SetWorkingDirectory(workingDirectory);
        SeedSessionForPlannedRun(session, "goal");
        ReturnsStream(_ => Stream(new TextDelta("done"), new Finished(null, "m")));

        var live = BuildLiveExecutor(session, _ => false);
        var ctx = new RunContext("goal", RunProfile.Interactive);

        await live.BeginRunAsync(run, ctx, TestContext.Current.CancellationToken);

        Assert.Equal(workingDirectory, ctx.WorkingSubpath);
        // Un-isolated (no workspaceRoot argument), which is what keeps the assignment above unchanged.
        Assert.Null(ctx.WorkspaceRoot);
    }

    // ---------------------------------------------------------------------------------------------------
    // Batch 06 G5 / plan D4 + D8: interactive isolation end to end, through the REAL provisioner.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A real <see cref="RunWorkspaceService"/> (copy mode — git reported absent) over a real
    /// <see cref="FilesToolHandler"/>, provisioning from <c>&lt;filesFolder&gt;\sub</c> the way an interactive
    /// chat with a working directory does. Rooted at the REAL <see cref="AssistantWorkspace.RunsRoot"/> rather
    /// than a temp runs base on purpose: <c>RunWorkspaceRedirects.Record</c>'s containment gate refuses
    /// anything else, so a temp-rooted fixture would prove the promotion and silently skip the chip half
    /// (plan R1's failure mode). Every directory it creates is Guid-named and removed in
    /// <see cref="Dispose"/>.
    /// </summary>
    private sealed class WorkspaceFixture : IDisposable
    {
        private readonly string _dir;
        private readonly List<Guid> _runIds = [];

        public string FilesFolder { get; }

        /// <summary>The chat's working directory, i.e. the root the workspace is provisioned FROM and the
        /// destination a promotion writes back to (B9).</summary>
        public string WorkingSub { get; }

        public RunWorkspaceService Workspaces { get; }

        public FilesToolHandler Files { get; }

        public WorkspaceFixture()
        {
            _dir = Path.Combine(Path.GetTempPath(), "PiaLiveWs_" + Guid.NewGuid().ToString("N"));
            FilesFolder = Path.Combine(_dir, "files");
            WorkingSub = Path.Combine(FilesFolder, "sub");
            Directory.CreateDirectory(WorkingSub);

            var settings = Substitute.For<ISettingsService>();
            settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = FilesFolder });

            // Git absent ⇒ copy mode, which is the only mode that promotes FILES and therefore the only one
            // that records a chip redirect (worktree mode's deliverable is a branch, plan D5b).
            Workspaces = new RunWorkspaceService(
                new FakeGitProcessRunner { IsGitInstalled = false }, settings,
                NullLogger<RunWorkspaceService>.Instance);
            Files = new FilesToolHandler(settings, new FileStalenessStore(), NullLogger<FilesToolHandler>.Instance);
        }

        public async Task<RunWorkspace> ProvisionAsync(Guid runId, CancellationToken ct)
        {
            _runIds.Add(runId);
            var workspace = await Workspaces.ProvisionAsync(runId, "sub", ct);
            Assert.NotNull(workspace);
            Assert.Equal(RunWorkspaceMode.Copy, workspace!.Mode);
            return workspace;
        }

        /// <summary>
        /// Moves the recorded provisioning instant back, so the promote set cannot depend on the clock. B7
        /// decides the set by <c>mtime &gt; provisionedAtUtc</c>, and a file the run writes milliseconds after
        /// provisioning can tie that timestamp — which would make the fact below flake rather than fail.
        /// WHAT the rule promotes is <c>RunWorkspacePromotionTests</c>' subject, not this one's.
        /// </summary>
        public void BackdateProvisioning(Guid runId)
        {
            var path = Path.Combine(AssistantWorkspace.RunsRoot, runId + ".workspace.json");
            var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            doc["provisionedAtUtc"] = DateTime.UtcNow.AddMinutes(-5);
            File.WriteAllText(path, doc.ToJsonString());
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
            foreach (var runId in _runIds)
            {
                try { Directory.Delete(Path.Combine(AssistantWorkspace.RunsRoot, runId.ToString()), recursive: true); }
                catch { /* best effort */ }
                try { File.Delete(Path.Combine(AssistantWorkspace.RunsRoot, runId + ".workspace.json")); }
                catch { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// One step that writes <c>a.md</c> through the REAL file tools, from inside the step's own logical async
    /// flow — the only place the per-step ambient is set. Records the ambient it saw, because the
    /// one-narrowing rule (B6) is otherwise invisible in the file's location:
    /// <c>ResolveEffectiveRoot</c> falls back to the base root when a subpath does not resolve, so a wrongly
    /// forwarded working subpath would still land the file in the workspace root.
    /// </summary>
    private static async IAsyncEnumerable<ChatStreamItem> WriteThenReply(
        IFilesToolHandler files, List<TaskContext?> seen, [EnumeratorCancellation] CancellationToken ct)
    {
        seen.Add(TaskAmbient.Current);

        var call = new FunctionCallContent("w1", "write_file",
            new Dictionary<string, object?> { ["path"] = "a.md", ["content"] = "step output" });
        var (_, pending) = await files.HandleToolCallAsync(call, ct);
        if (pending is not null)
            await pending.Execute();

        yield return new TextDelta("wrote it");
        yield return new Finished(null, "m");
    }

    /// <summary>Bounds an end-to-end run the way this file's policy-gate fact does: a regression that PROMPTS
    /// would otherwise hang the suite instead of failing it.</summary>
    private static async Task DrainAsync(Task runTask, string because)
    {
        var settled = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)) == runTask;
        Assert.True(settled, because);
        await runTask;
    }

    /// <summary>
    /// REGRESSION for <c>LiveTurnExecutor.BuildSpec</c>'s <c>WorkspaceRoot:</c> argument and for
    /// <c>BeginRunAsync</c>'s <c>ctx.WorkspaceRoot</c> assignment — the two lines that turn a provisioned
    /// directory into an actually-isolated interactive run. Drop either and the step's <c>write_file</c> lands
    /// in the user's assistant files folder instead. No workspace service is handed to the orchestrator here,
    /// so nothing promotes and the assertion is about where the bytes WENT.
    /// </summary>
    [Fact]
    public async Task PlannedRun_WritesIntoItsWorkspace_NotTheAssistantFolder()
    {
        using var ws = new WorkspaceFixture();
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var workspace = await ws.ProvisionAsync(run.Id, TestContext.Current.CancellationToken);

        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1"), false));

        var session = CreateSession();
        session.SetWorkingDirectory("sub");
        SeedSessionForPlannedRun(session, "goal");

        var seen = new List<TaskContext?>();
        ReturnsStream(ct => WriteThenReply(ws.Files, seen, ct));

        var live = BuildLiveExecutor(session, _ => false, workspaceRoot: workspace.Root);
        var ctx = new RunContext("goal", RunProfile.Interactive);
        await live.BeginRunAsync(run, ctx, TestContext.Current.CancellationToken);

        var orchestrator = new AgentRunOrchestrator(h.Runs, planner, new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance);
        await DrainAsync(
            orchestrator.RunAsync(run, live, Persona(), Provider(), RunProfile.Interactive, session.Cts!.Token),
            "the run never settled: a step blocked instead of writing into its workspace");

        // The run context the verifier reads carries the root, and the chat's subpath is NOT re-applied on top
        // of it (B6: the workspace root already IS <filesFolder>\sub).
        Assert.Equal(workspace.Root, ctx.WorkspaceRoot);
        Assert.Null(ctx.WorkingSubpath);

        var ambient = Assert.Single(seen);
        Assert.NotNull(ambient);
        Assert.Equal(workspace.Root, ambient!.Value.WorkspaceRoot);
        Assert.Null(ambient.Value.WorkingSubpath);

        Assert.True(File.Exists(Path.Combine(workspace.Root, "a.md")));
        Assert.False(File.Exists(Path.Combine(ws.WorkingSub, "a.md")));
        Assert.False(File.Exists(Path.Combine(ws.FilesFolder, "a.md")));
    }

    /// <summary>
    /// REGRESSION, and the EXECUTOR-PARITY fact (§11): promotion lives in the executor-agnostic orchestrator
    /// and keys off <c>ctx.WorkspaceRoot</c>, so a clean interactive run promotes exactly like a headless one
    /// (T-G4-8's live twin). It also closes plan D8's second phase at the real shape: the chip the step built
    /// points into a workspace that no longer exists by the time a user could click it, and
    /// <see cref="RunWorkspaceRedirects.Resolve"/> is what still opens the right file.
    /// </summary>
    [Fact]
    public async Task PlannedRun_PromotesOnCleanCompletion_AndItsChipResolvesToThePromotedFile()
    {
        using var ws = new WorkspaceFixture();
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var workspace = await ws.ProvisionAsync(run.Id, TestContext.Current.CancellationToken);
        ws.BackdateProvisioning(run.Id);

        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1"), false));

        var session = CreateSession();
        session.SetWorkingDirectory("sub");
        SeedSessionForPlannedRun(session, "goal");

        var seen = new List<TaskContext?>();
        ReturnsStream(ct => WriteThenReply(ws.Files, seen, ct));

        var live = BuildLiveExecutor(session, _ => false, workspaceRoot: workspace.Root);
        var orchestrator = new AgentRunOrchestrator(
            h.Runs, planner, new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance, ws.Workspaces);

        await DrainAsync(
            orchestrator.RunAsync(run, live, Persona(), Provider(), RunProfile.Interactive, session.Cts!.Token),
            "the run never settled: a step blocked instead of writing into its workspace");

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);

        // Canonical spelling on both sides: the destination the metadata recorded is real-path resolved, and
        // Path.GetTempPath() can hand back an 8.3 component.
        var promoted = Path.Combine(SafeFolderPath.Canonicalize(ws.WorkingSub), "a.md");
        Assert.True(File.Exists(promoted), "a clean interactive run's work was not promoted out of its workspace");
        Assert.False(Directory.Exists(workspace.Root), "the workspace was not torn down after promotion");

        var chip = Assert.Single(session.Messages.Last(m => !m.IsUser).FileRefs);
        Assert.Equal(Path.Combine(workspace.Root, "a.md"), chip.AbsolutePath, ignoreCase: true);
        Assert.Equal(promoted, RunWorkspaceRedirects.Resolve(chip.AbsolutePath), ignoreCase: true);
    }

    // ---------------------------------------------------------------------------------------------------
    // Batch 07 G6 — per-step persona on the INTERACTIVE executor (executor parity).
    //
    // Batch 04's post-review correction records this exact parity gap being missed on the Live side once
    // already, which is why the headless facts are not enough: LiveTurnExecutor carries the persona through
    // StepTurnSpec, and every member it sets is trailing and defaulted, so dropping one COMPILES.
    // ---------------------------------------------------------------------------------------------------

    private readonly IPersonaService _personaService = Substitute.For<IPersonaService>();
    private readonly IProviderService _providerService = Substitute.For<IProviderService>();
    private readonly IAssistantPromptComposer _composer = Substitute.For<IAssistantPromptComposer>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly AppSettings _appSettings = new();

    private static Persona RosterPersona(string name) =>
        new() { Id = Guid.NewGuid(), Name = name, SystemPrompt = "you are " + name };

    /// <summary>
    /// A real resolver over this fixture's substitutes, with <paramref name="roster"/> configured. Roster
    /// membership is checked on the executor side too, so stubbing only the persona store would leave every
    /// assignment ignored and the tests below green for the wrong reason.
    /// </summary>
    private StepPersonaResolver ResolverWith(params Persona[] roster)
    {
        _appSettings.SetAgentPersonaRoster(UserOperatingMode.Personal, roster.Select(p => p.Id).ToList());
        _settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(_appSettings));
        _personaService.GetPersonasAsync().Returns(roster.ToList());
        foreach (var p in roster)
            _personaService.GetPersonaAsync(p.Id).Returns(p);
        _composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(),
                Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(ci => new AssistantTurnSetup("system for " + ci.ArgAt<Persona>(0).Name, null, false, false));
        _providerService.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(Provider());
        return new StepPersonaResolver(
            _personaService, _providerService, _composer, _settingsService,
            NullLogger<StepPersonaResolver>.Instance);
    }

    /// <summary>Records the system message of every exchange, plus the context it ran under.</summary>
    private List<(string System, SynchronizationContext? Context)> CaptureSystemPrompts()
    {
        var captured = new List<(string, SynchronizationContext?)>();
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var messages = ci.ArgAt<IList<ChatMessage>>(0);
                captured.Add((messages[0].Text ?? string.Empty, SynchronizationContext.Current));
                return Stream(new TextDelta("reply"), new Finished(null, "m"));
            });
        return captured;
    }

    [Fact]
    public async Task PlannedRun_CarriesThePerStepPersonaIntoTheStepSpec()
    {
        // REGRESSION for BuildSpec's Persona:/SystemPrompt: change. A real orchestrator, a real
        // LiveTurnExecutor and a real ChatSession: the assigned step's assistant message is attributed to the
        // assigned persona and its exchange carries that persona's system prompt, while the unassigned step
        // stays on the run persona's — within ONE run.
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var analyst = RosterPersona("Analyst");
        var resolver = ResolverWith(analyst);

        var steps = MakeSteps("s1", "s2");
        steps[0].AssignedPersonaId = analyst.Id;   // steps[1] stays unassigned ⇒ the run persona
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(steps, false));

        var session = CreateSession();
        SeedSessionForPlannedRun(session, "goal");
        var captured = CaptureSystemPrompts();

        var runPersona = Persona();
        var live = BuildLiveExecutor(session, _ => false, stepPersonas: resolver, runPersona: runPersona);
        var orchestrator = new AgentRunOrchestrator(
            h.Runs, planner, new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance);

        await DrainAsync(
            orchestrator.RunAsync(run, live, runPersona, Provider(), RunProfile.Interactive, session.Cts!.Token),
            "the run never settled");

        // Attribution: what the transcript and the panel read off the step's own message.
        var replies = session.Messages.Where(m => !m.IsUser).ToList();
        Assert.Equal(2, replies.Count);
        Assert.Equal(analyst.Id, replies[0].Persona!.Id);
        Assert.Equal("Analyst", replies[0].Persona!.Name);
        Assert.Equal(runPersona.Id, replies[1].Persona!.Id);

        // Substance: the system prompt the model actually received per step. Without this half the feature is
        // a relabelled glyph (§0.1).
        Assert.Equal(2, captured.Count);
        Assert.Equal("system for Analyst", captured[0].System);
        Assert.Equal("system", captured[1].System);   // the run's turn setup, untouched
    }

    /// <summary>
    /// A UI context that installs ITSELF as <c>Current</c> for the duration of a posted callback, the way a
    /// real dispatcher does. That is what makes "did this run inside the Post?" observable: everything the
    /// executor does before <c>PostAsync</c> sees a different (null) context.
    /// </summary>
    private sealed class MarkingSyncContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) =>
            ThreadPool.QueueUserWorkItem(_ =>
            {
                SetSynchronizationContext(this);
                d(state);
            });
    }

    [Fact]
    public async Task PerStepResolutionHappensOutsideTheUiPost()
    {
        // GUARD. ResolveAsync awaits a settings read and two store reads; doing that inside PostAsync would put
        // them on the dispatcher for every step of every interactive run. ExecuteStepAsync is called from the
        // orchestrator's loop, already off the UI thread, so resolving before the Post costs the UI nothing.
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var analyst = RosterPersona("Analyst");
        var resolver = ResolverWith(analyst);

        SynchronizationContext? contextAtResolve = null;
        var resolveSeen = 0;
        // Observed at the ROSTER read, not at a per-id GetPersonaAsync: since the Phase 3 fix pass the resolver
        // takes the persona straight out of the roster list it already fetched (the per-id round-trip was the one
        // arm of the ladder that could throw, and it failed the whole run). GetRosterAsync is still called from
        // inside ResolveAsync, so this is the same observation point — one step, so it is reached once.
        _personaService.GetPersonasAsync().Returns(_ =>
        {
            contextAtResolve = SynchronizationContext.Current;
            resolveSeen++;
            return Task.FromResult<IReadOnlyList<Persona>>([analyst]);
        });

        var steps = MakeSteps("s1");
        steps[0].AssignedPersonaId = analyst.Id;
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(steps, false));

        var session = CreateSession();
        SeedSessionForPlannedRun(session, "goal");
        var captured = CaptureSystemPrompts();

        var ui = new MarkingSyncContext();
        var runPersona = Persona();
        var live = BuildLiveExecutor(session, _ => false, stepPersonas: resolver, runPersona: runPersona, ui: ui);
        var orchestrator = new AgentRunOrchestrator(
            h.Runs, planner, new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance);

        await DrainAsync(
            orchestrator.RunAsync(run, live, runPersona, Provider(), RunProfile.Interactive, session.Cts!.Token),
            "the run never settled");

        // Positive control FIRST: the step turn itself really did run inside the Post, so this context is
        // observable and the assertion below is not vacuous.
        Assert.Single(captured);
        Assert.Same(ui, captured[0].Context);

        Assert.Equal(1, resolveSeen);
        Assert.NotSame(ui, contextAtResolve);
    }

    // Real AgentPlanner, orchestrator, SQLite, ChatSession, and LiveTurnExecutor here — only the provider is stubbed.

    private const string ThinGoal = "ggg";

    private const string ModelQuestion = "what do u mean with ggg?";

    /// <summary>Counts provider turns, so tests can assert exactly one plan turn ran and no step turn followed.</summary>
    private int _providerTurns;

    /// <summary>Stubs the provider to answer every turn with an emit_plan call that declines the goal.</summary>
    private void ProviderDeclinesEveryPlanTurn(string? question = ModelQuestion)
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Interlocked.Increment(ref _providerTurns);
                return DeclineStream(ci.ArgAt<ToolCallHandler?>(3), question);
            });
    }

    private static async IAsyncEnumerable<ChatStreamItem> DeclineStream(ToolCallHandler? handler, string? question)
    {
        if (handler is not null)
        {
            await handler(
                new FunctionCallContent(Guid.NewGuid().ToString(), "emit_plan", new Dictionary<string, object?>
                {
                    ["cannotGround"] = true,
                    ["question"] = question,
                    ["steps"] = null,
                }),
                new ToolDispatchContext(1));
        }

        await Task.Yield();
        yield return new Finished(null, "test-model");
    }

    /// <summary>Builds the panel under an inline <see cref="SynchronizationContext"/> so its posted projection is observable synchronously, then restores the previous context.</summary>
    private RunProgressViewModel BuildPanel(Harness h, Guid runId)
    {
        var prev = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        try
        {
            return new RunProgressViewModel(
                h.Runs, runId, _loc, Substitute.For<IAgentRunResumeService>(), NullLogger.Instance);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prev);
        }
    }

    /// <summary>Runs Post callbacks inline so a marshaled projection is observable synchronously.</summary>
    /// <summary>The REAL planner over this fixture's stubbed provider client.</summary>
    private AgentPlanner RealPlanner()
    {
        _settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(_appSettings));
        var handler = Substitute.For<IAiProviderHandler>();
        handler.ProviderType.Returns(AiProviderType.OpenAI); // must match Provider()'s, or the resolver throws
        handler.DropsReasoningEffortWithTools.Returns(false);
        return new AgentPlanner(
            _ai, new AiProviderHandlerResolver([handler]), _settingsService, NullLogger<AgentPlanner>.Instance);
    }

    /// <summary>Runs one interactive Planned run whose plan turn declines, shared by the tests below so they observe the same real park.</summary>
    private async Task<(AgentRun Parked, ChatSession Session, AssistantMessage Placeholder)> RunDeclinedInteractiveAsync(
        Harness h, string goal = ThinGoal)
    {
        var run = await h.NewRunAsync(goal);
        var session = CreateSession();
        var (_, placeholder) = SeedSessionForPlannedRun(session, goal);
        ProviderDeclinesEveryPlanTurn();

        var live = BuildLiveExecutor(session, _ => false);
        var orchestrator = new AgentRunOrchestrator(
            h.Runs, RealPlanner(), new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance, chats: h.Chats);

        await orchestrator.RunAsync(run, live, Persona(), Provider(), RunProfile.Interactive, session.Cts!.Token);

        var parked = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(parked);
        return (parked!, session, placeholder);
    }

    /// <summary>An interactive Planned run whose plan turn declines parks with no steps and leaves the session usable.</summary>
    [Fact]
    public async Task InteractivePlannedRun_PlanTurnDeclines_ParksNeedsGoal_NoSteps_AndLeavesTheSessionUsable()
    {
        using var h = new Harness();

        var (parked, session, placeholder) = await RunDeclinedInteractiveAsync(h);

        Assert.Equal(1, _providerTurns);

        Assert.Equal(AgentRunState.WaitingForInput, parked.State);
        Assert.Equal(AgentRunOrchestrator.NeedsGoalReason, RunPauseEnvelope.ReadReason(parked));
        Assert.Empty(parked.Plan);
        Assert.Null(parked.CompletedAt);    // a park is not terminal

        Assert.DoesNotContain(placeholder, session.Messages);   // BeginRunAsync removed the streaming placeholder
        var posted = Assert.Single(session.Messages, m => !m.IsUser);
        Assert.Equal(ModelQuestion, posted.Content);            // the mirror step put the question on screen
        Assert.Equal(ChatState.Idle, session.State);            // OnPausedAsync released the session…
        Assert.Null(session.Cts);                               // …and disposed the run CTS, so Send re-enables
    }

    /// <summary>A needs-goal park suppresses the Flow card for the watched chat, but the run-progress panel still names the reason and offers Continue.</summary>
    [Fact]
    public async Task InteractiveNeedsGoalPark_PublishesNoFlowCardForTheWatchedChat_ButThePanelNamesItAndOffersContinue()
    {
        using var h = new Harness();
        var (parked, _, _) = await RunDeclinedInteractiveAsync(h);

        // Assistant window in foreground and this run's chat is the active session.
        var flow = Substitute.For<IFlowService>();
        var windows = Substitute.For<IWindowManagerService>();
        windows.IsInForeground(WindowMode.Assistant).Returns(true);
        windows.ActiveAssistantChatId.Returns(parked.ChatId);
        var surface = new AgentRunNotificationSurface(
            h.Runs, flow, windows, h.Chats, _loc, NullLogger<AgentRunNotificationSurface>.Instance);

        await surface.HandleRunStateAsync(parked.Id, AgentRunState.WaitingForInput);

        flow.DidNotReceiveWithAnyArgs().Publish(default!);

        // _loc echoes the key it is indexed with, so this asserts the token key, not the question text.
        var panel = BuildPanel(h, parked.Id);
        await panel.RefreshAsync();

        Assert.Equal(RunProgressState.WaitingForInput, panel.State);
        Assert.Equal("Run_Activity_NeedsGoal", panel.CurrentActivity);
        Assert.DoesNotContain(ModelQuestion, panel.CurrentActivity);  // no user content on a surface label
        Assert.True(panel.CanContinue);
        panel.Dispose();
    }
}
