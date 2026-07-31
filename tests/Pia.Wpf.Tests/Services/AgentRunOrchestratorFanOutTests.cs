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
/// Batch 07 D7 — the fan-out. A plan step marked with a <c>parallelGroup</c> is not executed in-process: the
/// whole group is dispatched as sibling CHILD runs on a SEPARATE concurrency pool and awaited, so siblings run
/// in parallel while the parent waits. These facts drive the real <see cref="AgentRunOrchestrator"/> against a
/// real SQLite <see cref="AgentRunService"/>, with a launcher double that creates real child run ROWS — because
/// every decision the loop makes after the wait is read off those rows and not off the handle.
/// <para>
/// The parent stays <c>Running</c> for the whole wait on this tree: the persisted <c>WaitingForChildren</c>
/// state and its two CAS members are a separate group that has not landed. The cancellation check after the
/// wait stands in for that CAS and has its own fact below.
/// </para>
/// </summary>
public sealed class AgentRunOrchestratorFanOutTests
{
    private static Persona Persona() => new() { Name = "Pia", SystemPrompt = "sys" };

    private static AiProvider Provider() => new() { Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };

    private static StepTurnResult Ok(string text = "done") =>
        new(true, false, null, text, null, Guid.NewGuid(), Guid.NewGuid());

    /// <summary>A plan whose steps carry the <c>{"parallelGroup":N}</c> marker the planner writes — the same
    /// document shape, produced through <see cref="JsonSerializer"/> rather than hand-typed, so a producer
    /// change cannot leave these facts asserting a stale spelling.</summary>
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

        /// <summary>What this executor publishes onto <c>ctx.WorkspaceRoot</c> in <c>BeginRunAsync</c>, exactly
        /// as both real executors do — and therefore the root the fan-out hands its children.</summary>
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

    /// <summary>What one <c>LaunchChildAsync</c> call was asked for, plus the row it created.</summary>
    private sealed record Dispatch(
        HeadlessRunRequest Request, Guid ParentRunId, string? ParentPolicyJson, string? ParentWorkspaceRoot,
        Guid ChildRunId, Guid ChatId, Task Completion);

    /// <summary>
    /// Launcher double that behaves like the real one in the two ways these facts depend on: it creates a REAL
    /// child run row (with <c>ParentRunId</c> set, through the real service, so <c>GetChildRunsAsync</c> and the
    /// per-child re-read see it) and its <c>Completion</c> settles on a budget PAUSE as well as on a terminal
    /// state — which is the whole reason the parent must re-read the row instead of trusting the handle.
    /// </summary>
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

        /// <summary>Held children: every child waits on this before settling, so a test can observe the parent
        /// inside the wait. Null ⇒ settle immediately.</summary>
        public TaskCompletionSource? Gate { get; set; }

        /// <summary>Signalled once every dispatched child has entered its wait.</summary>
        public TaskCompletionSource AllDispatched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>How many children must enter the wait before <see cref="AllDispatched"/> fires.</summary>
        public int ExpectedChildren { get; set; } = int.MaxValue;

        /// <summary>The state each child settles in, in dispatch order; the default is Completed.</summary>
        public Queue<AgentRunState> Outcomes { get; } = new();

        /// <summary>Tokens each child accrues into its OWN ledger before settling — the roll-up's input.</summary>
        public UsageDetails? ChildUsage { get; set; }

        /// <summary>The assistant answer written into each child's chat, or null to write none.</summary>
        public string? ChildAnswer { get; set; }

        /// <summary>When set, <c>LaunchChildAsync</c> throws for the CALL at this index (0-based). Counted per
        /// call and not per successful dispatch, or one configured fault would repeat for every later sibling.</summary>
        public int? ThrowForIndex { get; set; }

        private int _entered;
        private int _calls;

        public async Task<HeadlessRunHandle> LaunchChildAsync(
            HeadlessRunRequest req, Guid parentRunId, string? parentPolicyJson, string? parentWorkspaceRoot,
            CancellationToken ct = default)
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
            Dispatches.Add(new Dispatch(req, parentRunId, parentPolicyJson, parentWorkspaceRoot, child.Id, chatId, completion));
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
            Runs.Dispose();
            Ctx.Dispose();
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// T-FAN-1, <b>REGRESSION</b>. The headline: two steps sharing a <c>parallelGroup</c> are dispatched as
    /// child runs instead of executed in-process, both are awaited, both settle Done, and the run completes
    /// with its ordinary sequential step still running in-process.
    /// <para>
    /// The <c>DoesNotContain</c> legs are the discriminating half: without them a loop that ran the group
    /// in-process AND also dispatched children would pass every other assertion here.
    /// </para>
    /// </summary>
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

        // The delegated steps ran ELSEWHERE; only the sequential one ran in-process.
        Assert.Equal(["c"], exec.Executed);
        Assert.DoesNotContain("a", exec.Executed);
        Assert.DoesNotContain("b", exec.Executed);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.All(final.Plan, s => Assert.Equal(AgentStepStatus.Done, s.Status));

