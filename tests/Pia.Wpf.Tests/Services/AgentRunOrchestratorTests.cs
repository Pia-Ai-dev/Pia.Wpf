using System.IO;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

// Fake planner/executor over a real SQLite AgentRunService, so the persisted step store is the real one.
public sealed class AgentRunOrchestratorTests
{
    private static Persona Persona() => new() { Name = "Pia", SystemPrompt = "sys" };
    private static AiProvider Provider() => new() { Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };

    private static StepTurnResult Ok(string text = "done") => new(true, false, null, text, null, Guid.NewGuid(), Guid.NewGuid());
    private static StepTurnResult OkUsage(long input, long output) =>
        new(true, false, null, "done", new UsageDetails { InputTokenCount = input, OutputTokenCount = output }, Guid.NewGuid(), Guid.NewGuid());
    private static StepTurnResult OkWithArtifact(string text, string artifact) =>
        new(true, false, null, text, null, Guid.NewGuid(), Guid.NewGuid(),
            Outcome: new StepOutcomeClaim(true, "declared", artifact));
    private static StepTurnResult OkDeclaringNoArtifact(string text = "done") =>
        new(true, false, null, text, null, Guid.NewGuid(), Guid.NewGuid(),
            Outcome: new StepOutcomeClaim(true, "declared", null));
    private static StepTurnResult Fail(string err) => new(false, false, err, string.Empty, null, Guid.NewGuid(), Guid.NewGuid());
    private static StepTurnResult Cancel() => new(false, true, "cancelled", string.Empty, null, Guid.NewGuid(), Guid.NewGuid());

    // Empty ids because a step that asked never finished, exactly as the real executors return it.
    private static StepTurnResult Ask(string question) =>
        new(false, false, null, string.Empty, null, Guid.Empty, Guid.Empty, UserInputQuestion: question);

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
        public StepTurnResult? FallbackResult { get; set; }
        public List<string> Executed { get; } = new();
        public bool BeginCalled { get; private set; }
        public bool EndCalled { get; private set; }
        public bool EndCancelled { get; private set; }
        public bool EndFailed { get; private set; }
        public bool FallbackCalled { get; private set; }
        public bool PausedCalled { get; private set; }

