using System.IO;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The plan → act → failure-only-replan → complete loop (§13.2/§13.12). Uses fake planner/executor
/// + a real SQLite <see cref="AgentRunService"/> so the R2 re-query, R5 truncation, replan bound,
/// and R13 cancellation are exercised against the real persisted step store.
/// </summary>
public sealed class AgentRunOrchestratorTests
{
    private static Persona Persona() => new() { Name = "Pia", SystemPrompt = "sys" };
    private static AiProvider Provider() => new() { Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };

    private static StepTurnResult Ok(string text = "done") => new(true, false, null, text, null, Guid.NewGuid(), Guid.NewGuid());
    private static StepTurnResult OkUsage(long input, long output) =>
        new(true, false, null, "done", new UsageDetails { InputTokenCount = input, OutputTokenCount = output }, Guid.NewGuid(), Guid.NewGuid());
    private static StepTurnResult Fail(string err) => new(false, false, err, string.Empty, null, Guid.NewGuid(), Guid.NewGuid());
    private static StepTurnResult Cancel() => new(false, true, "cancelled", string.Empty, null, Guid.NewGuid(), Guid.NewGuid());

    private static List<AgentStep> MakeSteps(params (string Title, string Intent)[] steps)
    {
        var result = new List<AgentStep>();
        for (var i = 0; i < steps.Length; i++)
            result.Add(new AgentStep { Id = Guid.Empty, Ordinal = i, Title = steps[i].Title, Intent = steps[i].Intent, Status = AgentStepStatus.Pending });
        return result;
    }

    private sealed class FakePlanner : IAgentPlanner
    {
        public Queue<PlanResult> Plans { get; } = new();
        public Queue<PlanResult> Replans { get; } = new();
        public int ReplanCalls { get; private set; }

        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(Plans.Count > 0 ? Plans.Dequeue() : PlanResult.Fallback);

        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
        {
            ReplanCalls++;
            return Task.FromResult(Replans.Count > 0 ? Replans.Dequeue() : PlanResult.Fallback);
        }
    }

    private sealed class RecordingExecutor : IAgentTurnExecutor
    {
        private readonly Func<AgentStep, StepTurnResult> _result;
        public List<string> Executed { get; } = new();
        public bool BeginCalled { get; private set; }
        public bool EndCalled { get; private set; }
        public bool EndCancelled { get; private set; }
        public bool EndFailed { get; private set; }
        public bool FallbackCalled { get; private set; }
        public bool PausedCalled { get; private set; }

        public RecordingExecutor(Func<AgentStep, StepTurnResult> result) => _result = result;

        public Task BeginRunAsync(AgentRun run, RunContext ctx, CancellationToken ct) { BeginCalled = true; return Task.CompletedTask; }

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
            EndCalled = true; EndCancelled = cancelled; EndFailed = failed;
            return Task.CompletedTask;
        }

