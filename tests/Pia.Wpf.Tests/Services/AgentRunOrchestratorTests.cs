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

        /// <summary>Snapshot of <c>ctx.CompletedSteps</c> per replan — what the replan judge got to see (E2).</summary>
        public List<IReadOnlyList<CompletedStepSummary>> SeenCompletedSteps { get; } = new();

        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(Plans.Count > 0 ? Plans.Dequeue() : PlanResult.Fallback);

        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
        {
            ReplanCalls++;
            SeenCompletedSteps.Add(ctx.CompletedSteps.ToList());
            return Task.FromResult(Replans.Count > 0 ? Replans.Dequeue() : PlanResult.Fallback);
        }
    }

    private sealed class RecordingExecutor : IAgentTurnExecutor
    {
        private readonly Func<AgentStep, StepTurnResult> _result;
        /// <summary>What the R10 single-turn fallback returns; null = a plain successful turn.</summary>
        public StepTurnResult? FallbackResult { get; set; }
        public List<string> Executed { get; } = new();
        public bool BeginCalled { get; private set; }
        public bool EndCalled { get; private set; }
        public bool EndCancelled { get; private set; }
        public bool EndFailed { get; private set; }
        public bool FallbackCalled { get; private set; }
        public bool PausedCalled { get; private set; }

        /// <summary>
        /// What this executor publishes onto <c>ctx.WorkspaceRoot</c> in <c>BeginRunAsync</c>, exactly as both
        /// real executors do (Batch 06 B3). Null (the default) is the no-isolation shape every pre-Batch-06
        /// fact in this file runs in, and it is what keeps them from promoting anything.
        /// </summary>
        public string? WorkspaceRoot { get; set; }

        public RecordingExecutor(Func<AgentStep, StepTurnResult> result) => _result = result;

        public Task BeginRunAsync(AgentRun run, RunContext ctx, CancellationToken ct)
        {
            BeginCalled = true;
            ctx.WorkspaceRoot = WorkspaceRoot;
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
            return Task.FromResult(FallbackResult ?? Ok("fallback"));
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
    /// Real run store with individually POISONABLE bookkeeping seams: the run-level usage accrual
    /// (plan/replan/verify) and the run read the resume context seed uses. Everything else delegates, so
    /// the run really executes — guardrail 1: bookkeeping is never on the critical path.
    /// </summary>
    private sealed class FaultyRunService : IAgentRunService
    {
        private readonly IAgentRunService _inner;
        public FaultyRunService(IAgentRunService inner) => _inner = inner;

        public bool FailAddUsage { get; set; }
        public bool FailGet { get; set; }

        /// <summary>Shared call log, appended with <c>"complete"</c> (Batch 06 B8's ordering fact).</summary>
        public List<string>? Order { get; set; }

        public Task AddUsageAsync(Guid runId, Guid? stepId, UsageDetails usage, CancellationToken ct = default)
            => FailAddUsage ? throw new InvalidOperationException("ledger boom") : _inner.AddUsageAsync(runId, stepId, usage, ct);

        public Task<AgentRun?> GetAsync(Guid runId, CancellationToken ct = default)
            => FailGet ? throw new InvalidOperationException("read boom") : _inner.GetAsync(runId, ct);

        public Task<AgentRun> CreateAsync(AgentRunCreateRequest request, CancellationToken ct = default) => _inner.CreateAsync(request, ct);
        public Task SetStateAsync(Guid runId, AgentRunState state, CancellationToken ct = default) => _inner.SetStateAsync(runId, state, ct);
        public Task SetRunMessageRangeAsync(Guid runId, Guid firstMessageId, Guid lastMessageId, CancellationToken ct = default)
            => _inner.SetRunMessageRangeAsync(runId, firstMessageId, lastMessageId, ct);
        public Task CompleteAsync(Guid runId, bool truncated = false, string? truncationReason = null, CancellationToken ct = default)
        {
            Order?.Add("complete");
            return _inner.CompleteAsync(runId, truncated, truncationReason, ct);
        }
        public Task FailAsync(Guid runId, string? error, bool cancelled = false, CancellationToken ct = default) => _inner.FailAsync(runId, error, cancelled, ct);
        public Task PauseAsync(Guid runId, string? reason, CancellationToken ct = default) => _inner.PauseAsync(runId, reason, ct);
        public Task<bool> TryBeginResumeAsync(Guid runId, CancellationToken ct = default) => _inner.TryBeginResumeAsync(runId, ct);
        public Task BeginChildWaitAsync(Guid runId, int childCount, CancellationToken ct = default) => _inner.BeginChildWaitAsync(runId, childCount, ct);
        public Task<bool> TryEndChildWaitAsync(Guid runId, CancellationToken ct = default) => _inner.TryEndChildWaitAsync(runId, ct);
        public Task<int> FailInterruptedRunsAsync(CancellationToken ct = default) => _inner.FailInterruptedRunsAsync(ct);
        public Task<IReadOnlyList<AgentRun>> GetByChatAsync(Guid chatId, CancellationToken ct = default) => _inner.GetByChatAsync(chatId, ct);
        public Task<IReadOnlyList<AgentRun>> GetChildRunsAsync(Guid parentRunId, CancellationToken ct = default) => _inner.GetChildRunsAsync(parentRunId, ct);
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

        /// <summary>A real directory an isolated run's workspace root can point at (Batch 06 G4). It only has
        /// to EXIST and be non-null — promotion itself is a fake here; what is under test is the loop's
        /// ordering, not the copy.</summary>
        public string RunsBase { get; }

        public Harness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            RunsBase = Path.Combine(_dir, "runs");
            Directory.CreateDirectory(RunsBase);
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

        public AgentRunOrchestrator BuildOrchestrator(
            IAgentPlanner planner, IAgentVerifier? verifier = null,
            // Batch 06 G4, trailing and defaulted like the ctor param it feeds: omitted ⇒ no promotion, i.e.
            // the pre-Batch-06 loop, which is the shape every existing fact in this file asserts.
            IRunWorkspaceService? workspaces = null, IAgentRunService? runService = null) =>
            new(runService ?? Runs, planner, verifier ?? new FakeVerifier(),
                NullLogger<AgentRunOrchestrator>.Instance, workspaces);

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
    public async Task Run_SingleTurnFallbackUsage_AccruesToRunLedger()
    {
        // The fallback turn owns no step row, so nothing else would ever bill it: without a run-level
        // accrual the entire R10 degrade path reports zero tokens.
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(PlanResult.Fallback with { Usage = Usage(80, 24) });
        var exec = new RecordingExecutor(_ => Ok())
        {
            FallbackResult = new StepTurnResult(true, false, null, "fallback", Usage(15, 6), Guid.NewGuid(), Guid.NewGuid()),
        };

        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        var (input, output, perStep) = Ledger(final);
        Assert.Equal(95, input);  // planner degrade 80 + fallback turn 15
        Assert.Equal(30, output); // 24 + 6
        Assert.Equal(0, perStep); // no step row exists for the fallback turn
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
            new FaultyRunService(h.Runs) { FailAddUsage = true }, planner, new FakeVerifier(),
            NullLogger<AgentRunOrchestrator>.Instance);

        await orchestrator.RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.False(exec.EndFailed);
    }

    // ---- E2: a resumed run's critic/replan must see the PRE-PAUSE work, not only the post-resume slice ----

    private static List<AgentStep> MakeStepsWithArtifacts(params (string Title, string Intent, string? Artifact)[] steps)
    {
        var result = new List<AgentStep>();
        for (var i = 0; i < steps.Length; i++)
        {
            result.Add(new AgentStep
            {
                Id = Guid.Empty, Ordinal = i, Title = steps[i].Title, Intent = steps[i].Intent,
                ExpectedArtifact = steps[i].Artifact, Status = AgentStepStatus.Pending,
            });
        }
        return result;
    }

    [Fact]
    public async Task Run_Resume_VerifierSeesPrePauseSteps_AndTheSeedDoesNotSpendTheFreshBudget()
    {
        using var h = new Harness();
        var ct = TestContext.Current.CancellationToken;
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeStepsWithArtifacts(
            ("A", "s1", "one.md"), ("B", "s2", "two.md"), ("C", "s3", "three.md")), false));

        // First run: budget = 2 steps → s1, s2 Done, then park before s3 (verify never runs at a pause).
        var profile = new RunProfile(MaxSteps: 2, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20));
        var firstVerifier = new FakeVerifier();
        await h.BuildOrchestrator(planner, firstVerifier).RunAsync(run, new RecordingExecutor(_ => Ok()), Persona(), Provider(), profile, ct);
        Assert.Equal(AgentRunState.WaitingForInput, (await h.Runs.GetAsync(run.Id, ct))!.State);
        Assert.Empty(firstVerifier.SeenCompletedSteps);

        // Resume with the SAME 2-step budget: the seeded pre-pause steps must not be billed against it,
        // otherwise the run would re-park instantly instead of draining s3.
        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, ct));
        var verifier = new FakeVerifier();
        var exec2 = new RecordingExecutor(_ => Ok("post-resume text"));
        await h.BuildOrchestrator(planner, verifier).RunAsync(run, exec2, Persona(), Provider(), profile, ct, resume: true);

        Assert.Equal(new[] { "s3" }, exec2.Executed);
        Assert.Equal(AgentRunState.Completed, (await h.Runs.GetAsync(run.Id, ct))!.State);

        // The critic judged all three steps: the two seeded ones (marked as an earlier segment, carrying
        // their declared artifacts, with no recoverable result text) plus the post-resume one.
        var seen = Assert.Single(verifier.SeenCompletedSteps);
        Assert.Equal(new[] { 0, 1, 2 }, seen.Select(s => s.Ordinal).ToArray());
        Assert.Equal(new[] { "A", "B", "C" }, seen.Select(s => s.Title).ToArray());
        Assert.Equal(new[] { "one.md", "two.md", "three.md" }, seen.Select(s => s.ExpectedArtifact).ToArray());
        Assert.Equal(new[] { true, true, false }, seen.Select(s => s.FromEarlierSegment).ToArray());
        Assert.All(seen.Take(2), s => Assert.True(s.Succeeded));
        Assert.All(seen.Take(2), s => Assert.Equal(string.Empty, s.VisibleText)); // not recoverable (yet)
        Assert.Equal("post-resume text", seen[2].VisibleText);
    }

    [Fact]
    public async Task Run_Resume_ReplanAfterPause_SeesPrePauseStepsToo()
    {
        using var h = new Harness();
        var ct = TestContext.Current.CancellationToken;
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1"), ("B", "s2"), ("C", "s3fail")), false));
        planner.Replans.Enqueue(new PlanResult(MakeSteps(("D", "s4")), false));

        var profile = new RunProfile(MaxSteps: 2, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20));
        await h.BuildOrchestrator(planner).RunAsync(run, new RecordingExecutor(_ => Ok()), Persona(), Provider(), profile, ct);
        Assert.Equal(AgentRunState.WaitingForInput, (await h.Runs.GetAsync(run.Id, ct))!.State);

        // Resume: s3 fails → replan. The replan judge must be told about s1/s2 (which it cannot see in
        // this process any more) so it does not re-plan work that already happened.
        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, ct));
        var fresh = new RunProfile(MaxSteps: 24, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20));
        var exec = new RecordingExecutor(step => step.Intent == "s3fail" ? Fail("boom") : Ok());
        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), fresh, ct, resume: true);

        var seen = Assert.Single(planner.SeenCompletedSteps);
        Assert.Equal(new[] { "A", "B", "C" }, seen.Select(s => s.Title).ToArray());
        Assert.Equal(new[] { true, true, false }, seen.Select(s => s.FromEarlierSegment).ToArray());
    }

    [Fact]
    public async Task Run_Resume_SeedReadFaults_StillDrainsAndCompletes()
    {
        // Guardrail 1: the seed is bookkeeping. A failing read degrades to the old partial picture — it
        // must never fail the resume.
        using var h = new Harness();
        var ct = TestContext.Current.CancellationToken;
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1"), ("B", "s2"), ("C", "s3")), false));

        var profile = new RunProfile(MaxSteps: 2, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20));
        await h.BuildOrchestrator(planner).RunAsync(run, new RecordingExecutor(_ => Ok()), Persona(), Provider(), profile, ct);
        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, ct));

        var verifier = new FakeVerifier();
        var exec2 = new RecordingExecutor(_ => Ok());
        var fresh = new RunProfile(MaxSteps: 24, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20));
        var orchestrator = new AgentRunOrchestrator(
            new FaultyRunService(h.Runs) { FailGet = true }, planner, verifier, NullLogger<AgentRunOrchestrator>.Instance);

        await orchestrator.RunAsync(run, exec2, Persona(), Provider(), fresh, ct, resume: true);

        Assert.Equal(new[] { "s3" }, exec2.Executed);
        Assert.Equal(AgentRunState.Completed, (await h.Runs.GetAsync(run.Id, ct))!.State);
        var seen = Assert.Single(verifier.SeenCompletedSteps);
        Assert.Single(seen); // degraded to the post-resume slice only — but the run still finished cleanly
    }

    [Fact]
    public async Task Run_FreshRun_SeedsNothing_NoEarlierSegmentMarkers()
    {
        // A non-resume run must be untouched by E2 (and must not pay for the extra read).
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1")), false));
        var verifier = new FakeVerifier();

        await h.BuildOrchestrator(planner, verifier).RunAsync(run, new RecordingExecutor(_ => Ok()), Persona(), Provider(),
            RunProfile.Interactive, TestContext.Current.CancellationToken);

        var seen = Assert.Single(verifier.SeenCompletedSteps);
        Assert.All(seen, s => Assert.False(s.FromEarlierSegment));
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

    // ---- Batch 06 G4: promotion on the terminal path ----

    /// <summary>
    /// T-G4-8, <b>REGRESSION</b>. Batch 06 B8's ordering, asserted as an ORDER and not as three independent
    /// "it happened" facts: verify runs against the run root, THEN the workspace is promoted, THEN the run is
    /// marked Completed. Promoting before CompleteAsync is what dissolves the "Completed but its deliverables
    /// are still only in a workspace the sweep may delete" window without needing a promotion-aware sweep.
    /// <para>
    /// The orchestrator is built WITH the service on purpose: its ctor param is trailing-optional, so no
    /// existing fact in this file supplies one and there is no inherited coverage to lean on.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CleanRun_Promotes_AfterVerify_AndBeforeCompleteAsync()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var order = new List<string>();
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1")), false));
        var verifier = new FakeVerifier { Order = order };
        var runs = new FaultyRunService(h.Runs) { Order = order };
        var workspaces = new FakeRunWorkspaceService(h.RunsBase)
        {
            Order = order,
            PromoteResult = new RunPromotionResult(RunWorkspaceMode.Copy, Promoted: 1, Skipped: 0, Conflicts: 0, BranchName: null),
        };
        var exec = new RecordingExecutor(_ => Ok()) { WorkspaceRoot = h.RunsBase };

        await h.BuildOrchestrator(planner, verifier, workspaces, runs)
            .RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "verify", "promote", "teardown", "complete" }, order);
        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
    }

    /// <summary>
    /// T-G4-9, <b>REGRESSION</b>. The R10 single-turn fallback arm is a SECOND terminal path: it returns early
    /// and never reaches the terminal-settle block, and it settles Complete BEFORE EndRun — the opposite order
    /// to the main path. Omitting promotion there is this group's most likely silent hole, and it is the
    /// well-trodden path: every launcher-harness test plans to <c>PlanResult.Fallback</c>.
    /// <para>
    /// Its discrimination property was measured, not assumed: with promotion present on the MAIN arm only,
    /// this fact fails while <see cref="CleanRun_Promotes_AfterVerify_AndBeforeCompleteAsync"/> stays green.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheSingleTurnFallbackArm_AlsoPromotes()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var order = new List<string>();
        var planner = new FakePlanner(); // empty queue then PlanResult.Fallback then the R10 degrade arm
        var runs = new FaultyRunService(h.Runs) { Order = order };
        var workspaces = new FakeRunWorkspaceService(h.RunsBase)
        {
            Order = order,
            PromoteResult = new RunPromotionResult(RunWorkspaceMode.Copy, Promoted: 2, Skipped: 0, Conflicts: 0, BranchName: null),
        };
        var exec = new RecordingExecutor(_ => Ok()) { WorkspaceRoot = h.RunsBase };

        await h.BuildOrchestrator(planner, verifier: null, workspaces, runs)
            .RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.True(exec.FallbackCalled); // non-vacuity: this really is the degrade arm
        Assert.Equal(new[] { "promote", "teardown", "complete" }, order); // no verify on this arm at all
    }

    /// <summary>
    /// <b>REGRESSION</b> (Phase 3 fix pass, Batch 06 Lens A finding 5 / Lens B finding 3). A promotion that
    /// could not move everything reports <c>RetainWorkspace</c>, and the terminal path must OBEY it: the
    /// workspace holds the only copy of what was left behind — a copy-mode conflict whose resolution kept the
    /// user's newer file, or a worktree the run-branch commit could not fully take. Tearing it down there is
    /// silent, irreversible loss on a run that reports success.
    /// <para>
    /// The non-vacuity control is <see cref="CleanRun_Promotes_AfterVerify_AndBeforeCompleteAsync"/> above: the
    /// identical arm with <c>RetainWorkspace</c> unset DOES tear down, so this is about the flag and not about
    /// a teardown that never happens. Neutralization: drop the <c>RetainWorkspace</c> early return from
    /// <c>SafePromote</c> → red.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ACleanRunWhosePromotionLeftWorkBehind_KeepsItsWorkspace()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var order = new List<string>();
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1")), false));
        var runs = new FaultyRunService(h.Runs) { Order = order };
        var workspaces = new FakeRunWorkspaceService(h.RunsBase)
        {
            Order = order,
            PromoteResult = new RunPromotionResult(
                RunWorkspaceMode.Copy, Promoted: 0, Skipped: 0, Conflicts: 1, BranchName: null, RetainWorkspace: true),
        };
        var exec = new RecordingExecutor(_ => Ok()) { WorkspaceRoot = h.RunsBase };

        await h.BuildOrchestrator(planner, verifier: null, workspaces, runs)
            .RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "promote", "complete" }, order); // promoted, completed — and NOT torn down
        Assert.Empty(workspaces.TornDown);
        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State); // non-vacuity: this is the CLEAN terminal path
    }

    /// <summary>
    /// T-G4-10, <b>REGRESSION</b>. Plan D3's "Completed auto, ELSE OFFER": a cancelled or failed run is never
    /// promoted automatically and its workspace is never torn down, so the panel still has something to offer.
    /// </summary>
    [Theory]
    [InlineData("cancel")]
    [InlineData("step-fail")]
    [InlineData("fallback-fail")]
    public async Task ACancelledOrFailedRun_DoesNotPromote_AndKeepsItsWorkspace(string how)
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        if (how != "fallback-fail")
            planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1")), false));

        var workspaces = new FakeRunWorkspaceService(h.RunsBase)
        {
            PromoteResult = new RunPromotionResult(RunWorkspaceMode.Copy, 1, 0, 0, null),
        };
        var exec = new RecordingExecutor(_ => how == "cancel" ? Cancel() : Fail("boom"))
        {
            WorkspaceRoot = h.RunsBase,
            FallbackResult = how == "fallback-fail" ? Fail("planner degraded and the turn failed") : null,
        };
        // A step failure would otherwise burn a replan and end unverified-Completed; zero replans makes the
        // failure terminal on the step-fail row.
        var profile = new RunProfile(MaxSteps: 8, MaxReplans: 0, WallClock: TimeSpan.FromMinutes(5));

        await h.BuildOrchestrator(planner, verifier: null, workspaces)
            .RunAsync(run, exec, Persona(), Provider(), profile, TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.True(final!.State is AgentRunState.Failed or AgentRunState.Cancelled); // non-vacuity
        Assert.Empty(workspaces.Promoted);
        Assert.Empty(workspaces.TornDown);
    }

    /// <summary>
    /// T-G4-11, <b>GUARD</b>. Failure-isolated bookkeeping: a promotion that throws does not fail an
    /// otherwise-successful run. The files stay in the workspace and the publish affordance offers them.
    /// </summary>
    [Fact]
    public async Task APromotionFault_DoesNotFailTheRun()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1")), false));
        var workspaces = new FakeRunWorkspaceService(h.RunsBase) { ThrowOnPromote = true };
        var exec = new RecordingExecutor(_ => Ok()) { WorkspaceRoot = h.RunsBase };

        await h.BuildOrchestrator(planner, verifier: null, workspaces)
            .RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.Single(workspaces.Promoted);   // it really was attempted
        Assert.Empty(workspaces.TornDown);    // and the workspace kept, so the work is not lost
    }

    /// <summary>
    /// T-G4-12, <b>GUARD</b>. The pin that the trailing-optional dependency changed nothing: with no workspace
    /// service — and separately, with a service but no workspace root, which is the no-isolation degrade — the
    /// loop settles exactly as it did before Batch 06 and nothing is promoted.
    /// </summary>
    [Fact]
    public async Task WithNoWorkspaceService_TheLoopIsByteIdenticalToToday()
    {
        using var h = new Harness();
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1")), false));
        var noService = await h.NewRunAsync("goal");
        var exec = new RecordingExecutor(_ => Ok()) { WorkspaceRoot = h.RunsBase };

        await h.BuildOrchestrator(planner).RunAsync(
            noService, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        var settled = await h.Runs.GetAsync(noService.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, settled!.State);
        Assert.All(settled.Plan, s => Assert.Equal(AgentStepStatus.Done, s.Status));

        // Second half: a service IS injected, but the run has no workspace root (provisioning degraded to no
        // isolation). Promotion must not be attempted against a root that does not exist.
        var noRoot = await h.NewRunAsync("goal");
        var planner2 = new FakePlanner();
        planner2.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1")), false));
        var workspaces = new FakeRunWorkspaceService(h.RunsBase);
        var unisolated = new RecordingExecutor(_ => Ok()); // WorkspaceRoot stays null

        await h.BuildOrchestrator(planner2, verifier: null, workspaces).RunAsync(
            noRoot, unisolated, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunState.Completed,
            (await h.Runs.GetAsync(noRoot.Id, TestContext.Current.CancellationToken))!.State);
        Assert.Empty(workspaces.Promoted);
    }
}
