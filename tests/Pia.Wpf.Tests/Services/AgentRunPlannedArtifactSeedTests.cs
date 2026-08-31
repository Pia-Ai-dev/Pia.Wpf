using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>The data half of E1: what a step is told the LATER steps still owe. The prompt half is
/// <c>AgentStepInstruction</c>'s.</summary>
public sealed class AgentRunPlannedArtifactSeedTests
{
    private static Persona Persona() => new() { Name = "Pia", SystemPrompt = "sys" };

    private static AiProvider Provider() => new() { Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };

    private static List<AgentStep> Steps(params (string Title, string? Artifact)[] specs) =>
        specs.Select((s, i) => new AgentStep
        {
            Id = Guid.Empty,
            Ordinal = i,
            Title = s.Title,
            Intent = s.Title,
            ExpectedArtifact = s.Artifact,
            Status = AgentStepStatus.Pending,
        }).ToList();

    private sealed class FakePlanner : IAgentPlanner
    {
        public Queue<PlanResult> Plans { get; } = new();

        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(Plans.Count > 0 ? Plans.Dequeue() : PlanResult.Fallback);

        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(PlanResult.Fallback);
    }

    /// <summary>Snapshots <c>ctx.PlannedArtifacts</c> per step; <c>onStep</c> is the seam a test uses to mutate
    /// the plan from inside a turn.</summary>
    private sealed class ArtifactCapturingExecutor : IAgentTurnExecutor
    {
        private readonly Func<AgentStep, Task>? _onStep;

        public ArtifactCapturingExecutor(Func<AgentStep, Task>? onStep = null) => _onStep = onStep;

        public List<(int Ordinal, IReadOnlyList<PlannedStepArtifact> Planned)> Captured { get; } = new();

        public Task BeginRunAsync(AgentRun run, RunContext ctx, CancellationToken ct) => Task.CompletedTask;

        public async Task<StepTurnResult> ExecuteStepAsync(AgentRun run, AgentStep step, RunContext ctx, CancellationToken ct)
        {
            Captured.Add((step.Ordinal, ctx.PlannedArtifacts.ToList()));
            if (_onStep is not null)
                await _onStep(step);
            return new StepTurnResult(true, false, null, "done", null, Guid.NewGuid(), Guid.NewGuid());
        }

        public Task<StepTurnResult> RunSingleTurnFallbackAsync(AgentRun run, RunContext ctx, CancellationToken ct)
            => Task.FromResult(new StepTurnResult(true, false, null, "fallback", null, Guid.NewGuid(), Guid.NewGuid()));

        public Task EndRunAsync(AgentRun run, RunContext ctx, bool cancelled, bool failed, CancellationToken ct) => Task.CompletedTask;

        public Task OnPausedAsync(AgentRun run, RunContext ctx, CancellationToken ct) => Task.CompletedTask;
    }

    // A trimmed copy of AgentRunOrchestratorTests.FaultyRunService (private there) — the same duplication the
    // ArtifactProbe README sanctions.
    private sealed class FaultyRunService : IAgentRunService
    {
        private readonly IAgentRunService _inner;
        public FaultyRunService(IAgentRunService inner) => _inner = inner;

        public bool FailGet { get; set; }

        public Task<AgentRun?> GetAsync(Guid runId, CancellationToken ct = default)
            => FailGet ? throw new InvalidOperationException("read boom") : _inner.GetAsync(runId, ct);

