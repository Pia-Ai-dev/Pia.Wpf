using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Providers;
using Pia.Shared.Models;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>Covers both directions of resume re-planning: a needs-goal park with no step rows re-plans; every other park does not.</summary>
public sealed class AgentRunClarificationResumeTests
{
    /// <summary>Wire value literal, not a reference to the production constant, so a change to it would be caught.</summary>
    private const string NeedsGoalReason = "needs-goal";

    /// <summary>The mid-plan park token, as a literal, for the same reason as <see cref="NeedsGoalReason"/>.</summary>
    private const string NeedsInputReason = "needs-input";

    private const string ThinGoal = "ggg";

    private const string FirstAnswer = "I mean the golden-gate gateway config";

    private const string SecondAnswer = "the staging one, not production";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Persona Persona() => new() { Id = Guid.NewGuid(), Name = "Pia", SystemPrompt = "sys" };

    private static AiProvider Provider() => new()
    {
        Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI, SupportsToolCalling = true,
    };

    private static StepTurnResult Ok(string text = "done") =>
        new(true, false, null, text, null, Guid.NewGuid(), Guid.NewGuid());

    /// <summary>Steps as a PLANNER hands them over: <c>Id = Guid.Empty</c>, so the store mints the ids.</summary>
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

    /// <summary>Seeded directly so a zero call count on the resume means zero, not "one launch's calls minus one".</summary>
    private static AgentStep Persisted(int ordinal, string intent, AgentStepStatus status) => new()
    {
        Id = Guid.NewGuid(),
        Ordinal = ordinal,
        Title = intent,
        Intent = intent,
        Status = status,
    };

    // ---------------------------------------------------------------- the doubles

    /// <summary><c>PlanAsync</c> still receives <c>ctx.Goal</c> verbatim; answers travel separately via <c>ctx.Clarifications</c>.</summary>
    private sealed class SpyPlanner : IAgentPlanner
    {
        public Queue<PlanResult> Plans { get; } = new();

        public int PlanCalls { get; private set; }

        public int ReplanCalls { get; private set; }

        public List<string> PlannedGoals { get; } = new();

        public List<IReadOnlyList<string>> SeenClarifications { get; } = new();

        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
        {
            PlanCalls++;
            PlannedGoals.Add(goal);
            SeenClarifications.Add(ctx.Clarifications.ToList());
            return Task.FromResult(Plans.Count > 0 ? Plans.Dequeue() : PlanResult.Fallback);
        }

        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
        {
            ReplanCalls++;
            return Task.FromResult(PlanResult.Fallback);
        }
    }

    private sealed class SpyRunService : IAgentRunService
    {
        private readonly IAgentRunService _inner;

        public SpyRunService(IAgentRunService inner) => _inner = inner;

        public int ReplaceStepsCalls { get; private set; }

        /// <summary>Every <c>GetAsync</c> call; compared differentially against a control run rather than as an absolute count, since the loop makes other reads too.</summary>
        public int GetCalls { get; private set; }

        public List<AgentRunState> States { get; } = new();

        public Task ReplaceStepsAsync(Guid runId, IReadOnlyList<AgentStep> steps, CancellationToken ct = default)
        {
            ReplaceStepsCalls++;
            return _inner.ReplaceStepsAsync(runId, steps, ct);
        }

        public Task SetStateAsync(Guid runId, AgentRunState state, CancellationToken ct = default)
        {
            States.Add(state);
            return _inner.SetStateAsync(runId, state, ct);
        }

        public Task<AgentRun> CreateAsync(AgentRunCreateRequest request, CancellationToken ct = default) => _inner.CreateAsync(request, ct);
        public Task<AgentRun?> GetAsync(Guid runId, CancellationToken ct = default)
        {
            GetCalls++;
            return _inner.GetAsync(runId, ct);
        }