        // Published onto ctx in BeginRunAsync like the real executors; null means no isolation, so no promotion.
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
            PausedCalled = true;
            return Task.CompletedTask;
        }
    }

    // Cancels the session-level source and then blocks on the linked token: without the linkage the run hangs.
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
            _sessionCts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
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

    // Real store with two poisonable seams — the usage accrual and the read the resume seed uses; the rest delegates.
    private sealed class FaultyRunService : IAgentRunService
    {
        private readonly IAgentRunService _inner;
        public FaultyRunService(IAgentRunService inner) => _inner = inner;

        public bool FailAddUsage { get; set; }
        public bool FailGet { get; set; }

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
        public Task FailAsync(
            Guid runId, string? error, bool cancelled = false, CancellationToken ct = default,
            PiaFailure? failure = null) => _inner.FailAsync(runId, error, cancelled, ct, failure);
        public Task PauseAsync(Guid runId, string? reason, CancellationToken ct = default, string? approvalTool = null,
            string? approvalArgs = null) => _inner.PauseAsync(runId, reason, ct, approvalTool, approvalArgs);
        public Task UpdatePolicyJsonAsync(Guid runId, string? policyJson, CancellationToken ct = default) => _inner.UpdatePolicyJsonAsync(runId, policyJson, ct);
        public Task<IReadOnlyList<string>> AppendClarificationAsync(Guid runId, string? answer, CancellationToken ct = default) => _inner.AppendClarificationAsync(runId, answer, ct);
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
        public Task<IReadOnlyList<ScheduledFiringOutcome>> GetFiringsForTriggerAsync(Guid triggerRef, int limit, CancellationToken ct = default) => _inner.GetFiringsForTriggerAsync(triggerRef, limit, ct);
        public Task ReplaceStepsAsync(Guid runId, IReadOnlyList<AgentStep> steps, CancellationToken ct = default) => _inner.ReplaceStepsAsync(runId, steps, ct);
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

    private sealed class Harness : IDisposable
    {
        public readonly SqliteContext Ctx;
        public readonly AgentRunService Runs;
        public readonly AssistantChatService Chats;
        private readonly string _dir;

        // Only has to exist and be non-null: promotion is faked here, so nothing is really copied.
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
            IRunWorkspaceService? workspaces = null, IAgentRunService? runService = null,
            ILocalizationService? localization = null) =>
            new(runService ?? Runs, planner, verifier ?? new FakeVerifier(),
                NullLogger<AgentRunOrchestrator>.Instance, workspaces, chats: Chats, localization: localization);

        public void Dispose()
        {
            Chats.Dispose();
            Runs.Dispose();
            Ctx.Dispose();
            TempPath.Remove(_dir);
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
        planner.Replans.Enqueue(new PlanResult(MakeSteps(("B2", "s2prime")), false));
        var exec = new RecordingExecutor(step => step.Intent == "s2" ? Fail("boom") : Ok());

        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.Contains("s2prime", exec.Executed);
        Assert.DoesNotContain("s3", exec.Executed);
        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.Contains(final.Plan, s => s.Title == "A" && s.Status == AgentStepStatus.Done);
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
        // The single-turn fallback is only for an INITIAL plan degrade, never a mid-run replan degrade.
        Assert.False(exec.FallbackCalled);
        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Failed, final!.State);
        Assert.Contains(final.Plan, s => s.Title == "A" && s.Status == AgentStepStatus.Done);
        Assert.True(exec.EndCalled);
        Assert.True(exec.EndFailed);
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

        Assert.Equal(2, exec.Executed.Count);
        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.WaitingForInput, final!.State);
        Assert.Contains("paused", final.ExtraJson ?? string.Empty);
        Assert.Contains("step-cap", final.ExtraJson ?? string.Empty);
        Assert.DoesNotContain("truncated", final.ExtraJson ?? string.Empty);
        Assert.Null(final.CompletedAt);
        // A pause must not raise a terminal EndRun, but must call OnPaused so a live session is unwedged.
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

        var profile = new RunProfile(MaxSteps: 2, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20));
        var exec1 = new RecordingExecutor(_ => Ok());
        await h.BuildOrchestrator(planner).RunAsync(run, exec1, Persona(), Provider(), profile, ct);
        Assert.Equal(new[] { "s1", "s2" }, exec1.Executed);
        Assert.Equal(AgentRunState.WaitingForInput, (await h.Runs.GetAsync(run.Id, ct))!.State);

        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, ct));
        var fresh = new RunProfile(MaxSteps: 24, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20));
        var exec2 = new RecordingExecutor(_ => Ok());
        await h.BuildOrchestrator(planner).RunAsync(run, exec2, Persona(), Provider(), fresh, ct, resume: true);

        Assert.Equal(new[] { "s3" }, exec2.Executed);
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
        var exec1 = new RecordingExecutor(_ => OkUsage(10, 5));
        await h.BuildOrchestrator(planner).RunAsync(run, exec1, Persona(), Provider(), profile, ct);

        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, ct));
        var fresh = new RunProfile(MaxSteps: 24, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20));
        var exec2 = new RecordingExecutor(_ => OkUsage(10, 5));
        await h.BuildOrchestrator(planner).RunAsync(run, exec2, Persona(), Provider(), fresh, ct, resume: true);

        var final = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Completed, final!.State);
        using var doc = JsonDocument.Parse(final.LedgerJson!);
        var root = doc.RootElement;
        Assert.Equal(30, root.GetProperty("inputTokens").GetInt64());
        Assert.Equal(15, root.GetProperty("outputTokens").GetInt64());
    }

    [Fact]
    public async Task Run_Resume_PreservesRunMessageRange_AcrossPause()
    {
        using var h = new Harness();
        var ct = TestContext.Current.CancellationToken;
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1"), ("B", "s2"), ("C", "s3")), false));

        var profile = new RunProfile(MaxSteps: 2, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20));
        await h.BuildOrchestrator(planner).RunAsync(run, new RecordingExecutor(_ => Ok()), Persona(), Provider(), profile, ct);
        var parked = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.WaitingForInput, parked!.State);
        var pinnedFirst = parked.FirstMessageId;
        Assert.NotNull(pinnedFirst);

        // The resume must be handed the freshly fetched run: the terminal pin seeds its first id from there.
        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, ct));
        var resumeRun = await h.Runs.GetAsync(run.Id, ct);
        var fresh = new RunProfile(MaxSteps: 24, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20));
        await h.BuildOrchestrator(planner).RunAsync(resumeRun!, new RecordingExecutor(_ => Ok()), Persona(), Provider(), fresh, ct, resume: true);

        var final = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.Equal(pinnedFirst, final.FirstMessageId);
    }

    [Fact]
    public async Task Run_CancelDuringResume_SettlesCancelled_SlicePinned()
    {
        using var h = new Harness();
        var ct = TestContext.Current.CancellationToken;
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1"), ("B", "s2"), ("C", "s3")), false));

        var profile = new RunProfile(MaxSteps: 2, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20));
        var exec1 = new RecordingExecutor(_ => Ok()); // Ok() carries non-empty message ids, so there is a real slice
        await h.BuildOrchestrator(planner).RunAsync(run, exec1, Persona(), Provider(), profile, ct);
        var parked = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.WaitingForInput, parked!.State);
        Assert.NotNull(parked.FirstMessageId);

        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, ct));
        using var sessionCts = new CancellationTokenSource();
        var exec2 = new CancellingExecutor(sessionCts);
        var fresh = new RunProfile(MaxSteps: 24, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20));
        await h.BuildOrchestrator(planner).RunAsync(run, exec2, Persona(), Provider(), fresh, sessionCts.Token, resume: true);

        var final = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Cancelled, final!.State);
        Assert.True(exec2.EndCancelled);
        Assert.NotNull(final.FirstMessageId);
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

        Assert.Equal(new[] { "s1", "s2" }, exec.Executed);
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
        planner.Plans.Enqueue(PlanResult.Fallback);
        var exec = new RecordingExecutor(_ => Ok());

        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.True(exec.FallbackCalled);
        Assert.Empty(exec.Executed);
        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.Empty(final.Plan);
    }

    [Fact]
    public async Task Run_WallClockExhausted_PausesIntoWaitingForInput_WallClockReason()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        // A zero wall-clock budget trips on the very first loop iteration, before any step is dispatched.
        var profile = new RunProfile(MaxSteps: 24, MaxReplans: 2, WallClock: TimeSpan.Zero);
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1"), ("B", "s2"), ("C", "s3")), false));
        var exec = new RecordingExecutor(_ => Ok());

        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), profile, TestContext.Current.CancellationToken);

        Assert.Empty(exec.Executed);
        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.WaitingForInput, final!.State);
        Assert.Contains("paused", final.ExtraJson ?? string.Empty);
        Assert.Contains("wall-clock", final.ExtraJson ?? string.Empty);
        Assert.Null(final.CompletedAt);
        Assert.False(exec.EndCalled);
        Assert.True(exec.PausedCalled);
    }

    [Fact]
    public async Task Run_SessionCancelDuringStep_LinkedCts_CancelsInFlightStep_Cancelled()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1"), ("B", "s2")), false));

        // sessionCts stands in for ChatSession.Cts; the run's own CTS is linked from this token.
        using var sessionCts = new CancellationTokenSource();
        var exec = new CancellingExecutor(sessionCts);

        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, sessionCts.Token);

        Assert.Equal(new[] { "s1" }, exec.Executed);
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
        var exec = new RecordingExecutor(_ => OkUsage(10, 5));

        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.NotNull(final.LedgerJson);

        using var doc = JsonDocument.Parse(final.LedgerJson!);
        var root = doc.RootElement;
        Assert.Equal(20, root.GetProperty("inputTokens").GetInt64());
        Assert.Equal(10, root.GetProperty("outputTokens").GetInt64());
        Assert.Equal(2, root.GetProperty("perStep").GetArrayLength());
    }

    // ---- Verify/critic pass ----

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
        verifier.Verdicts.Enqueue(new VerdictResult(false, "not yet", new[] { "x" }, null));
        verifier.Verdicts.Enqueue(VerdictResult.Accept);
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
        verifier.Verdicts.Enqueue(new VerdictResult(false, "nope", new[] { "x" }, null));
        verifier.Verdicts.Enqueue(new VerdictResult(false, "still nope", new[] { "x" }, null));
        var exec = new RecordingExecutor(_ => Ok());

        await h.BuildOrchestrator(planner, verifier).RunAsync(run, exec, Persona(), Provider(), profile, TestContext.Current.CancellationToken);

        Assert.Equal(1, planner.ReplanCalls);
        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
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
        Assert.Equal(AgentRunState.Completed, final!.State);
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
        Assert.Equal(7, root.GetProperty("inputTokens").GetInt64());
        Assert.Equal(3, root.GetProperty("outputTokens").GetInt64());
        Assert.Equal(0, root.GetProperty("perStep").GetArrayLength()); // verify accrues run-level, with a null step id
    }

    // ---- plan/replan spend reaches the run ledger ----

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
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1")), false, Usage(40, 12)));
        var exec = new RecordingExecutor(_ => Ok()); // null step usage, so no per-step entry

        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        var (input, output, perStep) = Ledger(final);
        Assert.Equal(40, input);
        Assert.Equal(12, output);
        Assert.Equal(0, perStep); // planning is run-level spend, never a step entry
    }

    [Fact]
    public async Task Run_PlannerDegradeUsage_AccruesToRunLedger_OnTheSingleTurnPath()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(PlanResult.Fallback with { Usage = Usage(80, 24) });
        var exec = new RecordingExecutor(_ => Ok());

        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.True(exec.FallbackCalled);
        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        var (input, output, _) = Ledger(final);
        Assert.Equal(80, input);
        Assert.Equal(24, output);
    }

    [Fact]
    public async Task Run_SingleTurnFallbackUsage_AccruesToRunLedger()
    {
        // The fallback turn owns no step row, so only a run-level accrual can bill it at all.
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
        Assert.Equal(0, perStep);
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
        planner.Replans.Enqueue(PlanResult.Fallback with { Usage = Usage(30, 9) });
        var exec = new RecordingExecutor(_ => Fail("boom"));

        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Failed, final!.State);
        var (input, output, _) = Ledger(final);
        Assert.Equal(70, input);
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
        verifier.Verdicts.Enqueue(new VerdictResult(false, "not yet", new[] { "x" }, Usage(7, 3)));
        verifier.Verdicts.Enqueue(VerdictResult.Accept with { Usage = Usage(7, 3) });
        var exec = new RecordingExecutor(_ => OkUsage(10, 5));

        await h.BuildOrchestrator(planner, verifier).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        var (input, output, perStep) = Ledger(final);
        Assert.Equal(40 + 30 + 7 + 7 + 10 + 10, input);  // plan + verify-fail replan + 2 verifies + 2 steps
        Assert.Equal(12 + 9 + 3 + 3 + 5 + 5, output);
        Assert.Equal(2, perStep);
    }

    [Fact]
    public async Task Run_PlannerUsageBookkeepingFaults_DoesNotFailTheRun()
    {
        // The accrual is bookkeeping: a ledger write fault must never fail an otherwise-clean run.
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

    // ---- the orchestrator's DECLINE branch ----

    [Fact]
    public async Task Run_PlannerDeclinesTheGoal_ParksNeedsGoal_NoSteps_NoFallback_AndBillsThePlanTurn()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("ggg");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(PlanResult.Decline("what do you mean by that?") with { Usage = Usage(31, 7) });
        var exec = new RecordingExecutor(_ => Ok());

        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.False(exec.FallbackCalled);
        Assert.Empty(exec.Executed);
        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.WaitingForInput, final!.State);
        Assert.Contains("needs-goal", final.ExtraJson ?? string.Empty);
        Assert.Empty(final.Plan);
        Assert.Null(final.CompletedAt);
        Assert.False(exec.EndCalled);
        Assert.True(exec.PausedCalled);
        var (input, output, perStep) = Ledger(final);
        Assert.Equal(31, input);           // the decline branch sits after the usage accrual, not in front of it
        Assert.Equal(7, output);
        Assert.Equal(0, perStep);
    }

    // ---- a resumed run's critic/replan must see the pre-pause work ----

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

        var profile = new RunProfile(MaxSteps: 2, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20));
        var firstVerifier = new FakeVerifier();
        await h.BuildOrchestrator(planner, firstVerifier).RunAsync(run, new RecordingExecutor(_ => Ok()), Persona(), Provider(), profile, ct);
        Assert.Equal(AgentRunState.WaitingForInput, (await h.Runs.GetAsync(run.Id, ct))!.State);
        Assert.Empty(firstVerifier.SeenCompletedSteps);

        // The resume reuses the same 2-step budget: if the seeded steps were billed against it, it would re-park instantly.
        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, ct));
        var verifier = new FakeVerifier();
        var exec2 = new RecordingExecutor(_ => Ok("post-resume text"));
        await h.BuildOrchestrator(planner, verifier).RunAsync(run, exec2, Persona(), Provider(), profile, ct, resume: true);

        Assert.Equal(new[] { "s3" }, exec2.Executed);
        Assert.Equal(AgentRunState.Completed, (await h.Runs.GetAsync(run.Id, ct))!.State);

        var seen = Assert.Single(verifier.SeenCompletedSteps);
        Assert.Equal(new[] { 0, 1, 2 }, seen.Select(s => s.Ordinal).ToArray());
        Assert.Equal(new[] { "A", "B", "C" }, seen.Select(s => s.Title).ToArray());
        Assert.Equal(new[] { "one.md", "two.md", "three.md" }, seen.Select(s => s.ExpectedArtifact).ToArray());
        Assert.Equal(new[] { true, true, false }, seen.Select(s => s.FromEarlierSegment).ToArray());
        Assert.All(seen.Take(2), s => Assert.True(s.Succeeded));
        Assert.All(seen.Take(2), s => Assert.Equal(string.Empty, s.VisibleText)); // a seeded step's text is not recoverable
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

        // The replan judge must be told about s1/s2, which it cannot see in this process any more.
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
        // The seed is bookkeeping: a failing read degrades to the partial picture, never fails the resume.
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
        Assert.Single(seen); // degraded to the post-resume slice only
    }

    [Fact]
    public async Task Run_FreshRun_SeedsNothing_NoEarlierSegmentMarkers()
    {
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

    // ---- the artifact a step reported survives the park, so a resumed critic sees the same evidence ----

    [Fact]
    public async Task Run_Resume_VerifierSeesTheArtifactRefEachStepReported()
    {
        using var h = new Harness();
        var ct = TestContext.Current.CancellationToken;
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1"), ("B", "s2"), ("C", "s3")), false));

        var profile = new RunProfile(MaxSteps: 2, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20));
        var exec = new RecordingExecutor(step => step.Intent switch
        {
            "s1" => OkWithArtifact("done", "one.md"),
            "s2" => OkWithArtifact("done", "two.md"),
            _ => Ok(),
        });
        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), profile, ct);
        Assert.Equal(AgentRunState.WaitingForInput, (await h.Runs.GetAsync(run.Id, ct))!.State);

        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, ct));
        var verifier = new FakeVerifier();
        var exec2 = new RecordingExecutor(_ => Ok("post-resume text"));
        await h.BuildOrchestrator(planner, verifier).RunAsync(run, exec2, Persona(), Provider(), profile, ct, resume: true);

        var seen = Assert.Single(verifier.SeenCompletedSteps);
        Assert.Equal(new[] { "one.md", "two.md", null }, seen.Select(s => s.Outcome?.ArtifactRef).ToArray());
        Assert.Equal(new[] { true, true, false }, seen.Select(s => s.FromEarlierSegment).ToArray());
        Assert.Equal("ok, declared", AgentVerifier.OutcomeTag(seen[0]));
    }

    [Fact]
    public async Task Run_Resume_StepThatReportedNoArtifact_SeedsANullOutcome()
    {
        // The persisted datum is the artifact, not the declaration flag — so a step that declared success
        // without one comes back unconfirmed rather than inventing a claim nobody stored.
        using var h = new Harness();
        var ct = TestContext.Current.CancellationToken;
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1"), ("B", "s2"), ("C", "s3")), false));

        var profile = new RunProfile(MaxSteps: 2, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20));
        var exec = new RecordingExecutor(step => step.Intent == "s1" ? OkDeclaringNoArtifact() : Ok());
        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), profile, ct);

        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, ct));
        var verifier = new FakeVerifier();
        await h.BuildOrchestrator(planner, verifier).RunAsync(
            run, new RecordingExecutor(_ => Ok()), Persona(), Provider(), profile, ct, resume: true);

        var seen = Assert.Single(verifier.SeenCompletedSteps);
        Assert.Null(seen[0].Outcome);
        Assert.Null(seen[1].Outcome);
        Assert.Equal("ok, unconfirmed", AgentVerifier.OutcomeTag(seen[0]));
    }

    [Fact]
    public async Task Run_Resume_MalformedStepExtraJson_SeedsNoOutcome_AndStillCompletes()
    {
        using var h = new Harness();
        var ct = TestContext.Current.CancellationToken;
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1"), ("B", "s2"), ("C", "s3")), false));

        var profile = new RunProfile(MaxSteps: 2, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20));
        var exec = new RecordingExecutor(_ => OkWithArtifact("done", "one.md"));
        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), profile, ct);

        var parked = (await h.Runs.GetAsync(run.Id, ct))!;
        CorruptStepExtras(h, parked.Plan.Single(s => s.Title == "A").Id);

        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, ct));
        var verifier = new FakeVerifier();
        await h.BuildOrchestrator(planner, verifier).RunAsync(
            run, new RecordingExecutor(_ => Ok()), Persona(), Provider(), profile, ct, resume: true);

        Assert.Equal(AgentRunState.Completed, (await h.Runs.GetAsync(run.Id, ct))!.State);
        var seen = Assert.Single(verifier.SeenCompletedSteps);
        Assert.Null(seen[0].Outcome);
        Assert.Equal("one.md", seen[1].Outcome!.ArtifactRef); // the unreadable row costs only itself
    }

    [Fact]
    public async Task Run_ReplanAfterAStepReportedAnArtifact_PreservesIt()
    {
        // A replan rewrites the plan with DELETE + re-INSERT, so the kept Done row has to carry its ExtraJson.
        using var h = new Harness();
        var ct = TestContext.Current.CancellationToken;
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1"), ("B", "s2fail")), false));
        planner.Replans.Enqueue(new PlanResult(MakeSteps(("D", "s4")), false));

        var exec = new RecordingExecutor(step => step.Intent switch
        {
            "s1" => OkWithArtifact("done", "one.md"),
            "s2fail" => Fail("boom"),
            _ => Ok(),
        });
        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, ct);

        var final = await h.Runs.GetAsync(run.Id, ct);
        var kept = final!.Plan.Single(s => s.Title == "A");
        Assert.Equal("one.md", StepExtraJson.ArtifactRefOf(kept));
    }

    private static void CorruptStepExtras(Harness h, Guid stepId)
    {
        using var cmd = h.Ctx.GetConnection().CreateCommand();
        cmd.CommandText = "UPDATE AgentSteps SET ExtraJson='not json' WHERE Id=@Id";
        cmd.Parameters.AddWithValue("@Id", stepId.ToString());
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task Run_SessionCancelDuringVerify_PropagatesCancelled_RangePinned()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1")), false));

        // A genuine run cancel is rethrown out of verify rather than degrading to accept, so nothing settles Completed.
        using var sessionCts = new CancellationTokenSource();
        var verifier = new FakeVerifier { CancelSessionOnVerify = sessionCts };
        var exec = new RecordingExecutor(_ => Ok());

        await h.BuildOrchestrator(planner, verifier).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, sessionCts.Token);

        Assert.Equal(1, verifier.VerifyCalls);
        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Cancelled, final!.State);
        Assert.True(exec.EndCalled);
        Assert.True(exec.EndCancelled);
        Assert.NotNull(final.FirstMessageId);
        Assert.NotEqual(Guid.Empty, final.FirstMessageId!.Value);
    }

    // ---- promotion on the terminal path ----

    // Promotion has to land before CompleteAsync, or a Completed run's files live only in a sweepable workspace.
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

    // The fallback arm is a second terminal path: it returns early and settles Complete before EndRun.
    [Fact]
    public async Task TheSingleTurnFallbackArm_AlsoPromotes()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var order = new List<string>();
        var planner = new FakePlanner(); // an empty queue plans PlanResult.Fallback, taking the degrade arm
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

    // RetainWorkspace means the workspace still holds the only copy of what promotion could not move.
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

        Assert.Equal(new[] { "promote", "complete" }, order); // no teardown between them
        Assert.Empty(workspaces.TornDown);
        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State); // non-vacuity: this is the clean terminal path
    }

    // A cancelled or failed run keeps its workspace, so the panel still has something to offer.
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
        // Zero replans keeps the failure terminal: otherwise the step-fail row would replan into unverified-Completed.
        var profile = new RunProfile(MaxSteps: 8, MaxReplans: 0, WallClock: TimeSpan.FromMinutes(5));

        await h.BuildOrchestrator(planner, verifier: null, workspaces)
            .RunAsync(run, exec, Persona(), Provider(), profile, TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.True(final!.State is AgentRunState.Failed or AgentRunState.Cancelled); // non-vacuity
        Assert.Empty(workspaces.Promoted);
        Assert.Empty(workspaces.TornDown);
    }

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

        // Second half: a service is injected but the run has no workspace root, so there is nothing to promote.
        var noRoot = await h.NewRunAsync("goal");
        var planner2 = new FakePlanner();
        planner2.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1")), false));
        var workspaces = new FakeRunWorkspaceService(h.RunsBase);
        var unisolated = new RecordingExecutor(_ => Ok());

        await h.BuildOrchestrator(planner2, verifier: null, workspaces).RunAsync(
            noRoot, unisolated, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunState.Completed,
            (await h.Runs.GetAsync(noRoot.Id, TestContext.Current.CancellationToken))!.State);
        Assert.Empty(workspaces.Promoted);
    }

    // ---- the mid-plan ask park, at the loop level ----

    // The asking step must go back to Pending, not Failed: NextPendingStepAsync and KeepDoneAsync never see failed steps.
    [Fact]
    public async Task AStepThatAsks_ParksNeedsInput_KeepsDoneSteps_AndGivesTheAskingStepBack()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1"), ("B", "s2")), false));
        var exec = new RecordingExecutor(step => step.Intent == "s2" ? Ask("which cluster?") : Ok());

        await h.BuildOrchestrator(planner).RunAsync(
            run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.WaitingForInput, final!.State);
        Assert.Contains("needs-input", final.ExtraJson ?? string.Empty, StringComparison.Ordinal);
        Assert.Null(final.CompletedAt);

        Assert.Equal(AgentStepStatus.Done, final.Plan.Single(s => s.Title == "A").Status);
        Assert.Equal(AgentStepStatus.Pending, final.Plan.Single(s => s.Title == "B").Status);

        Assert.Equal(0, planner.ReplanCalls); // an ask is not a failure
        Assert.True(exec.PausedCalled);
        Assert.False(exec.EndCalled);
    }

    // The ask rides its own member rather than the outcome bool, so a declared failure alongside it changes nothing.
    [Fact]
    public async Task AStepThatAsksAndAlsoDeclaresFailure_StillParks_AndNeverReplans()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1")), false));
        planner.Replans.Enqueue(new PlanResult(MakeSteps(("R", "revised")), false)); // armed, so a replan would be visible
        var exec = new RecordingExecutor(_ => Ask("which cluster?") with
        {
            Succeeded = false,
            Error = "blocked on the target cluster",
            Outcome = new StepOutcomeClaim(false, "blocked on the target cluster", null),
        });

        await h.BuildOrchestrator(planner).RunAsync(
            run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.WaitingForInput, final!.State);
        Assert.Equal(0, planner.ReplanCalls);
        Assert.Equal(AgentStepStatus.Pending, final.Plan.Single().Status);
    }

    // The approval is the call that actually stopped the exchange, and re-asking costs nothing on the resumed step.
    [Fact]
    public async Task WhenAStepBothParksForApprovalAndAsks_TheApprovalWins()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1")), false));
        var exec = new RecordingExecutor(_ => Ask("which cluster?") with { ApprovalRequiredTool = "write_file" });

        await h.BuildOrchestrator(planner).RunAsync(
            run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.WaitingForInput, final!.State);
        Assert.Contains("tool-approval", final.ExtraJson ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("needs-input", final.ExtraJson ?? string.Empty, StringComparison.Ordinal);
    }

    // A step that will re-run must not carry a per-step ledger entry for the attempt that did not finish.
    [Fact]
    public async Task AnAskingStepsTokensAreBilledRunLevel()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1")), false));
        var exec = new RecordingExecutor(_ => Ask("which cluster?") with
        {
            Usage = new UsageDetails { InputTokenCount = 30, OutputTokenCount = 12 },
        });

        await h.BuildOrchestrator(planner).RunAsync(
            run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        var (input, output, perStep) = Ledger(final!);
        Assert.Equal(30, input);
        Assert.Equal(12, output);
        Assert.Equal(0, perStep);
    }

    [Fact]
    public async Task PostPlanRejectedNoticeAsync_PostsANoticeIntoTheRunsChat()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        // Echoes the key back, so the assertion can name the key instead of a translated sentence.
        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        var orchestrator = h.BuildOrchestrator(new FakePlanner(), localization: loc);

        await orchestrator.PostPlanRejectedNoticeAsync(run.Id, Persona(), ct);

        var chat = await h.Chats.GetAsync(run.ChatId, ct);
        Assert.Contains(chat!.Messages, m => m.Content == "Run_PlanRejected_ChatNote");
    }

    [Fact]
    public async Task PostPlanRejectedNoticeAsync_NoOps_WhenLocalizationIsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var orchestrator = h.BuildOrchestrator(new FakePlanner());

        await orchestrator.PostPlanRejectedNoticeAsync(run.Id, Persona(), ct);

        var chat = await h.Chats.GetAsync(run.ChatId, ct);
        Assert.Empty(chat?.Messages ?? []);
    }
}
