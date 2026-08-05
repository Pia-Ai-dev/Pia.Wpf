using System.IO;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Providers;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>Exercises the real planner, orchestrator, and SQLite services with only the AI client stubbed, so a decline is never chosen by a test double.</summary>
public sealed class GoalGroundingReproTests : IDisposable
{
    private const string ThinGoal = "ggg";

    /// <summary>User-derived text; the test never logs it (see the CLAUDE.md privacy rule).</summary>
    private const string ModelQuestion = "what do u mean with ggg?";

    /// <summary>Wire-level pause reason for a plan-time decline; kept as a literal so a rename doesn't go unnoticed.</summary>
    private const string NeedsGoalReason = "needs-goal";

    // Literal wire names, not read off the production schema, so a renamed member breaks this test.
    private const string DeclineMember = "cannotGround";
    private const string QuestionMember = "question";

    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly AppSettings _settings = new();

    private readonly string _dir;
    private readonly SqliteContext _ctx;
    private readonly AgentRunService _runs;
    private readonly AssistantChatService _chats;

    /// <summary>Plan turns the stub served. Non-vacuity: a fact that never reached the planner proves nothing.</summary>
    private int _planTurns;

    public GoalGroundingReproTests()
    {
        _settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(_settings));
        // AssistantFilesFolder stays null so the grounding digest stays absent and the goal reaches the plan turn verbatim.
        _dir = Path.Combine(Path.GetTempPath(), "PiaGoalGrounding_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _ctx = new SqliteContext(Path.Combine(_dir, "history.db"));
        _runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_ctx, _runs);
    }

    public void Dispose()
    {
        _runs.Dispose();
        _ctx.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* temp dir */ }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Persona Persona() => new() { Id = Guid.NewGuid(), Name = "Pia", SystemPrompt = "you are Pia" };

    private static AiProvider Provider() => new()
    {
        Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI, SupportsToolCalling = true,
    };

    // ---- the harness ----------------------------------------------------------------------------------

    /// <summary>Executor double that records what was dispatched and whether the single-turn fallback was taken.</summary>
    private sealed class RecordingExecutor : IAgentTurnExecutor
    {
        public List<string> Executed { get; } = new();
        public bool FallbackCalled { get; private set; }
        public bool EndCalled { get; private set; }
        public bool PausedCalled { get; private set; }

        private static StepTurnResult Ok(string text) =>
            new(true, false, null, text, null, Guid.NewGuid(), Guid.NewGuid());

        public Task BeginRunAsync(AgentRun run, RunContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task<StepTurnResult> ExecuteStepAsync(AgentRun run, AgentStep step, RunContext ctx, CancellationToken ct)
        {
            Executed.Add(step.Intent ?? step.Title);
            return Task.FromResult(Ok("done"));
        }

        public Task<StepTurnResult> RunSingleTurnFallbackAsync(AgentRun run, RunContext ctx, CancellationToken ct)
        {
            FallbackCalled = true;
            return Task.FromResult(Ok("fallback"));
        }

        public Task EndRunAsync(AgentRun run, RunContext ctx, bool cancelled, bool failed, CancellationToken ct)
        {
            EndCalled = true;
            return Task.CompletedTask;
        }

        public Task OnPausedAsync(AgentRun run, RunContext ctx, CancellationToken ct)
        {
            PausedCalled = true; // non-terminal park hook, not EndRunAsync
            return Task.CompletedTask;
        }
    }

    private async Task<AgentRun> NewPlannedRunAsync(string goal)
    {
        var chatId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await _chats.SaveAsync(new SyncAssistantChat
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
        // Planned + a null ParentRunId: the only shape a plan-time park is defined for.
        return await _runs.CreateAsync(
            new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: goal), Ct);
    }

    private AgentPlanner Planner()
    {
        var handler = Substitute.For<IAiProviderHandler>();
        handler.ProviderType.Returns(AiProviderType.OpenAI);
        handler.DropsReasoningEffortWithTools.Returns(false);
        return new AgentPlanner(_ai, new AiProviderHandlerResolver([handler]), _settingsService,
            NullLogger<AgentPlanner>.Instance);
    }

    // Real AssistantChatService (not a stub), so the clarification chat round-trip is exercised for real.
    private AgentRunOrchestrator Orchestrator() =>
        new(_runs, Planner(), new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance, chats: _chats);

    /// <summary>One plan turn: dispatches <c>emit_plan</c> when <paramref name="emitArgs"/> is non-null (null means the model called nothing).</summary>
    private static async IAsyncEnumerable<ChatStreamItem> PlanStream(
        ToolCallHandler? handler, Dictionary<string, object?>? emitArgs, string? visible, UsageDetails? usage)
    {
        if (handler is not null && emitArgs is not null)
            await handler(new FunctionCallContent(Guid.NewGuid().ToString(), "emit_plan", emitArgs),
                new ToolDispatchContext(1));
        if (!string.IsNullOrEmpty(visible))
            yield return new TextDelta(visible);
        await Task.Yield();
        // Usage rides the Finished item — the only place a provider reports it.
        yield return new Finished(usage, "test-model");
    }

    /// <summary>Every plan turn (the first AND the firm retry) answers the same way.</summary>
    private void ProviderAlwaysAnswers(
        Dictionary<string, object?>? emitArgs, string? visible, UsageDetails? usage = null)
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _planTurns++;
                return PlanStream(ci.ArgAt<ToolCallHandler?>(3), emitArgs, visible, usage);
            });
    }

    /// <summary>The decline turn: the model calls <c>emit_plan</c> once, using it to say it cannot ground the goal instead of filling <c>steps</c>.</summary>
    private static Dictionary<string, object?> DeclineArgs() => new()
    {
        [DeclineMember] = true,
        [QuestionMember] = ModelQuestion,
        ["steps"] = null,
    };

    /// <summary>The fabrication turn: a four-step plan for a goal too thin to plan for.</summary>
    private static Dictionary<string, object?> FourStepArgs() => new()
    {
        ["steps"] = new object[]
        {
            Step("Clarify the request", "work out what ggg refers to"),
            Step("Gather context", "collect anything related to ggg"),
            Step("Draft the deliverable", "produce a first pass at ggg"),
            Step("Review and finish", "check the ggg deliverable over"),
        },
    };

    private static Dictionary<string, object?> Step(string title, string intent) => new()
    {
        ["title"] = title, ["intent"] = intent, ["expectedArtifact"] = null,
        ["personaKey"] = null, ["parallelGroup"] = null,
    };

    private async Task<AgentRun> RunToSettlementAsync(RecordingExecutor exec, string goal)
    {
        var run = await NewPlannedRunAsync(goal);
        await Orchestrator().RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, Ct);
        var settled = await _runs.GetAsync(run.Id, Ct);
        // Assert rather than `!` so a harness fault fails informatively instead of an NRE.
        Assert.NotNull(settled);
        return settled!;
    }

    // ---- the forward assertion -------------------------------------------------------------------

    [Fact]
    public async Task UngroundableGoal_ParksAtWaitingForInput_WithNeedsGoal_AndCreatesNoSteps()
    {
        ProviderAlwaysAnswers(DeclineArgs(), ModelQuestion);
        var exec = new RecordingExecutor();

        var settled = await RunToSettlementAsync(exec, ThinGoal);

        // Non-vacuity: confirm the plan turn actually ran before asserting on its effects.
        Assert.True(_planTurns >= 1, $"the plan turn never ran (planTurns={_planTurns})");

        Assert.Equal(AgentRunState.WaitingForInput, settled.State);
        Assert.Equal(NeedsGoalReason, RunPauseEnvelope.ReadReason(settled));

        // Read back from SQLite, not the planner's in-memory result — only persisted rows matter to the drain loop.
        Assert.Empty(settled.Plan);
        Assert.Empty(exec.Executed);
        Assert.Null(await _runs.NextPendingStepAsync(settled.Id, Ct));

        // A decline must not fall back to the single-turn degrade.
        Assert.False(exec.FallbackCalled);

        // A park is neither a completion nor a failure: no CompletedAt, no EndRun, but OnPaused does fire.
        Assert.Null(settled.CompletedAt);
        Assert.False(exec.EndCalled);
        Assert.True(exec.PausedCalled);
    }

    // ---- the question posted into the run's own chat ----------------------------

    [Fact]
    public async Task UngroundableGoal_PostsTheQuestionIntoTheRunsOwnChat()
    {
        ProviderAlwaysAnswers(DeclineArgs(), ModelQuestion);
        var exec = new RecordingExecutor();

        var settled = await RunToSettlementAsync(exec, ThinGoal);

        var chat = await _chats.GetAsync(settled.ChatId, Ct);
        Assert.NotNull(chat);
        var posted = Assert.Single(chat!.Messages);
        Assert.Equal("assistant", posted.Role);
        Assert.Equal(ModelQuestion, posted.Content);
    }

    /// <summary>A decline with no question worded is still a decline: the flag is the discriminator, not the text.</summary>
    [Fact]
    public async Task UngroundableGoal_DeclinedWithNoQuestionWorded_PostsNothingToTheChat()
    {
        var unwordedDecline = new Dictionary<string, object?> { [DeclineMember] = true, ["steps"] = null };
        ProviderAlwaysAnswers(unwordedDecline, visible: null);
        var exec = new RecordingExecutor();

        var settled = await RunToSettlementAsync(exec, ThinGoal);

        Assert.Equal(AgentRunState.WaitingForInput, settled.State); // non-vacuity: it really parked
        Assert.Equal(NeedsGoalReason, RunPauseEnvelope.ReadReason(settled));
        var chat = await _chats.GetAsync(settled.ChatId, Ct);
        Assert.NotNull(chat);
        Assert.Empty(chat!.Messages);
    }

    // ---- the negative half, made concrete --------------------------------------------------------------

    /// <summary>False-positive guard: a model that emits a usable plan still gets one, however thin the goal was.</summary>
    [Fact]
    public async Task TodaysBehaviour_AFabricatedPlanForAThinGoal_IsPersistedAndExecutedToCompletion()
    {
        ProviderAlwaysAnswers(FourStepArgs(), visible: null);
        var exec = new RecordingExecutor();

        var settled = await RunToSettlementAsync(exec, ThinGoal);

        Assert.Equal(1, _planTurns); // one turn: the model called emit_plan, so no firm retry
        Assert.Equal(AgentRunState.Completed, settled.State);

        // The steps are REAL rows, in order, and every one of them ran.
        Assert.Equal(4, settled.Plan.Count);
        Assert.All(settled.Plan, s => Assert.Equal(AgentStepStatus.Done, s.Status));
        Assert.Equal(
            new[] { "Clarify the request", "Gather context", "Draft the deliverable", "Review and finish" },
            settled.Plan.OrderBy(s => s.Ordinal).Select(s => s.Title).ToArray());
        Assert.Equal(4, exec.Executed.Count);

        // Not the degrade path either — this run genuinely planned and genuinely executed a plan.
        Assert.False(exec.FallbackCalled);
        Assert.Null(RunPauseEnvelope.ReadReason(settled)); // never parked
    }

    // ---- fallback-path controls ----------------------------------------------------------

    [Fact]
    public async Task ADeclinedPlanTurn_TakesOneTurn_AndNeverCallsTheSingleTurnFallback()
    {
        ProviderAlwaysAnswers(DeclineArgs(), ModelQuestion);
        var exec = new RecordingExecutor();

        var settled = await RunToSettlementAsync(exec, ThinGoal);

        Assert.Equal(1, _planTurns);       // no firm retry was burned on a model that HAD called emit_plan
        Assert.False(exec.FallbackCalled); // the absence of the call is what's asserted, not the end state
        Assert.Empty(settled.Plan);
        Assert.Equal(AgentRunState.WaitingForInput, settled.State);
        Assert.Equal(NeedsGoalReason, RunPauseEnvelope.ReadReason(settled));
    }

    /// <summary>Positive control for the fact above, on the same double: silence still triggers the fallback.</summary>
    [Fact]
    public async Task ASilentPlanTurn_StillDegrades_AndDoesCallTheSingleTurnFallback()
    {
        ProviderAlwaysAnswers(emitArgs: null, visible: "I am not sure what you want here."); // called nothing
        var exec = new RecordingExecutor();

        var settled = await RunToSettlementAsync(exec, ThinGoal);

        Assert.Equal(2, _planTurns);      // the firm retry — the turn a decline must NOT burn
        Assert.True(exec.FallbackCalled); // the control: this double can record the call it is asked about above
        Assert.Empty(settled.Plan);       // the degrade records no steps either
        // Silence is not a decline: no park, no token.
        Assert.Equal(AgentRunState.Completed, settled.State);
        Assert.Null(RunPauseEnvelope.ReadReason(settled));
        Assert.False(exec.PausedCalled);
    }

    [Fact]
    public async Task DeclinePath_AccruesThePlanTurnUsage_ToTheRunLedger()
    {
        ProviderAlwaysAnswers(DeclineArgs(), ModelQuestion,
            new UsageDetails { InputTokenCount = 31, OutputTokenCount = 7 });
        var exec = new RecordingExecutor();

        var settled = await RunToSettlementAsync(exec, ThinGoal);

        Assert.Equal(AgentRunState.WaitingForInput, settled.State); // non-vacuity: it really took the decline
        Assert.NotNull(settled.LedgerJson);
        using var doc = JsonDocument.Parse(settled.LedgerJson!);
        var root = doc.RootElement;
        Assert.Equal(31, root.GetProperty("inputTokens").GetInt64());
        Assert.Equal(7, root.GetProperty("outputTokens").GetInt64());
        // Planning is run-level spend (stepId: null); no per-step entry to open.
        Assert.Equal(0, root.GetProperty("perStep").GetArrayLength());
    }
}