        public Task AddUsageAsync(Guid runId, Guid? stepId, UsageDetails usage, CancellationToken ct = default)
            => _inner.AddUsageAsync(runId, stepId, usage, ct);
        public Task<AgentRun> CreateAsync(AgentRunCreateRequest request, CancellationToken ct = default) => _inner.CreateAsync(request, ct);
        public Task SetStateAsync(Guid runId, AgentRunState state, CancellationToken ct = default) => _inner.SetStateAsync(runId, state, ct);
        public Task SetRunMessageRangeAsync(Guid runId, Guid firstMessageId, Guid lastMessageId, CancellationToken ct = default)
            => _inner.SetRunMessageRangeAsync(runId, firstMessageId, lastMessageId, ct);
        public Task CompleteAsync(Guid runId, bool truncated = false, string? truncationReason = null, CancellationToken ct = default)
            => _inner.CompleteAsync(runId, truncated, truncationReason, ct);
        public Task FailAsync(Guid runId, string? error, bool cancelled = false, CancellationToken ct = default,
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

    // A trimmed copy of AgentRunNudgeParityTests.OrchestratorHarness.
    private sealed class Harness : IDisposable
    {
        private readonly string _dir;

        public Harness(ILogger<AgentRunOrchestrator>? orchestratorLogger = null)
        {
            _dir = Path.Combine(Path.GetTempPath(), "PiaPlannedArtifacts_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            SqlCtx = new SqliteContext(Path.Combine(_dir, "history.db"));
            Runs = new AgentRunService(SqlCtx, NullLogger<AgentRunService>.Instance);
            Chats = new AssistantChatService(SqlCtx, Runs);
            OrchestratorLogger = orchestratorLogger ?? NullLogger<AgentRunOrchestrator>.Instance;
        }

        public SqliteContext SqlCtx { get; }
        public AgentRunService Runs { get; }
        public AssistantChatService Chats { get; }
        public ILogger<AgentRunOrchestrator> OrchestratorLogger { get; }

        public async Task<AgentRun> NewRunAsync(string goal, CancellationToken ct)
        {
            var chatId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            await Chats.SaveAsync(new SyncAssistantChat
            {
                Id = chatId, SchemaVersion = 1, Title = "t", CreatedAt = now, UpdatedAt = now,
                LastAccessedAt = now, WindowMode = WindowMode.Assistant.ToString(), Messages = [],
            }, ct);
            return await Runs.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: goal), ct);
        }

        public AgentRunOrchestrator BuildOrchestrator(IAgentPlanner planner, IAgentRunService? runs = null) =>
            new(runs ?? Runs, planner, new FakeVerifier(), OrchestratorLogger,
                workspaces: null, childLauncher: null, chats: null, steering: null);

        public void Dispose()
        {
            Runs.Dispose();
            SqlCtx.Dispose();
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task EachStep_SeesTheDeclaredArtifactsOfTheStillPendingSteps_AndNeverItsOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal", ct);
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(
            Steps(("s0", "A0.md"), ("s1", "A1.md"), ("s2", "A2.md")), false));

        var exec = new ArtifactCapturingExecutor();
        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, ct);

        Assert.Equal(AgentRunState.Completed, (await h.Runs.GetAsync(run.Id, ct))!.State);
        Assert.Equal([0, 1, 2], exec.Captured.Select(c => c.Ordinal));

        Assert.Equal(
            new[] { new PlannedStepArtifact(1, "A1.md"), new PlannedStepArtifact(2, "A2.md") },
            exec.Captured[0].Planned);
        Assert.DoesNotContain(exec.Captured[0].Planned, p => p.Artifact == "A0.md");

        Assert.Equal(new[] { new PlannedStepArtifact(2, "A2.md") }, exec.Captured[1].Planned);
        Assert.Empty(exec.Captured[2].Planned);
    }

    [Fact]
    public async Task ThePlannedArtifactsAreReadFreshPerStep_NotOncePerDispatch()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal", ct);
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(
            Steps(("s0", "A0.md"), ("s1", "A1.md"), ("s2", "OLD.md")), false));

        // The last step's declaration is rewritten while the first one runs, the way a mid-drain replan
        // rewrites the pending tail; the middle step's dispatch must read the new value.
        var exec = new ArtifactCapturingExecutor(async step =>
        {
            if (step.Ordinal != 0) return;
            var persisted = (await h.Runs.GetAsync(run.Id, ct))!.Plan.OrderBy(s => s.Ordinal).ToList();
            persisted[2].ExpectedArtifact = "NEW.md";
            await h.Runs.ReplaceStepsAsync(run.Id, persisted, ct);
        });

        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, ct);

        Assert.Equal(AgentRunState.Completed, (await h.Runs.GetAsync(run.Id, ct))!.State);
        Assert.Contains(exec.Captured[0].Planned, p => p.Artifact == "OLD.md");
        Assert.Equal(new[] { new PlannedStepArtifact(2, "NEW.md") }, exec.Captured[1].Planned);
    }

    [Fact]
    public async Task AFaultingPlanReadLeavesThePlannedArtifactsEmpty_AndTheRunStillCompletes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal", ct);
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(Steps(("s0", "A0.md"), ("s1", "A1.md")), false));

        var exec = new ArtifactCapturingExecutor();
        var faulty = new FaultyRunService(h.Runs) { FailGet = true };
        await h.BuildOrchestrator(planner, faulty).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, ct);

        Assert.Equal(AgentRunState.Completed, (await h.Runs.GetAsync(run.Id, ct))!.State);
        Assert.Equal([0, 1], exec.Captured.Select(c => c.Ordinal));
        Assert.All(exec.Captured, c => Assert.Empty(c.Planned));
    }

    [Fact]
    public async Task ThePlannedArtifactSeed_PutsNoArtifactNameInTheLog()
    {
        var ct = TestContext.Current.CancellationToken;
        const string artifact = "PLANNED-ARTIFACT-Z9.md";
        var log = new CapturingLogger<AgentRunOrchestrator>();
        using var h = new Harness(log);
        var run = await h.NewRunAsync("goal", ct);
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(Steps(("s0", null), ("s1", artifact)), false));

        var exec = new ArtifactCapturingExecutor();
        await h.BuildOrchestrator(planner).RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, ct);

        // Non-vacuity: the name really did travel the seam this test says is silent.
        Assert.Contains(exec.Captured[0].Planned, p => p.Artifact == artifact);
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains(artifact, StringComparison.Ordinal));
    }
}