        public Task AddUsageAsync(Guid runId, Guid? stepId, UsageDetails usage, CancellationToken ct = default)
            => _inner.AddUsageAsync(runId, stepId, usage, ct);
        public Task SetRunMessageRangeAsync(Guid runId, Guid firstMessageId, Guid lastMessageId, CancellationToken ct = default)
            => _inner.SetRunMessageRangeAsync(runId, firstMessageId, lastMessageId, ct);
        public Task CompleteAsync(Guid runId, bool truncated = false, string? truncationReason = null, CancellationToken ct = default)
            => _inner.CompleteAsync(runId, truncated, truncationReason, ct);
        public Task FailAsync(
            Guid runId, string? error, bool cancelled = false, CancellationToken ct = default,
            PiaFailure? failure = null)
            => _inner.FailAsync(runId, error, cancelled, ct, failure);
        public Task PauseAsync(Guid runId, string? reason, CancellationToken ct = default, string? approvalTool = null,
            string? approvalArgs = null)
            => _inner.PauseAsync(runId, reason, ct, approvalTool, approvalArgs);
        public Task UpdatePolicyJsonAsync(Guid runId, string? policyJson, CancellationToken ct = default)
            => _inner.UpdatePolicyJsonAsync(runId, policyJson, ct);
        public Task<IReadOnlyList<string>> AppendClarificationAsync(Guid runId, string? answer, CancellationToken ct = default)
            => _inner.AppendClarificationAsync(runId, answer, ct);
        public Task<bool> TryBeginResumeAsync(Guid runId, CancellationToken ct = default) => _inner.TryBeginResumeAsync(runId, ct);
        public Task<bool> TryPauseUserAsync(Guid runId, CancellationToken ct = default) => _inner.TryPauseUserAsync(runId, ct);
        public Task<bool> TryResumeFromPauseAsync(Guid runId, CancellationToken ct = default) => _inner.TryResumeFromPauseAsync(runId, ct);
        public Task<bool> TryRejectParkedPlanAsync(Guid runId, CancellationToken ct = default) => _inner.TryRejectParkedPlanAsync(runId, ct);
        public Task BeginChildWaitAsync(Guid runId, int childCount, CancellationToken ct = default) => _inner.BeginChildWaitAsync(runId, childCount, ct);
        public Task<bool> TryEndChildWaitAsync(Guid runId, CancellationToken ct = default) => _inner.TryEndChildWaitAsync(runId, ct);
        public Task<int> FailInterruptedRunsAsync(CancellationToken ct = default) => _inner.FailInterruptedRunsAsync(ct);
        public Task<IReadOnlyList<AgentRun>> GetByChatAsync(Guid chatId, CancellationToken ct = default) => _inner.GetByChatAsync(chatId, ct);
        public Task<IReadOnlyList<AgentRun>> GetChildRunsAsync(Guid parentRunId, CancellationToken ct = default) => _inner.GetChildRunsAsync(parentRunId, ct);
        public Task<bool> ChatHasPlannedRunAsync(Guid chatId, CancellationToken ct = default) => _inner.ChatHasPlannedRunAsync(chatId, ct);
        public Task<bool> AnyExecutingRunForTriggerAsync(Guid triggerRef, CancellationToken ct = default) => _inner.AnyExecutingRunForTriggerAsync(triggerRef, ct);
        public Task<IReadOnlyList<ScheduledFiringOutcome>> GetLatestSettledFiringsAsync(CancellationToken ct = default) => _inner.GetLatestSettledFiringsAsync(ct);
        public Task<IReadOnlyList<ScheduledFiringOutcome>> GetFiringsForTriggerAsync(Guid triggerRef, int limit, CancellationToken ct = default)
            => _inner.GetFiringsForTriggerAsync(triggerRef, limit, ct);
        public Task<PlanMutationResult> ApplyPlanMutationAsync(Guid runId, IReadOnlyList<PlanStepEdit> pendingSteps, CancellationToken ct = default)
            => _inner.ApplyPlanMutationAsync(runId, pendingSteps, ct);
        public Task<AgentStep?> NextPendingStepAsync(Guid runId, CancellationToken ct = default) => _inner.NextPendingStepAsync(runId, ct);
        public Task SetStepStatusAsync(Guid stepId, AgentStepStatus status, CancellationToken ct = default) => _inner.SetStepStatusAsync(stepId, status, ct);
        public Task RecordStepResultAsync(Guid stepId, AgentStepStatus status, Guid? firstMessageId, Guid? lastMessageId,
            UsageDetails? usage, CancellationToken ct = default, string? artifactRef = null)
            => _inner.RecordStepResultAsync(stepId, status, firstMessageId, lastMessageId, usage, ct, artifactRef);

        public event EventHandler<AgentRunChangedEventArgs> RunChanged
        {
            add => _inner.RunChanged += value;
            remove => _inner.RunChanged -= value;
        }
    }

