using System.IO;
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
        public bool FallbackCalled { get; private set; }

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

        public Task EndRunAsync(AgentRun run, RunContext ctx, bool cancelled, CancellationToken ct)
        {
            EndCalled = true; EndCancelled = cancelled;
            return Task.CompletedTask;
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

        public AgentRunOrchestrator BuildOrchestrator(IAgentPlanner planner) =>
            new(Runs, planner, NullLogger<AgentRunOrchestrator>.Instance);

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
    public async Task Run_BudgetExhausted_CompletedTruncated()
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
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.Contains("truncated", final.ExtraJson ?? string.Empty);
        Assert.Contains("step-cap", final.ExtraJson ?? string.Empty);
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
}
