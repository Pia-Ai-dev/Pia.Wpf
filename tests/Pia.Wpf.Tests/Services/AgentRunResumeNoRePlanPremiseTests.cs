using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>Measured on spies, not end state: a re-planning resume that produced the same steps would pass an end-state check.</summary>
public sealed class AgentRunResumeNoRePlanPremiseTests
{
    private static Persona Persona() => new() { Name = "Pia", SystemPrompt = "sys" };

    private static AiProvider Provider() => new() { Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };

    private static StepTurnResult Ok(string text = "done") =>
        new(true, false, null, text, null, Guid.NewGuid(), Guid.NewGuid());

    /// <summary>Steps as a planner would return them: <c>Id = Guid.Empty</c>, so the store mints the ids.</summary>
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

    /// <summary>Written directly so a seeded park has no prior dispatch and a zero call count means zero.</summary>
    private static AgentStep Persisted(int ordinal, string intent, AgentStepStatus status) => new()
    {
        Id = Guid.NewGuid(),
        Ordinal = ordinal,
        Title = intent,
        Intent = intent,
        Status = status,
    };

    /// <summary>The two entry points are counted separately because <c>ReplanAsync</c> is also reachable from a resume's verify-fail branch.</summary>
    private sealed class SpyPlanner : IAgentPlanner
    {
        public Queue<PlanResult> Plans { get; } = new();

        public Queue<PlanResult> Replans { get; } = new();

        public int PlanCalls { get; private set; }

        public int ReplanCalls { get; private set; }

        /// <summary>Lets a control fact show the planner was wired to this run rather than merely invoked.</summary>
        public List<string> PlannedGoals { get; } = new();

        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
        {
            PlanCalls++;
            PlannedGoals.Add(goal);
            return Task.FromResult(Plans.Count > 0 ? Plans.Dequeue() : PlanResult.Fallback);
        }

        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
        {
            ReplanCalls++;
            return Task.FromResult(Replans.Count > 0 ? Replans.Dequeue() : PlanResult.Fallback);
        }
    }

    private sealed class SpyRunService : IAgentRunService
    {
        private readonly IAgentRunService _inner;

        public SpyRunService(IAgentRunService inner) => _inner = inner;

        public int ReplaceStepsCalls { get; private set; }

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
        public Task<AgentRun?> GetAsync(Guid runId, CancellationToken ct = default) => _inner.GetAsync(runId, ct);
        public Task AddUsageAsync(Guid runId, Guid? stepId, UsageDetails usage, CancellationToken ct = default)
            => _inner.AddUsageAsync(runId, stepId, usage, ct);
        public Task SetRunMessageRangeAsync(Guid runId, Guid firstMessageId, Guid lastMessageId, CancellationToken ct = default)
            => _inner.SetRunMessageRangeAsync(runId, firstMessageId, lastMessageId, ct);
        public Task CompleteAsync(Guid runId, bool truncated = false, string? truncationReason = null, CancellationToken ct = default)
            => _inner.CompleteAsync(runId, truncated, truncationReason, ct);
        public Task FailAsync(Guid runId, string? error, bool cancelled = false, CancellationToken ct = default)
            => _inner.FailAsync(runId, error, cancelled, ct);
        public Task PauseAsync(Guid runId, string? reason, CancellationToken ct = default, string? approvalTool = null)
            => _inner.PauseAsync(runId, reason, ct, approvalTool);
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
            UsageDetails? usage, CancellationToken ct = default)
            => _inner.RecordStepResultAsync(stepId, status, firstMessageId, lastMessageId, usage, ct);