    private sealed class RecordingExecutor : IAgentTurnExecutor
    {
        public List<string> Executed { get; } = new();

        public bool FallbackCalled { get; private set; }

        public bool EndCalled { get; private set; }

        public bool PausedCalled { get; private set; }

        public Task BeginRunAsync(AgentRun run, RunContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task<StepTurnResult> ExecuteStepAsync(AgentRun run, AgentStep step, RunContext ctx, CancellationToken ct)
        {
            Executed.Add(step.Intent ?? step.Title);
            return Task.FromResult(Ok());
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
            PausedCalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _dir;

        public Harness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "PiaClarifyResume_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Ctx = new SqliteContext(Path.Combine(_dir, "history.db"));
            Runs = new AgentRunService(Ctx, NullLogger<AgentRunService>.Instance);
            Chats = new AssistantChatService(Ctx, Runs);
        }

        public SqliteContext Ctx { get; }

        public AgentRunService Runs { get; }

        public AssistantChatService Chats { get; }

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

        /// <summary>Parks and wins the resume claim as the launcher does, which NULLs <c>ExtraJson</c>.</summary>
        public async Task ParkAndClaimAsync(Guid runId, string reason)
        {
            await Runs.PauseAsync(runId, reason, Ct);
            Assert.True(await Runs.TryBeginResumeAsync(runId, Ct));
        }

        public AgentRunOrchestrator BuildOrchestrator(IAgentPlanner planner, IAgentRunService runService) =>
            new(runService, planner, new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance,
                workspaces: null, childLauncher: null, chats: null, steering: null);

        public void Dispose()
        {
            Runs.Dispose();
            Chats.Dispose();
            Ctx.Dispose();
            TempPath.Remove(_dir);
        }
    }

    // ============================================================================================
    // direction 1: a resumed needs-goal run RE-PLANS
    // ============================================================================================

    [Fact]
    public async Task Resume_NeedsGoalPark_WithNoStepRows_RePlans()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync(ThinGoal);

        // No ReplaceStepsAsync: a plan-time decline returns before it, so the park has an EMPTY plan.
        await h.ParkAndClaimAsync(run.Id, NeedsGoalReason);
        Assert.Empty((await h.Runs.GetAsync(run.Id, Ct))!.Plan);

        var planner = new SpyPlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1", "s2"), false));
        var spy = new SpyRunService(h.Runs);
        var exec = new RecordingExecutor();
        var resumed = (await h.Runs.GetAsync(run.Id, Ct))!;

        await h.BuildOrchestrator(planner, spy).RunAsync(
            resumed, exec, Persona(), Provider(), RunProfile.Interactive, Ct,
            resume: true, parkReason: NeedsGoalReason);

        Assert.Equal(1, planner.PlanCalls);
        Assert.Equal(1, spy.ReplaceStepsCalls);
        Assert.Contains(AgentRunState.Planning, spy.States);
        Assert.False(exec.FallbackCalled);

        // A real plan, not just a written one: both steps actually executed.
        Assert.Equal(new[] { "s1", "s2" }, exec.Executed);
        Assert.True(exec.EndCalled);
        var final = await h.Runs.GetAsync(run.Id, Ct);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.Equal(2, final.Plan.Count);
        Assert.All(final.Plan, s => Assert.Equal(AgentStepStatus.Done, s.Status));
    }

    // ============================================================================================
    // direction 2: a resumed mid-plan run does NOT re-plan — three ways of being direction 2
    // ============================================================================================

    /// <summary>Identical fixture to <see cref="Resume_NeedsGoalPark_WithNoStepRows_RePlans"/> except for the park reason: nothing plans.</summary>
    [Fact]
    public async Task Resume_NeedsInputPark_WithNoStepRows_DoesNotRePlan()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync(ThinGoal);
        await h.ParkAndClaimAsync(run.Id, NeedsInputReason);