        // No orphans (D16): the parent cannot leave the wait while a child's dispatch task is still live.
        Assert.All(launcher.Dispatches, d => Assert.True(d.Completion.IsCompleted));
    }

    /// <summary>
    /// T-FAN-2, <b>GUARD</b>. No launcher injected ⇒ no delegation, ever. This is what keeps every existing
    /// orchestrator fact — all of which construct the type positionally without a launcher — asserting the
    /// pre-Batch-07 loop, and it is the shape a build whose DI never registered a launcher would run in.
    /// </summary>
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

    /// <summary>
    /// T-FAN-3, <b>GUARD</b> (D11). A group of ONE is not a fan-out. Without this a model that stamped
    /// <c>parallelGroup: 1</c> on every step of a linear plan would turn it into N sequential child runs — all
    /// of delegation's cost, none of its parallelism.
    /// </summary>
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

    /// <summary>
    /// T-FAN-4, <b>REGRESSION</b> (§7.5's depth guard). A run that is ITSELF a child never delegates. One line,
    /// and it is what bounds the wall clock, the child-pool pressure and the scheduled-job <c>_runLock</c> hold
    /// to a single level — without it a plan shaped like a tree multiplies R15 by its depth.
    /// </summary>
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
        Assert.Equal(["a", "b"], exec.Executed); // the group ran in-process instead
    }

    /// <summary>
    /// T-FAN-5, <b>REGRESSION</b> (D15). The PERSISTED ledger nests: each settled child's token totals are
    /// pushed into the parent's run-level ledger exactly once. The per-step half is the discriminating one —
    /// the push is <c>stepId: null</c> on purpose, because the parent ran no turn for that step and a per-step
    /// entry would claim it spent tokens it never did.
    /// </summary>
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

        // Run-level, not per-step: no ledger entry exists for either delegated step.
        var perStep = ledger.GetProperty("perStep");
        Assert.Equal(0, perStep.GetArrayLength());
    }

    /// <summary>
    /// T-FAN-6, <b>REGRESSION</b> (§0.9/D13). <c>HeadlessRunHandle.Completion</c> settles on a budget PAUSE too,
    /// so a parent that treated it as terminality would mark the step Done, roll up a PARTIAL ledger and carry
    /// on while the child sat parked and resumable. The step stays Pending, the parent re-parks itself, and
    /// nothing is rolled up.
    /// </summary>
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

    /// <summary>
    /// T-FAN-7, <b>REGRESSION</b>. A failed child is an ordinary failed step: it feeds the SHARED replan budget
    /// through the same branch an in-process failure takes (the extraction that made both paths share one copy
    /// of it is what this pins). The replan's failure reason names the delegation, so the model is told what
    /// actually happened.
    /// </summary>
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

    /// <summary>
    /// T-FAN-8, <b>REGRESSION</b> (D16). Cancellation cascades through the EXISTING linked CTS and leaves no
    /// orphan. Three claims in one, because they are only meaningful together: every dispatched child is
    /// cancelled, the parent does NOT return until every child's dispatch task has unwound, and the parent
    /// settles <c>Cancelled</c> rather than resurrecting itself with the next blind state write.
    /// <para>
    /// That last one is the check standing in for the missing <c>TryEndChildWaitAsync</c> CAS. Remove it and
    /// the drain loop's next <c>SetStateAsync(Running)</c> flips this run back out of a terminal state.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// T-FAN-9, <b>REGRESSION</b>. A launcher fault dispatching sibling 2 of 3 fails THAT step and still awaits
    /// the sibling already in flight. The alternative — letting the throw escape the dispatch loop — leaves a
    /// dispatched child unawaited, which is exactly the orphan D16 rules out.
    /// </summary>
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

    /// <summary>
    /// T-FAN-10, <b>REGRESSION</b>. The stale-generation cancel. A parent that re-parked because a child parked
    /// arrives here again with the same Pending group, and nothing links a child to a STEP — so without this the
    /// previous generation would sit parked forever, each child owning a visible stub chat, because states at or
    /// above <c>WaitingForInput</c> are deliberately never swept. <c>CancelAsync</c> alone cannot fix it: a child
    /// parked in a PREVIOUS process is not in the in-flight map, which is the case this seeds.
    /// </summary>
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

    /// <summary>
    /// T-FAN-11, <b>GUARD</b>. What a child is actually asked for: its parent's id, its parent's grant envelope
    /// (never the launch default — the launcher narrows it), its parent's WORKSPACE ROOT rather than one of its
    /// own, no <c>TriggerRef</c>, no request-level grants, and a HALVED wall clock (R15 — the scheduled-job lock
    /// is held for the parent's wall clock plus every descendant's).
    /// </summary>
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
    }

    /// <summary>
    /// T-FAN-12, <b>REGRESSION</b>. The marker reader degrades to "sequential" on every unreadable shape and
    /// never throws — the same swallowing discipline <c>ReadTruncation</c> follows. The last row is the
    /// non-vacuity control: a reader that returned null for everything would pass the other rows for free.
    /// </summary>
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

    /// <summary>
    /// T-FAN-13, <b>REGRESSION</b>, and the highest-severity fact in this file. A child run reaches its own
    /// terminal settle, so without the guard inside <c>SafePromote</c> it would consume its parent's single
    /// allowed promotion — and worse, <c>SafePromote</c> TEARS THE WORKSPACE DOWN after a successful promote, so
    /// the first sibling to finish would delete the directory its still-running siblings are writing into.
    /// <para>
    /// Driven on the <c>PlanResult.Fallback</c> degrade arm, which returns early and settles in the opposite
    /// order to the main path — the arm a guard wrapped around the two call sites would most easily miss.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// T-FAN-14, <b>REGRESSION</b>. The parent's critic and any replan must SEE what the children produced.
    /// Without the chat read a completed delegated step carries empty visible text and the verifier judges the
    /// whole goal on nothing — the same failure mode <c>CompletedStepSummary.FromEarlierSegment</c> exists for.
    /// </summary>
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

    /// <summary>
    /// T-FAN-15, <b>GUARD</b> and the counterpart to the fact above: with no chat service the fan-out still
    /// works, and the step says the work ran elsewhere instead of implying it produced nothing. The chat
    /// dependency is trailing and defaulted, so this is the shape a caller that omits it runs in.
    /// </summary>
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
}