        public event EventHandler<AgentRunChangedEventArgs> RunChanged
        {
            add => _inner.RunChanged += value;
            remove => _inner.RunChanged -= value;
        }
    }

    private sealed class RecordingExecutor : IAgentTurnExecutor
    {
        private readonly Func<AgentStep, StepTurnResult> _result;

        public RecordingExecutor(Func<AgentStep, StepTurnResult> result) => _result = result;

        public List<string> Executed { get; } = new();

        public bool BeginCalled { get; private set; }

        public bool EndCalled { get; private set; }

        public bool EndCancelled { get; private set; }

        public bool EndFailed { get; private set; }

        public bool FallbackCalled { get; private set; }

        public bool PausedCalled { get; private set; }

        public Task BeginRunAsync(AgentRun run, RunContext ctx, CancellationToken ct)
        {
            BeginCalled = true;
            return Task.CompletedTask;
        }

        public Task<StepTurnResult> ExecuteStepAsync(AgentRun run, AgentStep step, RunContext ctx, CancellationToken ct)
        {
            Executed.Add(step.Intent ?? step.Title);
            return Task.FromResult(_result(step));
        }

        public Task<StepTurnResult> RunSingleTurnFallbackAsync(AgentRun run, RunContext ctx, CancellationToken ct)
        {
            FallbackCalled = true;
            return Task.FromResult(Ok("fallback"));
        }

        public Task EndRunAsync(AgentRun run, RunContext ctx, bool cancelled, bool failed, CancellationToken ct)
        {
            EndCalled = true;
            EndCancelled = cancelled;
            EndFailed = failed;
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
            _dir = Path.Combine(Path.GetTempPath(), "PiaResumePremise_" + Guid.NewGuid().ToString("N"));
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

        public AgentRunOrchestrator BuildOrchestrator(
            IAgentPlanner planner, IAgentRunService runService, IAgentVerifier? verifier = null) =>
            new(runService, planner, verifier ?? new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance,
                workspaces: null, childLauncher: null, chats: null, steering: null);

        public void Dispose()
        {
            Runs.Dispose();
            Ctx.Dispose();
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }
    }

    // ------------------------------------------------------- the premise itself

    /// <summary>The park is seeded through the store, so the resume is the only dispatch and its zero counts mean zero.</summary>
    [Fact]
    public async Task Resume_CallsNeitherPlanAsyncNorReplaceSteps_AndNeverEntersPlanning()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");

        // ReplaceStepsAsync writes the plan verbatim, so calling it here would overwrite the Done row too.
        await h.Runs.ReplaceStepsAsync(run.Id, new List<AgentStep>
        {
            Persisted(0, "s1", AgentStepStatus.Done),
            Persisted(1, "s2", AgentStepStatus.Pending),
        }, ct);
        await h.Runs.PauseAsync(run.Id, "step-cap", ct);
        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, ct)); // the CAS claim the launcher makes first

        var planner = new SpyPlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("must-not-be-used"), false)); // armed, so silence is a CHOICE
        var spy = new SpyRunService(h.Runs);
        var exec = new RecordingExecutor(_ => Ok());
        // Fetched from the store, exactly as HeadlessRunLauncher.ResumeAsync hands it over.
        var resumed = (await h.Runs.GetAsync(run.Id, ct))!;

        await h.BuildOrchestrator(planner, spy)
            .RunAsync(resumed, exec, Persona(), Provider(), RunProfile.Interactive, ct, resume: true);

        Assert.Equal(0, planner.PlanCalls);
        Assert.Equal(0, spy.ReplaceStepsCalls);
        Assert.DoesNotContain(AgentRunState.Planning, spy.States);
        // …and a fourth: no replan either, because nothing failed and the critic accepted.
        Assert.Equal(0, planner.ReplanCalls);
        Assert.False(exec.FallbackCalled); // nor the fallback degrade path

        // Non-vacuity: a fixture where RunAsync did nothing at all would also satisfy the absences above.
        Assert.Equal(new[] { "s2" }, exec.Executed);
        Assert.True(exec.BeginCalled);
        Assert.True(exec.EndCalled);
        var final = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.Equal(2, final.Plan.Count); // the plan was never rewritten
        Assert.All(final.Plan, s => Assert.Equal(AgentStepStatus.Done, s.Status));
    }

    // ------------------------------------------------------------- the controls

    /// <summary>Control for the fact above: it shows the harness really does wire a reachable planner.</summary>
    [Fact]
    public async Task Launch_CallsPlanAsyncOnce_AndWritesThePlan()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("the goal");

        var planner = new SpyPlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1"), false));
        var spy = new SpyRunService(h.Runs);
        var exec = new RecordingExecutor(_ => Ok());

        await h.BuildOrchestrator(planner, spy)
            .RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, ct);

        Assert.Equal(1, planner.PlanCalls);
        Assert.Equal(new[] { "the goal" }, planner.PlannedGoals); // the planner was reached for THIS run
        Assert.Equal(1, spy.ReplaceStepsCalls);
        Assert.Contains(AgentRunState.Planning, spy.States);
        Assert.Equal(new[] { "s1" }, exec.Executed);
        Assert.Equal(AgentRunState.Completed, (await h.Runs.GetAsync(run.Id, ct))!.State);
    }

    /// <summary>One planner instance and one spy store across a real park, so the only difference between the dispatches is the <c>resume</c> flag.</summary>
    [Fact]
    public async Task LaunchThenResume_OnOneWiring_AddsNoSecondPlanCall()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");

        var planner = new SpyPlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1", "s2", "s3"), false));
        var spy = new SpyRunService(h.Runs);

        // Launch with a 2-step budget: s1 and s2 run, then the run parks at the cap with s3 still Pending.
        var capped = new RunProfile(MaxSteps: 2, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20));
        var first = new RecordingExecutor(_ => Ok());
        await h.BuildOrchestrator(planner, spy).RunAsync(run, first, Persona(), Provider(), capped, ct);

        Assert.Equal(new[] { "s1", "s2" }, first.Executed);
        Assert.Equal(AgentRunState.WaitingForInput, (await h.Runs.GetAsync(run.Id, ct))!.State);
        Assert.Equal(1, planner.PlanCalls);      // the launch's one plan turn …
        Assert.Equal(1, spy.ReplaceStepsCalls);  // … and its one plan write

        // Resume the way the launcher does: claim the row, re-fetch it, re-enter with resume: true.
        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, ct));
        var resumed = (await h.Runs.GetAsync(run.Id, ct))!;
        var second = new RecordingExecutor(_ => Ok());
        await h.BuildOrchestrator(planner, spy)
            .RunAsync(resumed, second, Persona(), Provider(), RunProfile.Interactive, ct, resume: true);

        // Neither count moved, on the very planner instance that answered the launch.
        Assert.Equal(1, planner.PlanCalls);
        Assert.Equal(1, spy.ReplaceStepsCalls);
        Assert.Equal(0, planner.ReplanCalls);
        // Non-vacuity: the resume drained the remainder, so it did reach the drain loop.
        Assert.Equal(new[] { "s3" }, second.Executed);
        Assert.Equal(AgentRunState.Completed, (await h.Runs.GetAsync(run.Id, ct))!.State);
    }

    // ------------------------------------- the zero-step resume, characterised

    /// <summary>A zero-step resume settles <c>Completed</c> un-truncated — it reports the goal as done having done no work.</summary>
    [Fact]
    public async Task ZeroStepResume_PlansNothing_DrainsNothing_AndSettlesCompleted()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("ggg");

        // No ReplaceStepsAsync at all: the run parks with an empty plan.
        await h.Runs.PauseAsync(run.Id, "needs-goal", ct);
        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, ct));
        Assert.Empty((await h.Runs.GetAsync(run.Id, ct))!.Plan); // premise of this fact, asserted

        var planner = new SpyPlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("would-be-a-plan"), false)); // armed and still not called
        var spy = new SpyRunService(h.Runs);
        var verifier = new FakeVerifier();
        var exec = new RecordingExecutor(_ => Ok());
        var resumed = (await h.Runs.GetAsync(run.Id, ct))!;

        await h.BuildOrchestrator(planner, spy, verifier)
            .RunAsync(resumed, exec, Persona(), Provider(), RunProfile.Interactive, ct, resume: true);

        // (1) nothing planned, nothing written, nothing run — the guard held, and that is the problem.
        Assert.Equal(0, planner.PlanCalls);
        Assert.Equal(0, planner.ReplanCalls);
        Assert.Equal(0, spy.ReplaceStepsCalls);
        Assert.DoesNotContain(AgentRunState.Planning, spy.States);
        Assert.Empty(exec.Executed);
        Assert.False(exec.FallbackCalled); // it does not fall into the fallback degrade path either

        // (2) the critic ran once, on nothing.
        Assert.Equal(1, verifier.VerifyCalls);
        Assert.Empty(Assert.Single(verifier.SeenCompletedSteps));

        // (3) and the run reports SUCCESS. Not parked again, not failed, not truncated.
        Assert.True(exec.EndCalled);
        Assert.False(exec.EndCancelled);
        Assert.False(exec.EndFailed);
        Assert.False(exec.PausedCalled);
        var final = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.NotNull(final.CompletedAt);
        Assert.Empty(final.Plan);
        Assert.DoesNotContain("truncated", final.ExtraJson ?? string.Empty);
    }

    /// <summary>A resume does reach the planner on a failed verdict, through <c>ReplanAsync</c> rather than <c>PlanAsync</c>.</summary>
    [Fact]
    public async Task ZeroStepResume_WhoseVerifyFails_ReachesReplanAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("ggg");
        await h.Runs.PauseAsync(run.Id, "needs-goal", ct);
        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, ct));

        var planner = new SpyPlanner();
        planner.Replans.Enqueue(new PlanResult(MakeSteps("r1"), false));
        var spy = new SpyRunService(h.Runs);
        var verifier = new FakeVerifier();
        verifier.Verdicts.Enqueue(new VerdictResult(false, "nothing was done", new[] { "x" }, null)); // fail → replan
        // Second verdict left to the default Accept, so the re-drain settles instead of exhausting the budget.
        var exec = new RecordingExecutor(_ => Ok());
        var resumed = (await h.Runs.GetAsync(run.Id, ct))!;

        await h.BuildOrchestrator(planner, spy, verifier)
            .RunAsync(resumed, exec, Persona(), Provider(), RunProfile.Interactive, ct, resume: true);

        // The planner was reached on this resume, through ReplanAsync, never through PlanAsync.
        Assert.Equal(0, planner.PlanCalls);
        Assert.Equal(1, planner.ReplanCalls);
        Assert.Equal(1, spy.ReplaceStepsCalls); // and the revised plan really was written
        Assert.Equal(new[] { "r1" }, exec.Executed);

        var final = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.Equal(AgentStepStatus.Done, Assert.Single(final.Plan).Status);
    }
}
