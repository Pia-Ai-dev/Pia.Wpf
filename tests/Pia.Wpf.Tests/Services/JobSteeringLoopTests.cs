using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

public sealed class JobSteeringLoopTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Persona Persona() => new() { Id = Guid.NewGuid(), Name = "Pia", SystemPrompt = "sys" };

    private static AiProvider Provider() => new()
    {
        Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI, SupportsToolCalling = true,
    };

    private static StepTurnResult Ok() => new(true, false, null, "done", null, Guid.NewGuid(), Guid.NewGuid());

    private sealed class ScriptedExecutor : IAgentTurnExecutor
    {
        public List<string> Executed { get; } = new();
        public List<string?> Nudges { get; } = new();
        public Func<AgentStep, Task>? During { get; set; }
        public bool PausedCalled { get; private set; }

        public Task BeginRunAsync(AgentRun run, RunContext ctx, CancellationToken ct) => Task.CompletedTask;

        public async Task<StepTurnResult> ExecuteStepAsync(AgentRun run, AgentStep step, RunContext ctx, CancellationToken ct)
        {
            Executed.Add(step.Title);
            Nudges.Add(ctx.Nudge);
            if (During is { } during)
                await during(step);
            return Ok();
        }

        public Task<StepTurnResult> RunSingleTurnFallbackAsync(AgentRun run, RunContext ctx, CancellationToken ct) =>
            Task.FromResult(Ok());

        public Task EndRunAsync(AgentRun run, RunContext ctx, bool cancelled, bool failed, CancellationToken ct) =>
            Task.CompletedTask;

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
            _dir = Path.Combine(Path.GetTempPath(), "PiaJobLoop_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Ctx = new SqliteContext(Path.Combine(_dir, "history.db"));
            Runs = new AgentRunService(Ctx, NullLogger<AgentRunService>.Instance);
            Chats = new AssistantChatService(Ctx, Runs);
        }

        public SqliteContext Ctx { get; }
        public AgentRunService Runs { get; }
        public AssistantChatService Chats { get; }
        public JobSteeringStore Jobs { get; } = new();

        public async Task<AgentRun> PlannedRunAsync(params string[] titles)
        {
            var chatId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            await Chats.SaveAsync(new SyncAssistantChat
            {
                Id = chatId, SchemaVersion = 1, Title = "t", CreatedAt = now, UpdatedAt = now, LastAccessedAt = now,
                WindowMode = WindowMode.Assistant.ToString(), Messages = [],
            });
            var run = await Runs.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User,
                Goal: "g", PolicyJson: HeadlessRunLauncher.ChatJobEmptyEnvelopeJson));
            await Runs.ReplaceStepsAsync(run.Id, titles.Select((t, i) => new AgentStep
            {
                Id = Guid.NewGuid(), Ordinal = i, Title = t, Intent = t, Status = AgentStepStatus.Pending,
            }).ToList(), Ct);
            await Runs.PauseAsync(run.Id, "user", Ct);
            Assert.True(await Runs.TryBeginResumeAsync(run.Id, Ct));
            return (await Runs.GetAsync(run.Id, Ct))!;
        }

        public Task RunAsync(AgentRun run, IAgentTurnExecutor executor, string? nudge = null) =>
            new AgentRunOrchestrator(Runs, new NoPlanner(), new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance,
                    jobSteering: Jobs)
                .RunAsync(run, executor, Persona(), Provider(), RunProfile.Interactive, Ct, resume: true, nudge: nudge);

        public async Task<AgentStep> StepAsync(Guid runId, string title) =>
            (await Runs.GetAsync(runId, Ct))!.Plan.Single(s => s.Title == title);

        public void Dispose()
        {
            Runs.Dispose();
            Chats.Dispose();
            Ctx.Dispose();
            TempPath.Remove(_dir);
        }
    }

    private sealed class NoPlanner : IAgentPlanner
    {
        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct) =>
            Task.FromResult(PlanResult.Fallback);

        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct) =>
            Task.FromResult(PlanResult.Fallback);
    }

    [Fact]
    public async Task AQueuedChange_IsAppliedAtTheNextLoopTop_AndNeverToAStartedStep()
    {
        using var h = new Harness();
        var run = await h.PlannedRunAsync("s1", "s2", "s3");
        var s1 = await h.StepAsync(run.Id, "s1");
        var s3 = await h.StepAsync(run.Id, "s3");
        var inserted = Guid.NewGuid();
        var planDuringS1 = new List<string>();
        var exec = new ScriptedExecutor
        {
            During = async step =>
            {
                if (step.Title != "s1") return;
                h.Jobs.QueuePlanChanges(run.Id,
                [
                    new JobPlanChange(JobPlanChangeKind.Edit, s1.Id, Title: "s1 changed"),
                    new JobPlanChange(JobPlanChangeKind.Insert, inserted, AtStart: true, Title: "new"),
                    new JobPlanChange(JobPlanChangeKind.Remove, s3.Id),
                ]);
                planDuringS1.AddRange((await h.Runs.GetAsync(run.Id, Ct))!.Plan.Select(s => s.Title));
            },
        };

        await h.RunAsync(run, exec);

        Assert.Equal(["s1", "s2", "s3"], planDuringS1);
        Assert.Equal(["s1", "new", "s2"], exec.Executed);
        var final = (await h.Runs.GetAsync(run.Id, Ct))!;
        Assert.Equal(["s1", "new", "s2"], final.Plan.OrderBy(s => s.Ordinal).Select(s => s.Title));
        Assert.Contains(final.Plan, s => s.Id == inserted);
        Assert.Equal(1, h.Jobs.TakeDropped(run.Id));
        Assert.Empty(h.Jobs.ChangedSteps(run.Id));
    }

    private sealed class LateQueueStore(JobSteeringStore inner) : IJobSteeringStore
    {
        public Action? AfterTake { get; set; }
        public event EventHandler<Guid>? Changed { add => inner.Changed += value; remove => inner.Changed -= value; }
        public void QueuePlanChanges(Guid runId, IReadOnlyList<JobPlanChange> changes) => inner.QueuePlanChanges(runId, changes);
        public IReadOnlyList<JobPlanChange> PeekPlanChanges(Guid runId) => inner.PeekPlanChanges(runId);

        public IReadOnlyList<JobPlanChange> TakePlanChanges(Guid runId)
        {
            var taken = inner.TakePlanChanges(runId);
            AfterTake?.Invoke();
            return taken;
        }

        public void MarkChanged(Guid runId, IEnumerable<Guid> stepIds) => inner.MarkChanged(runId, stepIds);
        public IReadOnlySet<Guid> ChangedSteps(Guid runId) => inner.ChangedSteps(runId);
        public void UnmarkChanged(Guid runId, Guid stepId) => inner.UnmarkChanged(runId, stepId);
        public void AddNote(Guid runId, string note) => inner.AddNote(runId, note);
        public IReadOnlyList<string> Notes(Guid runId) => inner.Notes(runId);
        public void RequestPauseAfterStep(Guid runId) => inner.RequestPauseAfterStep(runId);
        public bool IsPauseAfterStepRequested(Guid runId) => inner.IsPauseAfterStepRequested(runId);
        public bool TryConsumePauseAfterStep(Guid runId) => inner.TryConsumePauseAfterStep(runId);
        public void DropPauseAfterStep(Guid runId) => inner.DropPauseAfterStep(runId);
        public void RecordDropped(Guid runId, int count) => inner.RecordDropped(runId, count);
        public int TakeDropped(Guid runId) => inner.TakeDropped(runId);
        public void Forget(Guid runId) => inner.Forget(runId);
    }

    [Fact]
    public async Task AChangeQueuedAfterTheLastLoopTop_IsStillApplied_AndItsStepRuns()
    {
        using var h = new Harness();
        var run = await h.PlannedRunAsync("s1");
        var store = new LateQueueStore(h.Jobs);
        var exec = new ScriptedExecutor();
        var queued = false;
        store.AfterTake = () =>
        {
            if (queued || !exec.Executed.Contains("s1")) return;
            queued = true;
            h.Jobs.QueuePlanChanges(run.Id,
                [new JobPlanChange(JobPlanChangeKind.Insert, Guid.NewGuid(), AtStart: false, Title: "late")]);
        };

        await new AgentRunOrchestrator(h.Runs, new NoPlanner(), new FakeVerifier(),
                NullLogger<AgentRunOrchestrator>.Instance, jobSteering: store)
            .RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, Ct, resume: true);

        Assert.Equal(["s1", "late"], exec.Executed);
        Assert.Empty(h.Jobs.PeekPlanChanges(run.Id));
        Assert.Equal(AgentRunState.Completed, (await h.Runs.GetAsync(run.Id, Ct))!.State);
    }

    [Fact]
    public async Task PauseAfterThisStep_KeepsTheFinishedStep()
    {
        using var h = new Harness();
        var run = await h.PlannedRunAsync("s1", "s2");
        var exec = new ScriptedExecutor
        {
            During = step =>
            {
                if (step.Title == "s1") h.Jobs.RequestPauseAfterStep(run.Id);
                return Task.CompletedTask;
            },
        };

        await h.RunAsync(run, exec);

        Assert.Equal(["s1"], exec.Executed);
        Assert.True(exec.PausedCalled);
        var final = (await h.Runs.GetAsync(run.Id, Ct))!;
        Assert.Equal(AgentRunState.Paused, final.State);
        Assert.Equal(AgentStepStatus.Done, final.Plan.Single(s => s.Title == "s1").Status);
        Assert.Equal(AgentStepStatus.Pending, final.Plan.Single(s => s.Title == "s2").Status);
    }

    [Fact]
    public async Task PauseAfterThisStep_OnTheLastStep_LetsTheJobFinish()
    {
        using var h = new Harness();
        var run = await h.PlannedRunAsync("s1");
        var exec = new ScriptedExecutor
        {
            During = _ =>
            {
                h.Jobs.RequestPauseAfterStep(run.Id);
                return Task.CompletedTask;
            },
        };

        await h.RunAsync(run, exec);

        Assert.Equal(AgentRunState.Completed, (await h.Runs.GetAsync(run.Id, Ct))!.State);
    }

    [Fact]
    public async Task ANote_ReachesEveryLaterStep_AndJoinsTheDispatchNudge()
    {
        using var h = new Harness();
        var run = await h.PlannedRunAsync("s1", "s2", "s3");
        var exec = new ScriptedExecutor
        {
            During = step =>
            {
                if (step.Title == "s1") h.Jobs.AddNote(run.Id, "write in German");
                return Task.CompletedTask;
            },
        };

        await h.RunAsync(run, exec, nudge: "be brief");

        Assert.Equal("be brief", exec.Nudges[0]);
        Assert.Equal("write in German / be brief", exec.Nudges[1]);
        Assert.Equal("write in German / be brief", exec.Nudges[2]);
    }

    [Fact]
    public async Task AChangedMark_StaysUntilTheStepStarts()
    {
        using var h = new Harness();
        var run = await h.PlannedRunAsync("s1", "s2");
        var s2 = await h.StepAsync(run.Id, "s2");
        var markedDuringS1 = false;
        var markedDuringS2 = true;
        var exec = new ScriptedExecutor
        {
            During = step =>
            {
                if (step.Title == "s1")
                {
                    h.Jobs.MarkChanged(run.Id, [s2.Id]);
                    markedDuringS1 = h.Jobs.ChangedSteps(run.Id).Contains(s2.Id);
                }
                else
                {
                    markedDuringS2 = h.Jobs.ChangedSteps(run.Id).Contains(s2.Id);
                }
                return Task.CompletedTask;
            },
        };

        await h.RunAsync(run, exec);

        Assert.True(markedDuringS1);
        Assert.False(markedDuringS2);
    }

    [Fact]
    public async Task WithoutAJobStore_TheLoopIsUnchanged()
    {
        using var h = new Harness();
        var run = await h.PlannedRunAsync("s1", "s2");
        var exec = new ScriptedExecutor();

        await new AgentRunOrchestrator(h.Runs, new NoPlanner(), new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance)
            .RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, Ct, resume: true, nudge: "n");

        Assert.Equal(["s1", "s2"], exec.Executed);
        Assert.All(exec.Nudges, n => Assert.Equal("n", n));
    }
}