        var planner = new SpyPlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("must-not-be-used"), false)); // armed, so silence is a CHOICE
        var spy = new SpyRunService(h.Runs);
        var exec = new RecordingExecutor();
        var resumed = (await h.Runs.GetAsync(run.Id, Ct))!;

        await h.BuildOrchestrator(planner, spy).RunAsync(
            resumed, exec, Persona(), Provider(), RunProfile.Interactive, Ct,
            resume: true, parkReason: NeedsInputReason);

        Assert.Equal(0, planner.PlanCalls);
        Assert.Equal(0, spy.ReplaceStepsCalls);
        Assert.DoesNotContain(AgentRunState.Planning, spy.States);
        Assert.Empty(exec.Executed);
        Assert.False(exec.FallbackCalled);
    }

    [Fact]
    public async Task Resume_NeedsGoalPark_ButStepRowsExist_DoesNotRePlan()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync(ThinGoal);
        await h.Runs.ReplaceStepsAsync(run.Id, new List<AgentStep>
        {
            Persisted(0, "s1", AgentStepStatus.Done),
            Persisted(1, "s2", AgentStepStatus.Pending),
        }, Ct);
        await h.ParkAndClaimAsync(run.Id, NeedsGoalReason);

        var planner = new SpyPlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("would-wipe-the-done-row"), false));
        var spy = new SpyRunService(h.Runs);
        var exec = new RecordingExecutor();
        var resumed = (await h.Runs.GetAsync(run.Id, Ct))!;

        await h.BuildOrchestrator(planner, spy).RunAsync(
            resumed, exec, Persona(), Provider(), RunProfile.Interactive, Ct,
            resume: true, parkReason: NeedsGoalReason);

        Assert.Equal(0, planner.PlanCalls);
        Assert.Equal(0, spy.ReplaceStepsCalls);
        Assert.DoesNotContain(AgentRunState.Planning, spy.States);

        // The Done row is neither re-run nor deleted; only the Pending remainder executes.
        Assert.Equal(new[] { "s2" }, exec.Executed);
        var final = await h.Runs.GetAsync(run.Id, Ct);
        Assert.Equal(2, final!.Plan.Count);
        Assert.Equal(new[] { "s1", "s2" }, final.Plan.OrderBy(s => s.Ordinal).Select(s => s.Title));
    }

    /// <summary>The partially-executed shape a mid-plan ask actually produces: one Done row plus the asking step reset to Pending.</summary>
    [Fact]
    public async Task Resume_NeedsInputPark_WithAPartiallyExecutedPlan_DrainsTheRemainder_AndNeverRePlans()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("a real goal");
        await h.Runs.ReplaceStepsAsync(run.Id, new List<AgentStep>
        {
            Persisted(0, "s1", AgentStepStatus.Done),
            // The step that asked was reset to Pending rather than recorded, so this matches what a resume actually sees.
            Persisted(1, "s2", AgentStepStatus.Pending),
        }, Ct);
        await h.ParkAndClaimAsync(run.Id, NeedsInputReason);
        await h.Runs.AppendClarificationAsync(run.Id, FirstAnswer, Ct);

        var planner = new SpyPlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("would-wipe-the-done-row"), false)); // armed: silence is a CHOICE
        var spy = new SpyRunService(h.Runs);
        var exec = new RecordingExecutor();
        var resumed = (await h.Runs.GetAsync(run.Id, Ct))!;

        await h.BuildOrchestrator(planner, spy).RunAsync(
            resumed, exec, Persona(), Provider(), RunProfile.Interactive, Ct,
            resume: true, nudge: FirstAnswer, parkReason: NeedsInputReason);

        Assert.Equal(0, planner.PlanCalls);
        Assert.Equal(0, spy.ReplaceStepsCalls);
        Assert.DoesNotContain(AgentRunState.Planning, spy.States);
        Assert.False(exec.FallbackCalled);

        // Only the step that asked re-ran; the Done row survived un-re-executed and undeleted.
        Assert.Equal(new[] { "s2" }, exec.Executed);
        var final = await h.Runs.GetAsync(run.Id, Ct);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.Equal(2, final.Plan.Count);
        Assert.Equal(new[] { "s1", "s2" }, final.Plan.OrderBy(s => s.Ordinal).Select(s => s.Title));

        // The answer was not thrown away by the park it belongs to.
        Assert.Contains(FirstAnswer, RunClarifications.Read(final.ClarificationsJson));
    }

    [Fact]
    public async Task Resume_WithNoParkReason_DoesNotRePlan()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync(ThinGoal);
        await h.ParkAndClaimAsync(run.Id, NeedsGoalReason); // the ROW parked needs-goal …

        var planner = new SpyPlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("must-not-be-used"), false));
        var spy = new SpyRunService(h.Runs);
        var exec = new RecordingExecutor();
        var resumed = (await h.Runs.GetAsync(run.Id, Ct))!;

        // …but this dispatch doesn't say so; the claim above NULLed the token, so it can't be recovered here.
        await h.BuildOrchestrator(planner, spy).RunAsync(
            resumed, exec, Persona(), Provider(), RunProfile.Interactive, Ct, resume: true);

        Assert.Equal(0, planner.PlanCalls);
        Assert.Equal(0, spy.ReplaceStepsCalls);
        Assert.DoesNotContain(AgentRunState.Planning, spy.States);
    }

    [Fact]
    public async Task Resume_StepCapPark_DrainsTheRemainder_AndNeverRePlans()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("a real goal");
        await h.Runs.ReplaceStepsAsync(run.Id, new List<AgentStep>
        {
            Persisted(0, "s1", AgentStepStatus.Done),
            Persisted(1, "s2", AgentStepStatus.Pending),
        }, Ct);
        await h.ParkAndClaimAsync(run.Id, "step-cap");

        var planner = new SpyPlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("must-not-be-used"), false));
        var spy = new SpyRunService(h.Runs);
        var exec = new RecordingExecutor();
        var resumed = (await h.Runs.GetAsync(run.Id, Ct))!;

        await h.BuildOrchestrator(planner, spy).RunAsync(
            resumed, exec, Persona(), Provider(), RunProfile.Interactive, Ct,
            resume: true, parkReason: "step-cap");

        Assert.Equal(0, planner.PlanCalls);
        Assert.Equal(0, spy.ReplaceStepsCalls);
        Assert.Equal(new[] { "s2" }, exec.Executed);
        Assert.Equal(AgentRunState.Completed, (await h.Runs.GetAsync(run.Id, Ct))!.State);
    }

    [Fact]
    public async Task Launch_IgnoresTheParkReason_AndStillPlansExactlyOnce()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("a real goal");

        var planner = new SpyPlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1"), false));
        var spy = new SpyRunService(h.Runs);
        var exec = new RecordingExecutor();

        await h.BuildOrchestrator(planner, spy).RunAsync(
            run, exec, Persona(), Provider(), RunProfile.Interactive, Ct, parkReason: NeedsGoalReason);

        Assert.Equal(1, planner.PlanCalls);
        Assert.Equal(1, spy.ReplaceStepsCalls);
        Assert.Equal(new[] { "s1" }, exec.Executed);

        // Control: identical launch with no reason at all; any difference in store reads would be the guard.
        var control = await h.NewRunAsync("a real goal");
        var controlPlanner = new SpyPlanner();
        controlPlanner.Plans.Enqueue(new PlanResult(MakeSteps("s1"), false));
        var controlSpy = new SpyRunService(h.Runs);

        await h.BuildOrchestrator(controlPlanner, controlSpy).RunAsync(
            control, new RecordingExecutor(), Persona(), Provider(), RunProfile.Interactive, Ct);

        Assert.Equal(controlSpy.GetCalls, spy.GetCalls);
    }

    // ============================================================================================
    // The answers: persisted beside the goal, and READ by the re-plan
    // ============================================================================================

    [Fact]
    public async Task Resume_NeedsGoalPark_SeedsThePersistedAnswersIntoTheRunContext()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync(ThinGoal);
        await h.Runs.AppendClarificationAsync(run.Id, FirstAnswer, Ct);
        await h.Runs.AppendClarificationAsync(run.Id, SecondAnswer, Ct);
        await h.ParkAndClaimAsync(run.Id, NeedsGoalReason);

        var planner = new SpyPlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1"), false));
        var spy = new SpyRunService(h.Runs);
        var resumed = (await h.Runs.GetAsync(run.Id, Ct))!;

        await h.BuildOrchestrator(planner, spy).RunAsync(
            resumed, new RecordingExecutor(), Persona(), Provider(), RunProfile.Interactive, Ct,
            resume: true, parkReason: NeedsGoalReason);

        var seen = Assert.Single(planner.SeenClarifications);
        Assert.Equal(new[] { FirstAnswer, SecondAnswer }, seen);         // oldest-first, both of them
        Assert.Equal(new[] { ThinGoal }, planner.PlannedGoals);          // the goal argument is NOT rewritten
    }

    /// <summary>The panel renders <c>AgentRuns.Goal</c> directly, so a clarification cycle must leave it byte-identical.</summary>
    [Fact]
    public async Task ClarificationCycle_LeavesTheGoalColumnExactlyAsTheUserTypedIt()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync(ThinGoal);
        await h.Runs.AppendClarificationAsync(run.Id, FirstAnswer, Ct);
        await h.ParkAndClaimAsync(run.Id, NeedsGoalReason);

        var planner = new SpyPlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1"), false));
        var resumed = (await h.Runs.GetAsync(run.Id, Ct))!;

        await h.BuildOrchestrator(planner, new SpyRunService(h.Runs)).RunAsync(
            resumed, new RecordingExecutor(), Persona(), Provider(), RunProfile.Interactive, Ct,
            resume: true, parkReason: NeedsGoalReason);

        var final = await h.Runs.GetAsync(run.Id, Ct);
        Assert.Equal(ThinGoal, final!.Goal);
        Assert.DoesNotContain(FirstAnswer, final.Goal!);
    }

    [Fact]
    public async Task TwoParks_TwoAnswers_AndTheSecondRePlanSeesBoth()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync(ThinGoal);
        var planner = new SpyPlanner();
        var spy = new SpyRunService(h.Runs);

        // ---- park 1, answered → the re-plan declines again and re-parks ----
        await h.Runs.AppendClarificationAsync(run.Id, FirstAnswer, Ct);
        await h.ParkAndClaimAsync(run.Id, NeedsGoalReason);
        planner.Plans.Enqueue(PlanResult.Decline("still not sure what you mean"));
        await h.BuildOrchestrator(planner, spy).RunAsync(
            (await h.Runs.GetAsync(run.Id, Ct))!, new RecordingExecutor(), Persona(), Provider(),
            RunProfile.Interactive, Ct, resume: true, parkReason: NeedsGoalReason);

        var afterFirst = await h.Runs.GetAsync(run.Id, Ct);
        Assert.Equal(AgentRunState.WaitingForInput, afterFirst!.State); // parked again, not terminal
        Assert.Empty(afterFirst.Plan);                                  // still zero step rows

        // ---- park 2, answered → the second re-plan gets BOTH answers ----
        await h.Runs.AppendClarificationAsync(run.Id, SecondAnswer, Ct);
        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, Ct));
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1"), false));
        await h.BuildOrchestrator(planner, spy).RunAsync(
            (await h.Runs.GetAsync(run.Id, Ct))!, new RecordingExecutor(), Persona(), Provider(),
            RunProfile.Interactive, Ct, resume: true, parkReason: NeedsGoalReason);

        Assert.Equal(2, planner.PlanCalls);
        Assert.Equal(new[] { FirstAnswer }, planner.SeenClarifications[0]);
        Assert.Equal(new[] { FirstAnswer, SecondAnswer }, planner.SeenClarifications[1]);
        Assert.Equal(AgentRunState.Completed, (await h.Runs.GetAsync(run.Id, Ct))!.State);
    }

    // ============================================================================================
    // The store: accumulation, and where the text must NOT end up
    // ============================================================================================

    /// <summary>A consumer may log the pause envelope wholesale, so the answers must never be in it.</summary>
    [Fact]
    public async Task AppendClarificationAsync_Accumulates_SurvivesTheResumeClaim_AndStaysOutOfThePauseEnvelope()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync(ThinGoal);

        Assert.Equal(new[] { FirstAnswer }, await h.Runs.AppendClarificationAsync(run.Id, FirstAnswer, Ct));
        Assert.Equal(new[] { FirstAnswer, SecondAnswer },
            await h.Runs.AppendClarificationAsync(run.Id, SecondAnswer, Ct));

        await h.Runs.PauseAsync(run.Id, NeedsGoalReason, Ct);
        var parked = await h.Runs.GetAsync(run.Id, Ct);
        Assert.DoesNotContain(FirstAnswer, parked!.ExtraJson ?? string.Empty);
        Assert.DoesNotContain(SecondAnswer, parked.ExtraJson ?? string.Empty);

        // The claim NULLs ExtraJson — that is exactly why the answers cannot live there.
        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, Ct));
        var claimed = await h.Runs.GetAsync(run.Id, Ct);
        Assert.Null(claimed!.ExtraJson);
        Assert.Equal(new[] { FirstAnswer, SecondAnswer }, RunClarifications.Read(claimed.ClarificationsJson));
    }

    /// <summary>Write-time bounds only: neither cap limits how many times a run may ask.</summary>
    [Fact]
    public async Task AppendClarificationAsync_PastTheStoredBounds_KeepsTheNewest_AndHeadKeepsALongAnswer()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync(ThinGoal);

        // One more than the keep cap, each answer identifiable by its own index.
        for (var i = 1; i <= RunClarifications.MaxAnswers + 1; i++)
            await h.Runs.AppendClarificationAsync(run.Id, "answer " + i, Ct);

        var kept = RunClarifications.Read((await h.Runs.GetAsync(run.Id, Ct))!.ClarificationsJson);
        Assert.Equal(RunClarifications.MaxAnswers, kept.Count);
        Assert.Equal("answer 2", kept[0]);                                    // the oldest was dropped
        Assert.Equal("answer " + (RunClarifications.MaxAnswers + 1), kept[^1]); // the newest is kept, last
        Assert.DoesNotContain("answer 1", kept);

        // Fresh run so the keep cap isn't also in play; head-kept + ellipsis so a long pasted answer isn't resent whole.
        var other = await h.NewRunAsync(ThinGoal);
        var stored = Assert.Single(
            await h.Runs.AppendClarificationAsync(other.Id, new string('x', RunClarifications.MaxAnswerChars + 50), Ct));
        Assert.Equal(RunClarifications.MaxAnswerChars + 1, stored.Length); // the cap, plus the one-char ellipsis
        Assert.EndsWith("…", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppendClarificationAsync_BlankAnswer_WritesNothing_AndAnAnswerlessResumeStillRePlans()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync(ThinGoal);

        Assert.Empty(await h.Runs.AppendClarificationAsync(run.Id, "   ", Ct));
        Assert.Empty(await h.Runs.AppendClarificationAsync(run.Id, null, Ct));
        Assert.Null((await h.Runs.GetAsync(run.Id, Ct))!.ClarificationsJson);

        await h.ParkAndClaimAsync(run.Id, NeedsGoalReason);
        var planner = new SpyPlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1"), false));
        await h.BuildOrchestrator(planner, new SpyRunService(h.Runs)).RunAsync(
            (await h.Runs.GetAsync(run.Id, Ct))!, new RecordingExecutor(), Persona(), Provider(),
            RunProfile.Interactive, Ct, resume: true, parkReason: NeedsGoalReason);

        Assert.Equal(1, planner.PlanCalls);
        Assert.Empty(Assert.Single(planner.SeenClarifications));
    }

    [Fact]
    public async Task AnExistingDatabase_GainsClarificationsJson_AndKeepsItsRuns()
    {
        // Own directory, not the Harness's, since the DB file must outlive the first "launch" for the second to migrate it.
        var dir = Path.Combine(Path.GetTempPath(), "PiaClarifyMigrate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "history.db");
        try
        {
            Guid runId;
            // ---- launch 1: create a run, then drop the column to simulate an old schema ----
            using (var ctx = new SqliteContext(dbPath))
            using (var runs = new AgentRunService(ctx, NullLogger<AgentRunService>.Instance))
            {
                var chats = new AssistantChatService(ctx, runs);
                var chatId = Guid.NewGuid();
                var now = DateTime.UtcNow;
                await chats.SaveAsync(new SyncAssistantChat
                {
                    Id = chatId,
                    SchemaVersion = 1,
                    Title = "t",
                    CreatedAt = now,
                    UpdatedAt = now,
                    LastAccessedAt = now,
                    WindowMode = WindowMode.Assistant.ToString(),
                    Messages = [],
                }, Ct);
                runId = (await runs.CreateAsync(
                    new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: ThinGoal), Ct)).Id;

                // DROP rather than a pasted old CREATE TABLE: defined against whatever this build creates.
                using var drop = ctx.GetConnection().CreateCommand();
                drop.CommandText = "ALTER TABLE AgentRuns DROP COLUMN ClarificationsJson";
                drop.ExecuteNonQuery();
            }

            // ---- launch 2: EnsureSchema + MigrateSchema run against the existing file ----
            using var reopened = new SqliteContext(dbPath);
            var columns = new List<string>();
            using (var pragma = reopened.GetConnection().CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info(AgentRuns)";
                using var r = pragma.ExecuteReader();
                while (r.Read()) columns.Add(r.GetString(1));
            }
            // CREATE TABLE IF NOT EXISTS is a no-op on an existing table, so the only thing that can have added
            // the column is MigrateSchema's ALTER pass.
            Assert.Contains("ClarificationsJson", columns);

            // The migrated run survives, still readable, and reads NULL in the new column.
            using var migratedRuns = new AgentRunService(reopened, NullLogger<AgentRunService>.Instance);
            var migrated = await migratedRuns.GetAsync(runId, Ct);
            Assert.NotNull(migrated);
            Assert.Equal(ThinGoal, migrated!.Goal);
            Assert.Null(migrated.ClarificationsJson);
            Assert.Empty(RunClarifications.Read(migrated.ClarificationsJson));

            // …and it is writable straight away, i.e. the ALTER produced a usable column and not just a name.
            Assert.Equal(new[] { FirstAnswer }, await migratedRuns.AppendClarificationAsync(runId, FirstAnswer, Ct));

            // Idempotent: a THIRD launch must not re-issue the ALTER (SQLite errors on a duplicate column name
            // and MigrateSchema has no try/catch — an unguarded ALTER takes startup down on every later launch).
            migratedRuns.Dispose();
            reopened.Dispose();
            using var third = new SqliteContext(dbPath);
            Assert.NotNull(third.GetConnection());
        }
        finally
        {
            TempPath.Remove(dir);
        }
    }

    // ============================================================================================
    // The prompt: what the model actually receives
    // ============================================================================================

    /// <summary>The tokenizer only rewrites User-role text, so the answers must ride the user message.</summary>
    [Fact]
    public async Task PlanTurn_CarriesTheAnswersOnTheUserMessage_NeverOnTheSystemPrompt()
    {
        var captured = await CapturePlanMessagesAsync(ctx => ctx.SetClarifications([FirstAnswer, SecondAnswer]));

        var user = Assert.Single(captured, m => m.Role == ChatRole.User);
        Assert.Contains(ThinGoal, user.Text);
        Assert.Contains(FirstAnswer, user.Text);
        Assert.Contains(SecondAnswer, user.Text);

        var system = Assert.Single(captured, m => m.Role == ChatRole.System);
        Assert.DoesNotContain(FirstAnswer, system.Text);
        Assert.DoesNotContain(SecondAnswer, system.Text);
    }

    [Fact]
    public async Task PlanTurn_WithNoAnswers_SendsTheGoalUnchanged()
    {
        var captured = await CapturePlanMessagesAsync(_ => { });

        var user = Assert.Single(captured, m => m.Role == ChatRole.User);
        Assert.Equal(ThinGoal, user.Text);
    }

    /// <summary>Drives one real plan turn through a stubbed provider and returns the messages it sent.</summary>
    private static async Task<IList<ChatMessage>> CapturePlanMessagesAsync(Action<RunContext> seed)
    {
        var ai = Substitute.For<IAiClientService>();
        IList<ChatMessage>? sent = null;
        ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                sent ??= ci.ArgAt<IList<ChatMessage>>(0);
                return PlanStream();
            });

        var settings = Substitute.For<ISettingsService>();
        // The default AppSettings leaves the reason-then-emit turn OFF, so this is the single constrained turn.
        settings.GetSettingsAsync().Returns(_ => Task.FromResult(new AppSettings()));
        var handler = Substitute.For<IAiProviderHandler>();
        handler.ProviderType.Returns(AiProviderType.OpenAI);
        handler.DropsReasoningEffortWithTools.Returns(false);
        var planner = new AgentPlanner(ai, new AiProviderHandlerResolver([handler]), settings,
            NullLogger<AgentPlanner>.Instance);

        var ctx = new RunContext(ThinGoal, RunProfile.Interactive);
        seed(ctx);
        await planner.PlanAsync(ThinGoal, ctx, Persona(), Provider(), Ct);

        Assert.NotNull(sent); // a fixture that never reached the provider would make either fact vacuous
        return sent!;
    }

    /// <summary>Finishes immediately; what comes back is irrelevant since the planner just degrades.</summary>
    private static async IAsyncEnumerable<ChatStreamItem> PlanStream()
    {
        await Task.Yield();
        yield return new Finished(null, "test-model");
    }
}