        public Task OnPausedAsync(AgentRun run, RunContext ctx, CancellationToken ct)
        {
            PausedCalled = true; // non-terminal pause hook — NOT EndRunAsync (guardrail 5)
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Executor whose <see cref="ExecuteStepAsync"/> models a real blocking step-turn: it cancels the
    /// SESSION-level source the orchestrator's run CTS is linked from (as <c>ChatSession.Cancel()</c>
    /// would), then honors the linked token by blocking on it. Without the R13 linkage the delay would
    /// never observe the cancel and the run would hang — so a green test proves the link propagates.
    /// </summary>
    private sealed class CancellingExecutor : IAgentTurnExecutor
    {
        private readonly CancellationTokenSource _sessionCts;
        public List<string> Executed { get; } = new();
        public bool EndCalled { get; private set; }
        public bool EndCancelled { get; private set; }

        public CancellingExecutor(CancellationTokenSource sessionCts) => _sessionCts = sessionCts;

        public Task BeginRunAsync(AgentRun run, RunContext ctx, CancellationToken ct) => Task.CompletedTask;

        public async Task<StepTurnResult> ExecuteStepAsync(AgentRun run, AgentStep step, RunContext ctx, CancellationToken ct)
        {
            Executed.Add(step.Intent ?? step.Title);
            _sessionCts.Cancel(); // ChatSession.Cancel() fires mid-step
            await Task.Delay(Timeout.Infinite, ct); // linked run CTS must cancel this in-flight step
            return Ok(); // unreachable
        }

        public Task<StepTurnResult> RunSingleTurnFallbackAsync(AgentRun run, RunContext ctx, CancellationToken ct)
            => Task.FromResult(Ok());

        public Task EndRunAsync(AgentRun run, RunContext ctx, bool cancelled, bool failed, CancellationToken ct)
        {
            EndCalled = true; EndCancelled = cancelled;
            return Task.CompletedTask;
        }

        public Task OnPausedAsync(AgentRun run, RunContext ctx, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Real run store with a POISONED <see cref="AddUsageAsync"/> — the run-level (plan/replan/verify)
    /// accrual seam. Everything else delegates, so the run really executes; only the bookkeeping write
    /// faults (guardrail 1: bookkeeping is never on the critical path).
    /// </summary>
    private sealed class ThrowingUsageRunService : IAgentRunService
    {
        private readonly IAgentRunService _inner;
        public ThrowingUsageRunService(IAgentRunService inner) => _inner = inner;

        public Task AddUsageAsync(Guid runId, Guid? stepId, UsageDetails usage, CancellationToken ct = default)
            => throw new InvalidOperationException("ledger boom");

        public Task<AgentRun> CreateAsync(AgentRunCreateRequest request, CancellationToken ct = default) => _inner.CreateAsync(request, ct);
        public Task SetStateAsync(Guid runId, AgentRunState state, CancellationToken ct = default) => _inner.SetStateAsync(runId, state, ct);
        public Task SetRunMessageRangeAsync(Guid runId, Guid firstMessageId, Guid lastMessageId, CancellationToken ct = default)
            => _inner.SetRunMessageRangeAsync(runId, firstMessageId, lastMessageId, ct);
        public Task CompleteAsync(Guid runId, bool truncated = false, string? truncationReason = null, CancellationToken ct = default)
            => _inner.CompleteAsync(runId, truncated, truncationReason, ct);
        public Task FailAsync(Guid runId, string? error, bool cancelled = false, CancellationToken ct = default) => _inner.FailAsync(runId, error, cancelled, ct);
        public Task PauseAsync(Guid runId, string? reason, CancellationToken ct = default) => _inner.PauseAsync(runId, reason, ct);
        public Task<bool> TryBeginResumeAsync(Guid runId, CancellationToken ct = default) => _inner.TryBeginResumeAsync(runId, ct);
        public Task<int> FailInterruptedRunsAsync(CancellationToken ct = default) => _inner.FailInterruptedRunsAsync(ct);
        public Task<AgentRun?> GetAsync(Guid runId, CancellationToken ct = default) => _inner.GetAsync(runId, ct);
        public Task<IReadOnlyList<AgentRun>> GetByChatAsync(Guid chatId, CancellationToken ct = default) => _inner.GetByChatAsync(chatId, ct);
        public Task<bool> ChatHasPlannedRunAsync(Guid chatId, CancellationToken ct = default) => _inner.ChatHasPlannedRunAsync(chatId, ct);
        public Task ReplaceStepsAsync(Guid runId, IReadOnlyList<AgentStep> steps, CancellationToken ct = default) => _inner.ReplaceStepsAsync(runId, steps, ct);
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

        public AgentRunOrchestrator BuildOrchestrator(IAgentPlanner planner, IAgentVerifier? verifier = null) =>
            new(Runs, planner, verifier ?? new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance);

        public void Dispose()
        {
            Runs.Dispose();
            Ctx.Dispose();
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Run_NStepPlan_ExecutesInOrder_Completed()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "ia"), ("B", "ib"), ("C", "ic")), false));
        var exec = new RecordingExecutor(_ => Ok());

        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "ia", "ib", "ic" }, exec.Executed);
        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.DoesNotContain("truncated", final.ExtraJson ?? string.Empty);
        Assert.All(final.Plan, s => Assert.Equal(AgentStepStatus.Done, s.Status));
        Assert.True(exec.BeginCalled);
        Assert.True(exec.EndCalled);
        Assert.False(exec.EndCancelled);
    }

    [Fact]
    public async Task Run_ReplanRequery_ExecutesRevised_SkipsDropped()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1"), ("B", "s2"), ("C", "s3")), false));
        planner.Replans.Enqueue(new PlanResult(MakeSteps(("B2", "s2prime")), false)); // drops s3, adds s2prime
        var exec = new RecordingExecutor(step => step.Intent == "s2" ? Fail("boom") : Ok());

        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.Contains("s2prime", exec.Executed); // revised step ran (re-query, R2)
        Assert.DoesNotContain("s3", exec.Executed); // dropped step never ran
        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.Contains(final.Plan, s => s.Title == "A" && s.Status == AgentStepStatus.Done); // Done step preserved
    }

    [Fact]
    public async Task Run_ReplanBoundExceeded_Failed_DoneStepsPreserved()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var profile = new RunProfile(24, 2, TimeSpan.FromMinutes(20));
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1"), ("B", "s2fail")), false));
        planner.Replans.Enqueue(new PlanResult(MakeSteps(("B", "s2fail")), false));
        planner.Replans.Enqueue(new PlanResult(MakeSteps(("B", "s2fail")), false));
        var exec = new RecordingExecutor(step => step.Intent == "s2fail" ? Fail("boom") : Ok());

        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), profile, TestContext.Current.CancellationToken);

        Assert.Equal(2, planner.ReplanCalls); // bounded by MaxReplans
        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Failed, final!.State);
        Assert.Contains(final.Plan, s => s.Title == "A" && s.Status == AgentStepStatus.Done);
    }

    [Fact]
    public async Task Run_ReplanItselfDegradesToFallback_Fails_NoSingleTurn_DoneStepsPreserved()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1"), ("B", "s2fail")), false));
        // Replans queue left empty → ReplanAsync returns PlanResult.Fallback (the replan itself degrades).
        var exec = new RecordingExecutor(step => step.Intent == "s2fail" ? Fail("orig-error") : Ok());

        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.Equal(1, planner.ReplanCalls);
        // R10 replan-degrade: fails with the original step error — it does NOT run a single-turn fallback
        // (that fallback is only for the INITIAL plan degrade, not a mid-run replan degrade).
        Assert.False(exec.FallbackCalled);
        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Failed, final!.State);
        Assert.Contains(final.Plan, s => s.Title == "A" && s.Status == AgentStepStatus.Done); // Done step preserved
        Assert.True(exec.EndCalled);
        Assert.True(exec.EndFailed);        // EndRunAsync told the run failed (§13.5.2 / D-fix)
        Assert.False(exec.EndCancelled);
    }

    [Fact]
    public async Task Run_BudgetExhausted_PausesIntoWaitingForInput_NotCompletedTruncated()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var profile = new RunProfile(MaxSteps: 2, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20));
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1"), ("B", "s2"), ("C", "s3")), false));
        var exec = new RecordingExecutor(_ => Ok());

        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), profile, TestContext.Current.CancellationToken);

        Assert.Equal(2, exec.Executed.Count); // dispatched at most MaxSteps
        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        // Budget now PARKS the run (WaitingForInput), not Completed+truncated.
        Assert.Equal(AgentRunState.WaitingForInput, final!.State);
        Assert.Contains("paused", final.ExtraJson ?? string.Empty);
        Assert.Contains("step-cap", final.ExtraJson ?? string.Empty);
        Assert.DoesNotContain("truncated", final.ExtraJson ?? string.Empty);
        Assert.Null(final.CompletedAt); // not terminal
        // Guardrail 5: a pause must NOT raise a terminal EndRun (no ChatState.Completed / TurnCompleted),
        // but MUST call the non-terminal OnPaused release hook so a Live session is unwedged (Idle).
        Assert.False(exec.EndCalled);
        Assert.True(exec.PausedCalled);
    }

    [Fact]
    public async Task Run_Resume_ReDrainsRemainingSteps_ToCompleted()
    {
        using var h = new Harness();
        var ct = TestContext.Current.CancellationToken;
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1"), ("B", "s2"), ("C", "s3")), false));

        // First run: budget = 2 steps → s1, s2 Done, then pause before s3.
        var profile = new RunProfile(MaxSteps: 2, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20));
        var exec1 = new RecordingExecutor(_ => Ok());
        await h.BuildOrchestrator(planner).RunAsync(run, exec1, Persona(), Provider(), profile, ct);
        Assert.Equal(new[] { "s1", "s2" }, exec1.Executed);
        Assert.Equal(AgentRunState.WaitingForInput, (await h.Runs.GetAsync(run.Id, ct))!.State);

        // Resume: CAS-claim, then re-invoke on the EXISTING run with resume:true + a fresh budget. The
        // persisted Pending remainder (s3) drains; the Done steps (s1, s2) are NOT re-executed.
        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, ct));
        var fresh = new RunProfile(MaxSteps: 24, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20));
        var exec2 = new RecordingExecutor(_ => Ok());
        await h.BuildOrchestrator(planner).RunAsync(run, exec2, Persona(), Provider(), fresh, ct, resume: true);

        Assert.Equal(new[] { "s3" }, exec2.Executed); // only the remainder ran (no re-plan, no re-run)
        var final = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.All(final.Plan, s => Assert.Equal(AgentStepStatus.Done, s.Status));
    }

    [Fact]
    public async Task Run_Resume_PreservesLedgerAcrossPause()
    {
        using var h = new Harness();
        var ct = TestContext.Current.CancellationToken;
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1"), ("B", "s2"), ("C", "s3")), false));

        var profile = new RunProfile(MaxSteps: 2, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20));
        var exec1 = new RecordingExecutor(_ => OkUsage(10, 5)); // 2 steps → 20/10 accrued, then pause
        await h.BuildOrchestrator(planner).RunAsync(run, exec1, Persona(), Provider(), profile, ct);

        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, ct));
        var fresh = new RunProfile(MaxSteps: 24, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20));
        var exec2 = new RecordingExecutor(_ => OkUsage(10, 5)); // 1 more step → +10/5 (ledger is persisted, NOT reset)
        await h.BuildOrchestrator(planner).RunAsync(run, exec2, Persona(), Provider(), fresh, ct, resume: true);

        var final = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Completed, final!.State);
        using var doc = JsonDocument.Parse(final.LedgerJson!);
        var root = doc.RootElement;
        Assert.Equal(30, root.GetProperty("inputTokens").GetInt64());  // 20 pre-pause + 10 resume
        Assert.Equal(15, root.GetProperty("outputTokens").GetInt64()); // 10 pre-pause + 5 resume
    }

    [Fact]
    public async Task Run_Resume_PreservesRunMessageRange_AcrossPause()
    {
        using var h = new Harness();
        var ct = TestContext.Current.CancellationToken;
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1"), ("B", "s2"), ("C", "s3")), false));

        // First run: 2 steps produce a transcript slice, then pause. The pre-pause first message is pinned.
        var profile = new RunProfile(MaxSteps: 2, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20));
        await h.BuildOrchestrator(planner).RunAsync(run, new RecordingExecutor(_ => Ok()), Persona(), Provider(), profile, ct);
        var parked = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.WaitingForInput, parked!.State);
        var pinnedFirst = parked.FirstMessageId;
        Assert.NotNull(pinnedFirst); // pre-pause slice pinned

        // Resume drains s3 (producing its OWN message ids). The terminal PinRange must EXTEND the range, not
        // overwrite FirstMessageId with the resume-only first id — the orchestrator seeds runFirst from the
        // (freshly-fetched) run on resume (R3). Pass the fetched run, exactly as HeadlessRunLauncher does.
        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, ct));
        var resumeRun = await h.Runs.GetAsync(run.Id, ct);
        var fresh = new RunProfile(MaxSteps: 24, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20));
        await h.BuildOrchestrator(planner).RunAsync(resumeRun!, new RecordingExecutor(_ => Ok()), Persona(), Provider(), fresh, ct, resume: true);

        var final = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.Equal(pinnedFirst, final.FirstMessageId); // first message UNCHANGED across pause → resume → Completed
    }

    [Fact]
    public async Task Run_CancelDuringResume_SettlesCancelled_SlicePinned()
    {
        using var h = new Harness();
        var ct = TestContext.Current.CancellationToken;
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1"), ("B", "s2"), ("C", "s3")), false));

        // First run pauses after 2 steps; the executed slice is pinned by PauseAsync's PinRange.
        var profile = new RunProfile(MaxSteps: 2, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20));
        var exec1 = new RecordingExecutor(_ => Ok()); // Ok() carries non-empty message ids → a real slice
        await h.BuildOrchestrator(planner).RunAsync(run, exec1, Persona(), Provider(), profile, ct);
        var parked = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.WaitingForInput, parked!.State);
        Assert.NotNull(parked.FirstMessageId); // slice pinned at pause

        // Resume, but a cancel lands on the remaining step. The run settles Cancelled and the pre-pause
        // slice stays pinned (no double-run, no null range).
        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, ct));
        using var sessionCts = new CancellationTokenSource();
        var exec2 = new CancellingExecutor(sessionCts);
        var fresh = new RunProfile(MaxSteps: 24, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20));
        await h.BuildOrchestrator(planner).RunAsync(run, exec2, Persona(), Provider(), fresh, sessionCts.Token, resume: true);

        var final = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Cancelled, final!.State);
        Assert.True(exec2.EndCancelled);
        Assert.NotNull(final.FirstMessageId); // slice still pinned
        Assert.NotEqual(Guid.Empty, final.FirstMessageId!.Value);
    }

    [Fact]
    public async Task Run_StepCancelled_FailsCancelled_NoFurtherSteps()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1"), ("B", "s2"), ("C", "s3")), false));
        var exec = new RecordingExecutor(step => step.Intent == "s2" ? Cancel() : Ok());

        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "s1", "s2" }, exec.Executed); // s3 never dispatched
        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Cancelled, final!.State);
        Assert.True(exec.EndCalled);
        Assert.True(exec.EndCancelled);
    }

    [Fact]
    public async Task Run_PlannerFallback_RunsSingleTurn_Completed()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(PlanResult.Fallback); // R10 degrade
        var exec = new RecordingExecutor(_ => Ok());

        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.True(exec.FallbackCalled);
        Assert.Empty(exec.Executed); // no step recorded — not a degenerate 1-step Planned run
        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.Empty(final.Plan);
    }

    [Fact]
    public async Task Run_WallClockExhausted_PausesIntoWaitingForInput_WallClockReason()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        // A zero wall-clock budget trips WallClockExceeded on the very first loop iteration, before
        // any step is dispatched — the OTHER §16 R5 branch from the step-cap test above.
        var profile = new RunProfile(MaxSteps: 24, MaxReplans: 2, WallClock: TimeSpan.Zero);
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1"), ("B", "s2"), ("C", "s3")), false));
        var exec = new RecordingExecutor(_ => Ok());

        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), profile, TestContext.Current.CancellationToken);

        Assert.Empty(exec.Executed); // wall-clock exhausted before dispatching any step
        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.WaitingForInput, final!.State); // parked, never a silent clean Completed
        Assert.Contains("paused", final.ExtraJson ?? string.Empty);
        Assert.Contains("wall-clock", final.ExtraJson ?? string.Empty);
        Assert.Null(final.CompletedAt);
        Assert.False(exec.EndCalled); // guardrail 5: pause is not terminal
        Assert.True(exec.PausedCalled); // non-terminal release hook fired (Live session → Idle)
    }

    [Fact]
    public async Task Run_SessionCancelDuringStep_LinkedCts_CancelsInFlightStep_Cancelled()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1"), ("B", "s2")), false));

        // sessionCts stands in for ChatSession.Cts; RunAsync links its run CTS from this token (R13).
        using var sessionCts = new CancellationTokenSource();
        var exec = new CancellingExecutor(sessionCts);

        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, sessionCts.Token);

        Assert.Equal(new[] { "s1" }, exec.Executed); // cancel landed on the in-flight step; s2 never dispatched
        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Cancelled, final!.State);
        Assert.True(exec.EndCalled);
        Assert.True(exec.EndCancelled);
    }

    [Fact]
    public async Task Run_PerStepUsage_AccruesLedgerThroughLoop()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1"), ("B", "s2")), false));
        var exec = new RecordingExecutor(_ => OkUsage(10, 5)); // each step carries usage (R16 ledger)

        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.NotNull(final.LedgerJson);

        using var doc = JsonDocument.Parse(final.LedgerJson!);
        var root = doc.RootElement;
        Assert.Equal(20, root.GetProperty("inputTokens").GetInt64());  // 2 × 10
        Assert.Equal(10, root.GetProperty("outputTokens").GetInt64()); // 2 × 5
        Assert.Equal(2, root.GetProperty("perStep").GetArrayLength());  // per-step ledger entries
    }

    // ---- Verify/critic pass (§13.x) ----

    [Fact]
    public async Task Run_VerifyPasses_Completed()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1")), false));
        var verifier = new FakeVerifier();
        verifier.Verdicts.Enqueue(new VerdictResult(true, "ok", Array.Empty<string>(), null));
        var exec = new RecordingExecutor(_ => Ok());

        await h.BuildOrchestrator(planner, verifier).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.DoesNotContain("truncated", final.ExtraJson ?? string.Empty);
        Assert.Equal(1, verifier.VerifyCalls);
    }

    [Fact]
    public async Task Run_VerifyFails_Replans_Redrains_Passes_Completed()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1")), false));
        planner.Replans.Enqueue(new PlanResult(MakeSteps(("B", "s2")), false));
        var verifier = new FakeVerifier();
        verifier.Verdicts.Enqueue(new VerdictResult(false, "not yet", new[] { "x" }, null)); // fail → replan
        verifier.Verdicts.Enqueue(VerdictResult.Accept);                                     // re-drain → pass
        var exec = new RecordingExecutor(_ => Ok());

        await h.BuildOrchestrator(planner, verifier).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.Contains("s1", exec.Executed);
        Assert.Contains("s2", exec.Executed);
        Assert.Equal(1, planner.ReplanCalls);
        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.DoesNotContain("truncated", final.ExtraJson ?? string.Empty);
    }

    [Fact]
    public async Task Run_VerifyFails_ReplansExhausted_CompletedTruncatedUnverified()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var profile = new RunProfile(24, 1, TimeSpan.FromMinutes(20)); // MaxReplans = 1
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1")), false));
        planner.Replans.Enqueue(new PlanResult(MakeSteps(("B", "s2")), false));
        var verifier = new FakeVerifier();
        verifier.Verdicts.Enqueue(new VerdictResult(false, "nope", new[] { "x" }, null)); // fail → replan (1)
        verifier.Verdicts.Enqueue(new VerdictResult(false, "still nope", new[] { "x" }, null)); // fail → exhausted
        var exec = new RecordingExecutor(_ => Ok());

        await h.BuildOrchestrator(planner, verifier).RunAsync(run, exec, Persona(), Provider(), profile, TestContext.Current.CancellationToken);

        Assert.Equal(1, planner.ReplanCalls);
        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State); // NOT Failed — steps genuinely ran
        Assert.Contains("truncated", final.ExtraJson ?? string.Empty);
        Assert.Contains("unverified", final.ExtraJson ?? string.Empty);
    }

    [Fact]
    public async Task Run_VerifyFails_ReplanDegradesToFallback_CompletedTruncatedUnverified()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1")), false));
        // Replans queue left empty → ReplanAsync returns PlanResult.Fallback (the replan itself degrades).
        var verifier = new FakeVerifier();
        verifier.Verdicts.Enqueue(new VerdictResult(false, "nope", new[] { "x" }, null));
        var exec = new RecordingExecutor(_ => Ok());

        await h.BuildOrchestrator(planner, verifier).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.Equal(1, planner.ReplanCalls);
        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State); // NOT Failed
        Assert.Contains("unverified", final.ExtraJson ?? string.Empty);
    }

    [Fact]
    public async Task Run_VerifierThrows_DegradesToAccept_Completed()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1")), false));
        var verifier = new FakeVerifier { ThrowOnVerify = true };
        var exec = new RecordingExecutor(_ => Ok());

        await h.BuildOrchestrator(planner, verifier).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.DoesNotContain("truncated", final.ExtraJson ?? string.Empty);
        Assert.False(exec.EndFailed);
    }

    [Fact]
    public async Task Run_VerifyUsage_AccruesToRunLedger()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1")), false));
        var verifier = new FakeVerifier();
        verifier.Verdicts.Enqueue(new VerdictResult(true, "ok", Array.Empty<string>(),
            new UsageDetails { InputTokenCount = 7, OutputTokenCount = 3 }));
        var exec = new RecordingExecutor(_ => Ok()); // null step usage → no per-step ledger entry

        await h.BuildOrchestrator(planner, verifier).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.NotNull(final.LedgerJson);

        using var doc = JsonDocument.Parse(final.LedgerJson!);
        var root = doc.RootElement;
        Assert.Equal(7, root.GetProperty("inputTokens").GetInt64());   // verify run-level accrual
        Assert.Equal(3, root.GetProperty("outputTokens").GetInt64());
        Assert.Equal(0, root.GetProperty("perStep").GetArrayLength()); // verify accrues run-level (stepId null)
    }

    // ---- I1: plan/replan spend reaches the run ledger (it used to be discarded in the planner) ----

    private static UsageDetails Usage(long input, long output) =>
        new() { InputTokenCount = input, OutputTokenCount = output };

    private static (long In, long Out, int PerStep) Ledger(AgentRun run)
    {
        using var doc = JsonDocument.Parse(run.LedgerJson!);
        var root = doc.RootElement;
        return (root.GetProperty("inputTokens").GetInt64(), root.GetProperty("outputTokens").GetInt64(),
            root.GetProperty("perStep").GetArrayLength());
    }

    [Fact]
    public async Task Run_PlannerUsage_AccruesToRunLedger()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1")), false, Usage(40, 12))); // the plan turn's rounds
        var exec = new RecordingExecutor(_ => Ok()); // null step usage → no per-step entry

        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        var (input, output, perStep) = Ledger(final);
        Assert.Equal(40, input);
        Assert.Equal(12, output);
        Assert.Equal(0, perStep); // planning is run-level spend (stepId: null), never a step entry
    }

    [Fact]
    public async Task Run_PlannerDegradeUsage_AccruesToRunLedger_OnTheSingleTurnPath()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        // R10 degrade: no usable plan, but both planning attempts (incl. the firm retry) were paid for.
        planner.Plans.Enqueue(PlanResult.Fallback with { Usage = Usage(80, 24) });
        var exec = new RecordingExecutor(_ => Ok());

        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.True(exec.FallbackCalled);
        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        var (input, output, _) = Ledger(final);
        Assert.Equal(80, input);  // the degrade path must not drop the planner's spend
        Assert.Equal(24, output);
    }

    [Fact]
    public async Task Run_StepFailureReplanUsage_AccruesToRunLedger()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1fail")), false, Usage(40, 12)));
        planner.Replans.Enqueue(new PlanResult(MakeSteps(("B", "s2")), false, Usage(30, 9)));
        var exec = new RecordingExecutor(step => step.Intent == "s1fail" ? Fail("boom") : Ok());

        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.Equal(1, planner.ReplanCalls);
        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        var (input, output, _) = Ledger(final);
        Assert.Equal(70, input);  // plan 40 + replan 30
        Assert.Equal(21, output); // plan 12 + replan 9
    }

    [Fact]
    public async Task Run_ReplanDegradeUsage_AccruesToRunLedger_EvenThoughTheRunFails()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1fail")), false, Usage(40, 12)));
        planner.Replans.Enqueue(PlanResult.Fallback with { Usage = Usage(30, 9) }); // replan degraded → run fails
        var exec = new RecordingExecutor(_ => Fail("boom"));

        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Failed, final!.State);
        var (input, output, _) = Ledger(final);
        Assert.Equal(70, input);  // a failed run still bills the planning it consumed
        Assert.Equal(21, output);
    }

    [Fact]
    public async Task Run_VerifyFailReplanUsage_AccruesToRunLedger_WithPlanAndVerify()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1")), false, Usage(40, 12)));
        planner.Replans.Enqueue(new PlanResult(MakeSteps(("B", "s2")), false, Usage(30, 9)));
        var verifier = new FakeVerifier();
        verifier.Verdicts.Enqueue(new VerdictResult(false, "not yet", new[] { "x" }, Usage(7, 3))); // fail → replan
        verifier.Verdicts.Enqueue(VerdictResult.Accept with { Usage = Usage(7, 3) });               // re-drain → pass
        var exec = new RecordingExecutor(_ => OkUsage(10, 5));

        await h.BuildOrchestrator(planner, verifier).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        var (input, output, perStep) = Ledger(final);
        Assert.Equal(40 + 30 + 7 + 7 + 10 + 10, input);  // plan + verify-fail replan + 2 verifies + 2 steps
        Assert.Equal(12 + 9 + 3 + 3 + 5 + 5, output);
        Assert.Equal(2, perStep); // only the two step turns own per-step entries
    }

    [Fact]
    public async Task Run_PlannerUsageBookkeepingFaults_DoesNotFailTheRun()
    {
        // Guardrail 1: the plan-usage accrual is bookkeeping. A ledger write fault must never turn an
        // otherwise-clean run into a failure — the run still completes.
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1")), false, Usage(40, 12)));
        var exec = new RecordingExecutor(_ => Ok());
        var orchestrator = new AgentRunOrchestrator(
            new ThrowingUsageRunService(h.Runs), planner, new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance);

        await orchestrator.RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.False(exec.EndFailed);
    }

    [Fact]
    public async Task Run_SessionCancelDuringVerify_PropagatesCancelled_RangePinned()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1")), false));

        // The step produces a transcript slice (Ok() carries non-empty message Ids); a user cancel then
        // lands DURING verify. Guardrail 1: SafeVerify rethrows a genuine run cancel (never degrade-to-
        // accept), so the run settles Cancelled — not a spurious Completed. R3: the executed-so-far slice
        // is still pinned even though the cancel surfaced after the clean drain.
        using var sessionCts = new CancellationTokenSource();
        var verifier = new FakeVerifier { CancelSessionOnVerify = sessionCts };
        var exec = new RecordingExecutor(_ => Ok());

        await h.BuildOrchestrator(planner, verifier).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, sessionCts.Token);

        Assert.Equal(1, verifier.VerifyCalls);
        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Cancelled, final!.State); // cancel during verify propagates — NOT accepted
        Assert.True(exec.EndCalled);
        Assert.True(exec.EndCancelled);
        Assert.NotNull(final.FirstMessageId);                 // R3: transcript slice pinned on cancel-during-verify
        Assert.NotEqual(Guid.Empty, final.FirstMessageId!.Value);
    }
}
