using System.IO;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

public sealed class AgentRunOrchestratorFanOutTests
{
    private static Persona Persona() => new() { Name = "Pia", SystemPrompt = "sys" };

    private static AiProvider Provider() => new() { Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };

    private static StepTurnResult Ok(string text = "done") =>
        new(true, false, null, text, null, Guid.NewGuid(), Guid.NewGuid());

    // The parallelGroup marker is serialized, not hand-typed, so a producer change cannot leave a stale spelling here.
    private static List<AgentStep> MakeSteps(params (string Intent, int? Group)[] steps)
    {
        var result = new List<AgentStep>();
        for (var i = 0; i < steps.Length; i++)
        {
            result.Add(new AgentStep
            {
                Id = Guid.Empty,
                Ordinal = i,
                Title = steps[i].Intent,
                Intent = steps[i].Intent,
                Status = AgentStepStatus.Pending,
                ExtraJson = steps[i].Group is { } g ? Extras(g) : null,
            });
        }

        return result;
    }

    private static string Extras(int group) =>
        JsonSerializer.Serialize(new { parallelGroup = group }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    private sealed class FakePlanner : IAgentPlanner
    {
        public Queue<PlanResult> Plans { get; } = new();

        public Queue<PlanResult> Replans { get; } = new();

        public int ReplanCalls { get; private set; }

        public List<string?> ReplanFailures { get; } = new();

        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(Plans.Count > 0 ? Plans.Dequeue() : PlanResult.Fallback);

        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
        {
            ReplanCalls++;
            ReplanFailures.Add(failure);
            return Task.FromResult(Replans.Count > 0 ? Replans.Dequeue() : PlanResult.Fallback);
        }
    }

    private sealed class RecordingExecutor : IAgentTurnExecutor
    {
        public List<string> Executed { get; } = new();

        public bool PausedCalled { get; private set; }

        public string? WorkspaceRoot { get; set; }

        public Task BeginRunAsync(AgentRun run, RunContext ctx, CancellationToken ct)
        {
            ctx.WorkspaceRoot = WorkspaceRoot;
            return Task.CompletedTask;
        }

        public Task<StepTurnResult> ExecuteStepAsync(AgentRun run, AgentStep step, RunContext ctx, CancellationToken ct)
        {
            Executed.Add(step.Intent ?? step.Title);
            return Task.FromResult(Ok());
        }

        public Task<StepTurnResult> RunSingleTurnFallbackAsync(AgentRun run, RunContext ctx, CancellationToken ct)
            => Task.FromResult(Ok("fallback"));

        public Task EndRunAsync(AgentRun run, RunContext ctx, bool cancelled, bool failed, CancellationToken ct)
            => Task.CompletedTask;

        public Task OnPausedAsync(AgentRun run, RunContext ctx, CancellationToken ct)
        {
            PausedCalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed record Dispatch(
        HeadlessRunRequest Request, Guid ParentRunId, string? ParentPolicyJson, string? ParentWorkspaceRoot,
        Guid? PersonaId, Guid ChildRunId, Guid ChatId, Task Completion);

    // Like the real launcher, Completion settles on a budget pause too, not only on a terminal state — which is
    // why the parent must re-read the row instead of trusting the handle.
    private sealed class FakeChildLauncher : IHeadlessRunLauncher
    {
        private readonly AgentRunService _runs;
        private readonly AssistantChatService _chats;

        public FakeChildLauncher(AgentRunService runs, AssistantChatService chats)
        {
            _runs = runs;
            _chats = chats;
        }

        public List<Dispatch> Dispatches { get; } = new();

        public List<Guid> Cancelled { get; } = new();

        // Every child waits on this before settling, so a test can observe the parent inside the wait; null ⇒ settle at once.
        public TaskCompletionSource? Gate { get; set; }

        public TaskCompletionSource AllDispatched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ExpectedChildren { get; set; } = int.MaxValue;

        public Queue<AgentRunState> Outcomes { get; } = new();

        public UsageDetails? ChildUsage { get; set; }

        // Per-step ledger entries each child writes; zero by default so every other fact here is untouched.
        public int ChildStepEntries { get; set; }

        public string? ChildAnswer { get; set; }

        // Counted per CALL, not per successful dispatch, or one configured fault would repeat for every later sibling.
        public int? ThrowForIndex { get; set; }

        private int _entered;
        private int _calls;

        public async Task<HeadlessRunHandle> LaunchChildAsync(
            HeadlessRunRequest req, Guid parentRunId, string? parentPolicyJson, string? parentWorkspaceRoot,
            Guid? personaId = null, CancellationToken ct = default)
        {
            if (ThrowForIndex == _calls++)
                throw new InvalidOperationException("launcher boom");

            var chatId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            await _chats.SaveAsync(new SyncAssistantChat
            {
                Id = chatId,
                SchemaVersion = 1,
                Title = "child",
                CreatedAt = now,
                UpdatedAt = now,
                LastAccessedAt = now,
                WindowMode = WindowMode.Assistant.ToString(),
                Messages = ChildAnswer is null
                    ? []
                    :
                    [
                        new SyncAssistantChatMessage { Id = Guid.NewGuid(), Role = "user", Content = "go", Timestamp = now },
                        new SyncAssistantChatMessage { Id = Guid.NewGuid(), Role = "assistant", Content = ChildAnswer, Timestamp = now },
                    ],
            }, ct);

            var child = await _runs.CreateAsync(new AgentRunCreateRequest(
                chatId, RunShape.Planned, req.Trigger, req.TriggerRef, req.OwnerDeviceId, Goal: req.Goal,
                PolicyJson: parentPolicyJson, ParentRunId: parentRunId), ct);

            var outcome = Outcomes.Count > 0 ? Outcomes.Dequeue() : AgentRunState.Completed;
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _cancels[child.Id] = cancelled;

            var completion = SettleAsync(child.Id, outcome, cancelled);
            Dispatches.Add(new Dispatch(req, parentRunId, parentPolicyJson, parentWorkspaceRoot, personaId, child.Id, chatId, completion));
            return new HeadlessRunHandle(child.Id, chatId, completion);
        }

        private readonly Dictionary<Guid, TaskCompletionSource> _cancels = new();

        private async Task SettleAsync(Guid childRunId, AgentRunState outcome, TaskCompletionSource cancelled)
        {
            if (Interlocked.Increment(ref _entered) >= ExpectedChildren)
                AllDispatched.TrySetResult();

            if (Gate is { } gate)
                await Task.WhenAny(gate.Task, cancelled.Task);

            if (ChildUsage is not null)
                await _runs.AddUsageAsync(childRunId, null, ChildUsage, CancellationToken.None);

            for (var i = 0; i < ChildStepEntries; i++)
            {
                await _runs.AddUsageAsync(
                    childRunId, Guid.NewGuid(), new UsageDetails { InputTokenCount = 1, OutputTokenCount = 1 },
                    CancellationToken.None);
            }

            if (cancelled.Task.IsCompleted)
            {
                await _runs.FailAsync(childRunId, null, cancelled: true, CancellationToken.None);
                return;
            }

            switch (outcome)
            {
                case AgentRunState.Completed:
                    await _runs.CompleteAsync(childRunId, ct: CancellationToken.None);
                    break;
                case AgentRunState.Failed:
                    await _runs.FailAsync(childRunId, "child boom", cancelled: false, CancellationToken.None);
                    break;
                case AgentRunState.Cancelled:
                    await _runs.FailAsync(childRunId, null, cancelled: true, CancellationToken.None);
                    break;
                case AgentRunState.WaitingForInput:
                    await _runs.PauseAsync(childRunId, "step-cap", CancellationToken.None);
                    break;
                default:
                    await _runs.SetStateAsync(childRunId, outcome, CancellationToken.None);
                    break;
            }
        }

        public Task CancelAsync(Guid runId)
        {
            Cancelled.Add(runId);
            if (_cancels.TryGetValue(runId, out var tcs))
                tcs.TrySetResult();
            return Task.CompletedTask;
        }

        public Task<HeadlessRunHandle> LaunchAsync(HeadlessRunRequest req, CancellationToken ct = default)
            => throw new NotSupportedException("the fan-out never launches a top-level run");

        public Task StopAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task RunStartupSweepAsync(CancellationToken ct) => throw new NotSupportedException();

#pragma warning disable CS0067 // never raised: this double dispatches CHILDREN and has no resume path
        public event EventHandler<ResumedRunSettledEventArgs>? ResumedRunSettled;
#pragma warning restore CS0067
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _dir;

        public Harness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "PiaFanOut_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            RunsBase = Path.Combine(_dir, "runs");
            Directory.CreateDirectory(RunsBase);
            Ctx = new SqliteContext(Path.Combine(_dir, "history.db"));
            Runs = new AgentRunService(Ctx, NullLogger<AgentRunService>.Instance);
            Chats = new AssistantChatService(Ctx, Runs);
        }

        public SqliteContext Ctx { get; }

        public AgentRunService Runs { get; }

        public AssistantChatService Chats { get; }

        public string RunsBase { get; }

        public async Task<AgentRun> NewRunAsync(string goal, Guid? parentRunId = null, string? policyJson = null)
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
            return await Runs.CreateAsync(new AgentRunCreateRequest(
                chatId, RunShape.Planned, AgentRunTrigger.User, Goal: goal, PolicyJson: policyJson,
                ParentRunId: parentRunId));
        }

        public AgentRunOrchestrator BuildOrchestrator(
            IAgentPlanner planner, IAgentVerifier? verifier = null, IRunWorkspaceService? workspaces = null,
            IHeadlessRunLauncher? childLauncher = null, IAssistantChatService? chats = null) =>
            new(Runs, planner, verifier ?? new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance,
                workspaces, childLauncher, chats);

        public void Dispose()
        {
            Chats.Dispose();
            Runs.Dispose();
            Ctx.Dispose();
            TempPath.Remove(_dir);
        }
    }

    [Fact]
    public async Task AParallelGroupIsDispatchedAsChildRuns_AwaitedAndMarkedDone()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var launcher = new FakeChildLauncher(h.Runs, h.Chats);
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("a", 1), ("b", 1), ("c", null)), false));
        var exec = new RecordingExecutor();

        await h.BuildOrchestrator(planner, childLauncher: launcher).RunAsync(
            run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.Equal(2, launcher.Dispatches.Count);
        Assert.All(launcher.Dispatches, d => Assert.Equal(run.Id, d.ParentRunId));

        Assert.Equal(["c"], exec.Executed);
        Assert.DoesNotContain("a", exec.Executed);
        Assert.DoesNotContain("b", exec.Executed);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.All(final.Plan, s => Assert.Equal(AgentStepStatus.Done, s.Status));

        // No orphans: the parent cannot leave the wait while a child's dispatch task is still live.
        Assert.All(launcher.Dispatches, d => Assert.True(d.Completion.IsCompleted));
    }

    [Fact]
    public async Task WithNoLauncher_AParallelGroupRunsInProcess()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("a", 1), ("b", 1)), false));
        var exec = new RecordingExecutor();

        await h.BuildOrchestrator(planner).RunAsync(
            run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.Equal(["a", "b"], exec.Executed);
        Assert.Equal(AgentRunState.Completed, (await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken))!.State);
    }

    // A group of one is not a fan-out: otherwise a linear plan stamped parallelGroup on every step becomes N
    // sequential child runs — all of delegation's cost, none of its parallelism.
    [Fact]
    public async Task AGroupOfOneRunsInProcess()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var launcher = new FakeChildLauncher(h.Runs, h.Chats);
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("a", 1), ("b", 2)), false)); // two groups of one
        var exec = new RecordingExecutor();

        await h.BuildOrchestrator(planner, childLauncher: launcher).RunAsync(
            run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.Empty(launcher.Dispatches);
        Assert.Equal(["a", "b"], exec.Executed);
    }

    // A child never delegates further: that depth guard is what bounds wall clock, child-pool pressure and the
    // scheduled-job lock hold to a single level.
    [Fact]
    public async Task AChildRunNeverDelegatesFurther()
    {
        using var h = new Harness();
        var parent = await h.NewRunAsync("parent");
        var child = await h.NewRunAsync("child", parentRunId: parent.Id);
        var launcher = new FakeChildLauncher(h.Runs, h.Chats);
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("a", 1), ("b", 1)), false));
        var exec = new RecordingExecutor();

        await h.BuildOrchestrator(planner, childLauncher: launcher).RunAsync(
            child, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.Empty(launcher.Dispatches);
        Assert.Equal(["a", "b"], exec.Executed);
    }

    [Fact]
    public async Task ASettledChildsTokensRollUpIntoTheParentsLedger_RunLevelOnly()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var launcher = new FakeChildLauncher(h.Runs, h.Chats)
        {
            ChildUsage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 7 },
        };
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("a", 1), ("b", 1)), false));

        await h.BuildOrchestrator(planner, childLauncher: launcher).RunAsync(
            run, new RecordingExecutor(), Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        var ledger = JsonDocument.Parse(final!.LedgerJson!).RootElement;
        Assert.Equal(200, ledger.GetProperty("inputTokens").GetInt64());  // both children, once each
        Assert.Equal(14, ledger.GetProperty("outputTokens").GetInt64());

        // Each child still holds its own truth (the roll-up is an aggregate convenience, not a move).
        var children = await h.Runs.GetChildRunsAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(2, children.Count);
        Assert.All(children, c =>
            Assert.Equal(100, JsonDocument.Parse(c.LedgerJson!).RootElement.GetProperty("inputTokens").GetInt64()));

        // Run-level, not per-step: the parent ran no turn for a delegated step, so a per-step entry would claim
        // tokens it never spent.
        var perStep = ledger.GetProperty("perStep");
        Assert.Equal(0, perStep.GetArrayLength());
    }

    [Fact]
    public async Task AParkedChildLeavesItsStepPending_AndReParksTheParent_WithNoRollUp()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var launcher = new FakeChildLauncher(h.Runs, h.Chats)
        {
            ChildUsage = new UsageDetails { InputTokenCount = 40, OutputTokenCount = 4 },
        };
        launcher.Outcomes.Enqueue(AgentRunState.Completed);
        launcher.Outcomes.Enqueue(AgentRunState.WaitingForInput);
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("a", 1), ("b", 1)), false));
        var exec = new RecordingExecutor();

        await h.BuildOrchestrator(planner, childLauncher: launcher).RunAsync(
            run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.WaitingForInput, final!.State); // re-parked, resumable, NOT failed
        Assert.True(exec.PausedCalled);                            // the non-terminal executor release fired

        // The parked child's step is left Pending so a resume re-dispatches it; the completed one is Done.
        Assert.Equal(AgentStepStatus.Done, final.Plan.Single(s => s.Title == "a").Status);
        Assert.Equal(AgentStepStatus.Pending, final.Plan.Single(s => s.Title == "b").Status);

        // Only the COMPLETED child was rolled up — the parked one will be counted when it settles terminally,
        // which is what stops a resumed child being billed to its parent twice.
        var ledger = JsonDocument.Parse(final.LedgerJson!).RootElement;
        Assert.Equal(40, ledger.GetProperty("inputTokens").GetInt64());
    }

    [Fact]
    public async Task AFailedChildFeedsTheOrdinaryReplanPath()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var launcher = new FakeChildLauncher(h.Runs, h.Chats);
        launcher.Outcomes.Enqueue(AgentRunState.Completed);
        launcher.Outcomes.Enqueue(AgentRunState.Failed);
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("a", 1), ("b", 1)), false));
        planner.Replans.Enqueue(new PlanResult(MakeSteps(("recovered", null)), false));
        var exec = new RecordingExecutor();

        await h.BuildOrchestrator(planner, childLauncher: launcher).RunAsync(
            run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.Equal(1, planner.ReplanCalls);
        Assert.Contains("delegated run failed", Assert.Single(planner.ReplanFailures));
        Assert.Equal(["recovered"], exec.Executed);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        // The Done sibling survived the replan's KeepDone pass.
        Assert.Contains(final.Plan, s => s.Title == "a" && s.Status == AgentStepStatus.Done);
    }

    [Fact]
    public async Task CancellingTheParentCascadesToEveryChild_WithNoOrphanAndNoResurrection()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var launcher = new FakeChildLauncher(h.Runs, h.Chats)
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            ExpectedChildren = 2,
        };
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("a", 1), ("b", 1)), false));

        using var external = new CancellationTokenSource();
        var loop = h.BuildOrchestrator(planner, childLauncher: launcher).RunAsync(
            run, new RecordingExecutor(), Persona(), Provider(), RunProfile.Interactive, external.Token);

        // Wait for both children to be inside their wait — a TaskCompletionSource gate, never a Task.Delay.
        await launcher.AllDispatched.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await external.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(2, launcher.Cancelled.Count);
        Assert.Equal(
            launcher.Dispatches.Select(d => d.ChildRunId).OrderBy(g => g),
            launcher.Cancelled.OrderBy(g => g));
        Assert.All(launcher.Dispatches, d => Assert.True(d.Completion.IsCompleted));

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Cancelled, final!.State);
        Assert.All(
            await h.Runs.GetChildRunsAsync(run.Id, TestContext.Current.CancellationToken),
            c => Assert.Equal(AgentRunState.Cancelled, c.State));
    }

    // The park is asserted off the persisted row, which is the only thing a restart can see: left unparked, the
    // sweep would take a waiting parent to Cancelled.
    [Fact]
    public async Task TwoSiblingsLaunchInParallel_AndTheParentParksInWaitingForChildren()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var launcher = new FakeChildLauncher(h.Runs, h.Chats)
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            ExpectedChildren = 2,
        };
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("a", 1), ("b", 1)), false));

        var loop = h.BuildOrchestrator(planner, childLauncher: launcher).RunAsync(
            run, new RecordingExecutor(), Persona(), Provider(), RunProfile.Interactive,
            TestContext.Current.CancellationToken);

        await launcher.AllDispatched.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Both were dispatched before either settled ⇒ they are genuinely concurrent, not sequential.
        Assert.Equal(2, launcher.Dispatches.Count);
        Assert.All(launcher.Dispatches, d => Assert.False(d.Completion.IsCompleted));

        var parked = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.WaitingForChildren, parked!.State);
        Assert.Null(parked.CompletedAt);
        Assert.All(parked.Plan, s => Assert.Equal(AgentStepStatus.Running, s.Status));
        // Parked ⇒ the work segment is closed; the CAS below re-opens a fresh one.
        Assert.False(JsonDocument.Parse(parked.LedgerJson!).RootElement
            .TryGetProperty("segmentStartedAt", out var seg) && seg.ValueKind != JsonValueKind.Null);

        launcher.Gate!.SetResult();
        await loop.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.All(final.Plan, s => Assert.Equal(AgentStepStatus.Done, s.Status));
    }

    // The external token is deliberately never cancelled here, so only the un-park CAS can stop the loop from
    // writing Completed over a Cancelled row.
    [Fact]
    public async Task WhenTheParentIsNoLongerWaiting_TheLoopStopsInsteadOfResurrectingIt()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var launcher = new FakeChildLauncher(h.Runs, h.Chats)
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            ExpectedChildren = 2,
        };
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("a", 1), ("b", 1), ("c", null)), false));
        var exec = new RecordingExecutor();

        var loop = h.BuildOrchestrator(planner, childLauncher: launcher).RunAsync(
            run, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        await launcher.AllDispatched.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // A DIFFERENT writer takes the run while it is parked. The parent's own token stays live.
        await h.Runs.FailAsync(run.Id, "taken elsewhere", cancelled: true, TestContext.Current.CancellationToken);
        launcher.Gate!.SetResult();
        await loop.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Cancelled, final!.State);   // never Running, never Completed
        Assert.Empty(exec.Executed);                           // "c" was never reached
        Assert.True(exec.PausedCalled);                        // the executor was still released
    }

    // Letting the dispatch throw escape the loop would leave the sibling already in flight unawaited — an orphan.
    [Fact]
    public async Task ADispatchFaultFailsOnlyThatSibling_AndTheOthersAreStillAwaited()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var launcher = new FakeChildLauncher(h.Runs, h.Chats) { ThrowForIndex = 1 };
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("a", 1), ("b", 1), ("c", 1)), false));
        planner.Replans.Enqueue(new PlanResult(MakeSteps(("recovered", null)), false));

        await h.BuildOrchestrator(planner, childLauncher: launcher).RunAsync(
            run, new RecordingExecutor(), Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        // Siblings 1 and 3 were dispatched and awaited; sibling 2 threw.
        Assert.Equal(2, launcher.Dispatches.Count);
        Assert.All(launcher.Dispatches, d => Assert.True(d.Completion.IsCompleted));
        Assert.Equal(1, planner.ReplanCalls);
        Assert.Contains("could not be started", Assert.Single(planner.ReplanFailures));
    }

    // Nothing links a child to a STEP, and parked states are never swept, so without the supersede the previous
    // generation sits parked forever, each child owning a visible stub chat.
    [Fact]
    public async Task APreviousGenerationsParkedChildIsSupersededBeforeANewDispatch()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        // A child of this parent, parked, from a "previous process": the launcher double has never heard of it.
        var stale = await h.NewRunAsync("stale child", parentRunId: run.Id);
        await h.Runs.PauseAsync(stale.Id, "step-cap", TestContext.Current.CancellationToken);

        var launcher = new FakeChildLauncher(h.Runs, h.Chats);
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("a", 1), ("b", 1)), false));

        await h.BuildOrchestrator(planner, childLauncher: launcher).RunAsync(
            run, new RecordingExecutor(), Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.Contains(stale.Id, launcher.Cancelled);
        var settled = await h.Runs.GetAsync(stale.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Cancelled, settled!.State); // the row-level fallback did the work

        // Non-vacuity: the new generation really did dispatch, and it is not the stale run.
        Assert.Equal(2, launcher.Dispatches.Count);
        Assert.DoesNotContain(stale.Id, launcher.Dispatches.Select(d => d.ChildRunId));
    }

    // The wall clock is halved because the scheduled-job lock is held for the parent's wall clock plus every
    // descendant's.
    [Fact]
    public async Task TheChildRequestCarriesTheParentsEnvelopeWorkspaceAndAHalvedWallClock()
    {
        using var h = new Harness();
        const string parentPolicy = """{"v":1,"grantedWrites":["write_file"],"trigger":"User"}""";
        var run = await h.NewRunAsync("goal", policyJson: parentPolicy);
        var launcher = new FakeChildLauncher(h.Runs, h.Chats);
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("a", 1), ("b", 1)), false));
        var workspaceRoot = Path.Combine(h.RunsBase, run.Id.ToString());
        Directory.CreateDirectory(workspaceRoot);
        var exec = new RecordingExecutor { WorkspaceRoot = workspaceRoot };
        var profile = new RunProfile(24, 2, TimeSpan.FromMinutes(45));

        await h.BuildOrchestrator(planner, childLauncher: launcher).RunAsync(
            run, exec, Persona(), Provider(), profile, TestContext.Current.CancellationToken);

        var first = launcher.Dispatches[0];
        Assert.Equal(run.Id, first.ParentRunId);
        Assert.Equal(parentPolicy, first.ParentPolicyJson);
        Assert.Equal(workspaceRoot, first.ParentWorkspaceRoot);
        Assert.Null(first.Request.TriggerRef);
        Assert.Null(first.Request.GrantedWrites);
        Assert.Equal(TimeSpan.FromMinutes(22.5), first.Request.Budget!.WallClock);
        Assert.Equal(profile.MaxSteps, first.Request.Budget.MaxSteps);
        // The goal is the step's own intent, not the parent's goal.
        Assert.Equal("a", first.Request.Goal);
        // No roster assignment on these steps ⇒ nothing to hand over, so the child takes the per-mode persona.
        Assert.Null(first.PersonaId);
    }

    // The two siblings carry DIFFERENT ids, so a dispatch that passed one constant — or null — would fail.
    [Fact]
    public async Task ADelegatedStepsAssignedPersonaReachesItsChildRun_NotJustThePanel()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var researcher = Guid.NewGuid();
        var writer = Guid.NewGuid();
        var steps = MakeSteps(("a", 1), ("b", 1));
        steps[0].AssignedPersonaId = researcher;
        steps[1].AssignedPersonaId = writer;
        var launcher = new FakeChildLauncher(h.Runs, h.Chats);
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(steps, false));

        await h.BuildOrchestrator(planner, childLauncher: launcher).RunAsync(
            run, new RecordingExecutor(), Persona(), Provider(), RunProfile.Interactive,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, launcher.Dispatches.Count);
        Assert.Equal(researcher, launcher.Dispatches[0].PersonaId);
        Assert.Equal(writer, launcher.Dispatches[1].PersonaId);
    }

    // The last row is the non-vacuity control: a reader that returned null for everything passes the rest for free.
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("{not json", null)]
    [InlineData("{}", null)]
    [InlineData("[]", null)]
    [InlineData("""{"parallelGroup":null}""", null)]
    [InlineData("""{"parallelGroup":"1"}""", null)]
    [InlineData("""{"somethingElse":1}""", null)]
    [InlineData("""{"parallelGroup":2}""", 2)]
    public void TheParallelGroupMarkerDegradesToSequential(string? extraJson, int? expected)
        => Assert.Equal(expected, AgentRunOrchestrator.ParallelGroupOf(new AgentStep { ExtraJson = extraJson }));

    // A settled sibling reports no outcome at all, so the write that lands it must leave the marker alone —
    // fan-out rows are the only ones that reliably carry one.
    [Fact]
    public async Task FanOut_SettledSibling_WritesNoArtifactRef_AndLeavesTheParallelGroupMarkerIntact()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var launcher = new FakeChildLauncher(h.Runs, h.Chats);
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("a", 1), ("b", 1)), false));

        await h.BuildOrchestrator(planner, childLauncher: launcher).RunAsync(
            run, new RecordingExecutor(), Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.All(final!.Plan, s => Assert.Equal(Extras(1), s.ExtraJson));
        Assert.All(final.Plan, s => Assert.Null(StepExtraJson.ArtifactRefOf(s)));
    }

    // Promotion tears the workspace down afterwards, so an unguarded child would delete the directory its
    // still-running siblings are writing into.
    [Fact]
    public async Task AChildRunNeverPromotesAndNeverTearsDownTheSharedWorkspace()
    {
        using var h = new Harness();
        var parent = await h.NewRunAsync("parent");
        var child = await h.NewRunAsync("child", parentRunId: parent.Id);
        var workspaces = new FakeRunWorkspaceService(h.RunsBase)
        {
            PromoteResult = new RunPromotionResult(RunWorkspaceMode.Copy, 3, 0, 0, null),
        };
        var root = Path.Combine(h.RunsBase, parent.Id.ToString());
        Directory.CreateDirectory(root);
        var exec = new RecordingExecutor { WorkspaceRoot = root };

        // The degrade arm: an empty Plans queue makes PlanAsync return PlanResult.Fallback.
        await h.BuildOrchestrator(new FakePlanner(), workspaces: workspaces).RunAsync(
            child, exec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.Empty(workspaces.Promoted);
        Assert.Empty(workspaces.TornDown);
        Assert.True(Directory.Exists(root), "a child must not delete the workspace its siblings are writing");
        Assert.Equal(AgentRunState.Completed, (await h.Runs.GetAsync(child.Id, TestContext.Current.CancellationToken))!.State);

        // Non-vacuity control: the SAME arm, the same fake, a run with no parent — which DOES promote and DOES
        // tear down. Without this the assertions above would pass on a promotion that had simply stopped working.
        var lone = await h.NewRunAsync("lone");
        var loneRoot = Path.Combine(h.RunsBase, lone.Id.ToString());
        Directory.CreateDirectory(loneRoot);
        await h.BuildOrchestrator(new FakePlanner(), workspaces: workspaces).RunAsync(
            lone, new RecordingExecutor { WorkspaceRoot = loneRoot }, Persona(), Provider(),
            RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.Equal([lone.Id], workspaces.Promoted);
        Assert.Equal([lone.Id], workspaces.TornDown);
    }

    // Today one early return inside SafePromote serves both arms, so this cannot diverge from the fact above; it
    // exists for the change that moves the guard out to the two call sites and gets only one of them right.
    [Fact]
    public async Task AChildRunNeverPromotes_OnTheMainTerminalArmEither()
    {
        using var h = new Harness();
        var parent = await h.NewRunAsync("parent");
        var child = await h.NewRunAsync("child", parentRunId: parent.Id);
        var workspaces = new FakeRunWorkspaceService(h.RunsBase)
        {
            PromoteResult = new RunPromotionResult(RunWorkspaceMode.Copy, 3, 0, 0, null),
        };
        var root = Path.Combine(h.RunsBase, parent.Id.ToString());
        Directory.CreateDirectory(root);

        // A real plan (no parallelGroup, no launcher) so the drain loop runs the step and reaches the terminal
        // settle block — NOT the early-returning degrade arm.
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("a", null)), false));

        await h.BuildOrchestrator(planner, workspaces: workspaces).RunAsync(
            child, new RecordingExecutor { WorkspaceRoot = root }, Persona(), Provider(),
            RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.Empty(workspaces.Promoted);
        Assert.Empty(workspaces.TornDown);
        Assert.True(Directory.Exists(root), "a child must not delete the workspace its siblings are writing");

        // Non-vacuity, on THIS arm: the same plan shape with no parent does promote and does tear down, which is
        // also the proof that the run really drained through the main terminal settle rather than degrading.
        var lone = await h.NewRunAsync("lone");
        var loneRoot = Path.Combine(h.RunsBase, lone.Id.ToString());
        Directory.CreateDirectory(loneRoot);
        var lonePlanner = new FakePlanner();
        lonePlanner.Plans.Enqueue(new PlanResult(MakeSteps(("a", null)), false));
        var loneExec = new RecordingExecutor { WorkspaceRoot = loneRoot };

        await h.BuildOrchestrator(lonePlanner, workspaces: workspaces).RunAsync(
            lone, loneExec, Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.Equal(["a"], loneExec.Executed); // the step really ran in-process: the main arm, not the fallback
        Assert.Equal([lone.Id], workspaces.Promoted);
        Assert.Equal([lone.Id], workspaces.TornDown);
    }

    // Without the chat read a completed delegated step carries empty visible text and the verifier judges the
    // whole goal on nothing.
    [Fact]
    public async Task ASettledChildsAnswerReachesTheParentsCriticAndReplan()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var launcher = new FakeChildLauncher(h.Runs, h.Chats) { ChildAnswer = "the analysis says yes" };
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("a", 1), ("b", 1)), false));
        var verifier = new FakeVerifier();

        await h.BuildOrchestrator(planner, verifier, childLauncher: launcher, chats: h.Chats).RunAsync(
            run, new RecordingExecutor(), Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        var judged = Assert.Single(verifier.SeenCompletedSteps);
        Assert.Equal(2, judged.Count);
        Assert.All(judged, s => Assert.Equal("the analysis says yes", s.VisibleText));
        Assert.All(judged, s => Assert.True(s.Succeeded));
    }

    // The chat dependency is trailing and defaulted, so this is the shape a caller that omits it runs in.
    [Fact]
    public async Task WithNoChatService_ADelegatedStepSaysTheWorkRanElsewhere()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var launcher = new FakeChildLauncher(h.Runs, h.Chats) { ChildAnswer = "unreachable" };
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("a", 1), ("b", 1)), false));
        var verifier = new FakeVerifier();

        await h.BuildOrchestrator(planner, verifier, childLauncher: launcher).RunAsync(
            run, new RecordingExecutor(), Persona(), Provider(), RunProfile.Interactive, TestContext.Current.CancellationToken);

        var judged = Assert.Single(verifier.SeenCompletedSteps);
        // The count first: Assert.All over an empty list proves nothing, and a fan-out that never called
        // ctx.RecordStep would hand the critic exactly that.
        Assert.Equal(2, judged.Count);
        Assert.All(judged, s => Assert.Contains("delegated run", s.VisibleText));
        Assert.Equal(AgentRunState.Completed, (await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken))!.State);
    }

    // The enforced budget lives on the ephemeral RunContext, not the persisted ledger, and does NOT nest: a
    // fan-out costs the parent one step per sibling step, and the children's own steps count against their caps.
    [Fact]
    public async Task TheParentsEnforcedBudgetCountsItsOwnStepsOnly_NeverItsChildrens()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var launcher = new FakeChildLauncher(h.Runs, h.Chats)
        {
            ChildUsage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 7 },
            ChildStepEntries = 10, // each child really did execute 10 steps of its own
        };
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("a", 1), ("b", 1), ("c", null)), false));
        var exec = new RecordingExecutor();
        var verifier = new FakeVerifier();

        await h.BuildOrchestrator(planner, verifier, childLauncher: launcher).RunAsync(
            run, exec, Persona(), Provider(),
            // Exactly the three steps this plan owns: one more unit of budget from anywhere and the run parks.
            new RunProfile(MaxSteps: 3, MaxReplans: 2, TimeSpan.FromMinutes(20)),
            TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);   // NOT parked at step-cap
        Assert.False(exec.PausedCalled);                       // and no non-terminal release fired
        Assert.Equal(["c"], exec.Executed);                    // the sequential step still got its in-process turn
        Assert.All(final.Plan, s => Assert.Equal(AgentStepStatus.Done, s.Status));

        // The parent's budget saw three steps — its own. Read off the slice the critic was handed rather than
        // off the private counter, and asserted as a COUNT first so the Assert.All below cannot pass vacuously.
        var judged = Assert.Single(verifier.SeenCompletedSteps);
        Assert.Equal(3, judged.Count);

        // Non-vacuity for the premise: the children really did persist the step counts a nesting implementation
        // would have had to read. Without this leg the fact above holds trivially for children that ran nothing.
        var children = await h.Runs.GetChildRunsAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(2, children.Count);
        Assert.All(children, c => Assert.Equal(
            10, JsonDocument.Parse(c.LedgerJson!).RootElement.GetProperty("perStep").GetArrayLength()));

        // And the OTHER budget did nest, in the same run: the tokens landed on the parent.
        var ledger = JsonDocument.Parse(final.LedgerJson!).RootElement;
        Assert.True(ledger.GetProperty("inputTokens").GetInt64() >= 200);
    }
}
