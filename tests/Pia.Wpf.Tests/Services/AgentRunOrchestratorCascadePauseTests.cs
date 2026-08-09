using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>A cascade never fires a fan-out parent's own token: the fan-out reads that as a terminal cancel.</summary>
public sealed class AgentRunOrchestratorCascadePauseTests
{
    private static Persona Persona() => new() { Name = "Pia", SystemPrompt = "sys" };

    private static AiProvider Provider() => new() { Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };

    /// <summary>Serialized rather than hand-typed, so a producer change cannot leave a stale spelling here.</summary>
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
                ExtraJson = steps[i].Group is { } g
                    ? JsonSerializer.Serialize(new { parallelGroup = g },
                        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
                    : null,
            });
        }

        return result;
    }

    private sealed class FakePlanner : IAgentPlanner
    {
        public Queue<PlanResult> Plans { get; } = new();

        public int ReplanCalls { get; private set; }

        /// <summary>A sibling's error text lives only in ctx, so this is the only copy a test can read.</summary>
        public List<string?> ReplanFailures { get; } = new();

        /// <summary>By the time this signals, the row already reads <c>Planning</c>.</summary>
        public TaskCompletionSource? PlanEntered { get; set; }

        /// <summary>Awaited with the dispatch token, so a cancel throws out of the plan turn.</summary>
        public Task? PlanGate { get; set; }

        public async Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
        {
            PlanEntered?.TrySetResult();
            if (PlanGate is { } gate)
                await gate.WaitAsync(ct);

            return Plans.Count > 0 ? Plans.Dequeue() : PlanResult.Fallback;
        }

        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
        {
            ReplanCalls++;
            ReplanFailures.Add(failure);
            return Task.FromResult(PlanResult.Fallback);
        }
    }

    /// <summary>The PARENT's executor: it only ever runs the plan's ordinary (non-delegated) steps.</summary>
    private sealed class ParentExecutor : IAgentTurnExecutor
    {
        public List<string> Executed { get; } = new();

        public bool PausedCalled { get; private set; }

        public bool EndCalled { get; private set; }

        public bool EndCancelled { get; private set; }

        public Task BeginRunAsync(AgentRun run, RunContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task<StepTurnResult> ExecuteStepAsync(AgentRun run, AgentStep step, RunContext ctx, CancellationToken ct)
        {
            Executed.Add(step.Intent ?? step.Title);
            return Task.FromResult(new StepTurnResult(true, false, null, "done", null, Guid.NewGuid(), Guid.NewGuid()));
        }

        public Task<StepTurnResult> RunSingleTurnFallbackAsync(AgentRun run, RunContext ctx, CancellationToken ct)
            => Task.FromResult(new StepTurnResult(true, false, null, "fallback", null, Guid.NewGuid(), Guid.NewGuid()));

        public Task EndRunAsync(AgentRun run, RunContext ctx, bool cancelled, bool failed, CancellationToken ct)
        {
            EndCalled = true;
            EndCancelled = cancelled;
            return Task.CompletedTask;
        }

        public Task OnPausedAsync(AgentRun run, RunContext ctx, CancellationToken ct)
        {
            PausedCalled = true; // the non-terminal release — never EndRunAsync
            return Task.CompletedTask;
        }
    }

    private enum ChildBehavior
    {
        /// <summary>The shape a cascade interrupts.</summary>
        HoldUntilCancelled,

        /// <summary>Finish the step immediately — a fresh generation after a resume.</summary>
        Complete,

        /// <summary>The child pauses itself, leaving no pause request standing against the parent.</summary>
        PauseItself,
    }

    private sealed class ChildExecutor : IAgentTurnExecutor
    {
        private readonly ChildBehavior _behavior;
        private readonly Action _entered;
        private readonly Func<Guid, Task<bool>> _pause;

        public ChildExecutor(ChildBehavior behavior, Action entered, Func<Guid, Task<bool>> pause)
        {
            _behavior = behavior;
            _entered = entered;
            _pause = pause;
        }

        public Task BeginRunAsync(AgentRun run, RunContext ctx, CancellationToken ct) => Task.CompletedTask;

        public async Task<StepTurnResult> ExecuteStepAsync(AgentRun run, AgentStep step, RunContext ctx, CancellationToken ct)
        {
            _entered();
            if (_behavior == ChildBehavior.Complete)
                return new StepTurnResult(true, false, null, "child done", null, Guid.NewGuid(), Guid.NewGuid());

            if (_behavior == ChildBehavior.PauseItself)
                await _pause(run.Id);

            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                // Swallowed on purpose: the RETURNING unwind shape, which is what the headless executor does.
            }

            return new StepTurnResult(false, true, "cancelled", string.Empty, null, Guid.Empty, Guid.Empty);
        }

        public Task<StepTurnResult> RunSingleTurnFallbackAsync(AgentRun run, RunContext ctx, CancellationToken ct)
            => Task.FromResult(new StepTurnResult(true, false, null, "fallback", null, Guid.NewGuid(), Guid.NewGuid()));

        public Task EndRunAsync(AgentRun run, RunContext ctx, bool cancelled, bool failed, CancellationToken ct)
            => Task.CompletedTask;

        public Task OnPausedAsync(AgentRun run, RunContext ctx, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed record Dispatch(Guid RunId, Guid ChatId, string Goal, Task Completion);

    /// <summary>CancelAsync reaches only a still-live dispatch, exactly like the real launcher's lookup.</summary>
    private sealed class CascadingChildLauncher : IHeadlessRunLauncher
    {
        private readonly Harness _h;
        private readonly object _liveLock = new();
        private readonly Dictionary<Guid, Action> _live = new();
        private int _calls;
        private int _entered;

        public CascadingChildLauncher(Harness h) => _h = h;

        public List<Dispatch> Dispatches { get; } = new();

        public List<Guid> Cancelled { get; } = new();

        /// <summary>Calls through the parent-pool LaunchAsync rather than the child-pool LaunchChildAsync.</summary>
        public int TopLevelLaunchAttempts { get; private set; }

        /// <summary>Behaviour by dispatch index, so one fact can hold generation 1 and complete generation 2.</summary>
        public Func<int, ChildBehavior> BehaviorFor { get; set; } = _ => ChildBehavior.HoldUntilCancelled;

        public int ExpectedChildren { get; set; } = int.MaxValue;

        /// <summary>Signalled once <see cref="ExpectedChildren"/> children are inside their step.</summary>
        public TaskCompletionSource AllChildrenInFlight { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Token-free on purpose: a cancel must not release the loop from the prologue early.</summary>
        public int GateBeforeLaunchIndex { get; set; } = -1;

        /// <summary>Signalled when <see cref="GateBeforeLaunchIndex"/> is reached.</summary>
        public TaskCompletionSource PrologueReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completed by the fact to let the prologue continue.</summary>
        public TaskCompletionSource ReleasePrologue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>A child sitting at Planning is the state a cascade must leave alone; -1 ⇒ none.</summary>
        public int HoldPlanningChildIndex { get; set; } = -1;

        /// <summary>Signalled when that child's plan turn is entered.</summary>
        public TaskCompletionSource PlanningChildReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completed by the fact to let that child plan and run.</summary>
        public TaskCompletionSource ReleasePlanning { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Snapshot under the append lock of every child the cascade fired at.</summary>
        public List<Guid> SinkFired
        {
            get { lock (_liveLock) return _sinkFired.ToList(); }
        }

        private readonly List<Guid> _sinkFired = new();

        public async Task<HeadlessRunHandle> LaunchChildAsync(
            HeadlessRunRequest req, Guid parentRunId, string? parentPolicyJson, string? parentWorkspaceRoot,
            Guid? personaId = null, CancellationToken ct = default)
        {
            var index = _calls++;
            if (index == GateBeforeLaunchIndex)
            {
                PrologueReached.TrySetResult();
                await ReleasePrologue.Task;
            }

            var child = await _h.NewRunAsync(req.Goal, parentRunId);
            var behavior = BehaviorFor(index);

            var childCts = new CancellationTokenSource();
            Action sink = () =>
            {
                lock (_liveLock) _sinkFired.Add(child.Id);
                try { childCts.Cancel(); } catch { /* already disposed */ }
            };
            _h.Store.RegisterDispatch(child.Id, sink);
            lock (_liveLock) _live[child.Id] = sink;

            var planner = new FakePlanner();
            planner.Plans.Enqueue(new PlanResult(MakeSteps((req.Goal, null)), false));
            if (index == HoldPlanningChildIndex)
            {
                planner.PlanEntered = PlanningChildReached;
                planner.PlanGate = ReleasePlanning.Task;
            }

            var exec = new ChildExecutor(behavior, MarkEntered, runId => _h.Steering.PauseAsync(runId, CancellationToken.None));
            var orchestrator = _h.BuildOrchestrator(planner);

            var completion = Task.Run(async () =>
            {
                try
                {
                    await orchestrator.RunAsync(
                        child, exec, Persona(), Provider(), RunProfile.Interactive, childCts.Token);
                }
                catch (Exception)
                {
                    // The real dispatch self-catches everything: Completion settles on ANY exit of RunAsync.
                }
                finally
                {
                    lock (_liveLock) _live.Remove(child.Id);
                    _h.Store.ReleaseDispatch(child.Id, sink);
                    childCts.Dispose();
                }
            }, CancellationToken.None);

            Dispatches.Add(new Dispatch(child.Id, child.ChatId, req.Goal, completion));
            return new HeadlessRunHandle(child.Id, child.ChatId, completion);
        }

        private void MarkEntered()
        {
            if (Interlocked.Increment(ref _entered) >= ExpectedChildren)
                AllChildrenInFlight.TrySetResult();
        }

        public Task CancelAsync(Guid runId)
        {
            Cancelled.Add(runId);
            Action? sink;
            lock (_liveLock) _live.TryGetValue(runId, out sink);
            sink?.Invoke();
            return Task.CompletedTask;
        }

        public Task<HeadlessRunHandle> LaunchAsync(HeadlessRunRequest req, CancellationToken ct = default)
        {
            TopLevelLaunchAttempts++;
            throw new NotSupportedException("the fan-out never launches a top-level run");
        }

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
            _dir = Path.Combine(Path.GetTempPath(), "PiaCascadePause_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Ctx = new SqliteContext(Path.Combine(_dir, "history.db"));
            Runs = new AgentRunService(Ctx, NullLogger<AgentRunService>.Instance);
            Chats = new AssistantChatService(Ctx, Runs);
            Store = new RunSteeringStore();
            Steering = new AgentRunSteeringService(Runs, Store, NullLogger<AgentRunSteeringService>.Instance);
        }

        public SqliteContext Ctx { get; }

        public AgentRunService Runs { get; }

        public AssistantChatService Chats { get; }

        public RunSteeringStore Store { get; }

        public AgentRunSteeringService Steering { get; }

        public async Task<AgentRun> NewRunAsync(string goal, Guid? parentRunId = null)
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
                chatId, RunShape.Planned, AgentRunTrigger.User, Goal: goal, ParentRunId: parentRunId));
        }

        public AgentRunOrchestrator BuildOrchestrator(
            IAgentPlanner planner, IAgentVerifier? verifier = null, IHeadlessRunLauncher? childLauncher = null) =>
            new(Runs, planner, verifier ?? new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance,
                workspaces: null, childLauncher: childLauncher, chats: null, steering: Store);

        /// <summary>Pausing before the park takes the Running arm, which fires the parent's own token.</summary>
        public async Task<AgentRun> WaitForStateAsync(Guid runId, AgentRunState state, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                var run = await Runs.GetAsync(runId, ct);
                if (run?.State == state)
                    return run;

                Assert.True(DateTime.UtcNow < deadline, $"run {runId} never reached {state} (last: {run?.State})");
                await Task.Delay(10, ct);
            }
        }

        public void Dispose()
        {
            Runs.Dispose();
            Ctx.Dispose();
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }
    }

    /// <summary>Hands back the sink instance so the release can be ownership-guarded on it.</summary>
    private static Action RegisterDispatch(Harness h, Guid runId, CancellationTokenSource cts)
    {
        Action sink = () => { try { cts.Cancel(); } catch { /* already disposed */ } };
        h.Store.RegisterDispatch(runId, sink);
        return sink;
    }

    [Fact]
    public async Task PausingAParent_ParksEveryChild_AndRecordsNoneAsFailed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var launcher = new CascadingChildLauncher(h) { ExpectedChildren = 2 };
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("a", 1), ("b", 1)), false));
        var verifier = new FakeVerifier();
        var exec = new ParentExecutor();

        using var dispatchCts = new CancellationTokenSource();
        RegisterDispatch(h, run.Id, dispatchCts);

        var loop = h.BuildOrchestrator(planner, verifier, launcher).RunAsync(
            run, exec, Persona(), Provider(), RunProfile.Interactive, dispatchCts.Token);

        await launcher.AllChildrenInFlight.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await h.WaitForStateAsync(run.Id, AgentRunState.WaitingForChildren, ct);

        Assert.True(await h.Steering.PauseAsync(run.Id, ct)); // not vacuous: the pause was ACCEPTED
        await loop.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // The parent: the four-part resumable shape.
        var parent = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Paused, parent!.State);
        Assert.Null(parent.CompletedAt);
        Assert.Equal(AgentRunService.UserPausedReason, RunPauseEnvelope.ReadReason(parent));

        // Every child parked through its OWN abort, with its own step given back to its own plan.
        var children = await h.Runs.GetChildRunsAsync(run.Id, ct);
        Assert.Equal(2, children.Count);
        Assert.All(children, c => Assert.Equal(AgentRunState.Paused, c.State));
        Assert.All(children, c => Assert.Null(c.CompletedAt));
        Assert.All(children, c => Assert.Equal(AgentRunService.UserPausedReason, RunPauseEnvelope.ReadReason(c)));
        foreach (var c in children)
        {
            var childRow = await h.Runs.GetAsync(c.Id, ct);
            Assert.Equal(AgentStepStatus.Pending, Assert.Single(childRow!.Plan).Status);
        }

        // The parent's sibling steps: back in the plan, NOT failed …
        Assert.Equal(2, parent.Plan.Count);
        Assert.All(parent.Plan, s => Assert.Equal(AgentStepStatus.Pending, s.Status));
        Assert.Equal("a", (await h.Runs.NextPendingStepAsync(run.Id, ct))!.Title);

        // … and the "child run did not settle" text reached neither the replan nor the critic.
        Assert.Equal(0, planner.ReplanCalls);
        Assert.Empty(planner.ReplanFailures);
        Assert.Equal(0, verifier.VerifyCalls);

        Assert.True(exec.PausedCalled);  // the non-terminal executor release ran …
        Assert.False(exec.EndCalled);    // … and the terminal one did not
    }

    [Fact]
    public async Task PausingAParent_DoesNotFireItsOwnToken_SoItNeverSettlesCancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var launcher = new CascadingChildLauncher(h) { ExpectedChildren = 2 };
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("a", 1), ("b", 1)), false));
        var exec = new ParentExecutor();

        using var dispatchCts = new CancellationTokenSource();
        RegisterDispatch(h, run.Id, dispatchCts);

        var loop = h.BuildOrchestrator(planner, childLauncher: launcher).RunAsync(
            run, exec, Persona(), Provider(), RunProfile.Interactive, dispatchCts.Token);

        await launcher.AllChildrenInFlight.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await h.WaitForStateAsync(run.Id, AgentRunState.WaitingForChildren, ct);
        Assert.True(await h.Steering.PauseAsync(run.Id, ct));
        await loop.WaitAsync(TimeSpan.FromSeconds(10), ct);

        Assert.False(dispatchCts.IsCancellationRequested); // THE claim: the parent was never cancelled

        var parent = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Paused, parent!.State);
        Assert.NotEqual(AgentRunState.Cancelled, parent.State);
        Assert.Null(parent.CompletedAt);
        Assert.False(exec.EndCancelled);
    }

    /// <summary>A child resumed from inside its parent would queue on the pool the parent already holds.</summary>
    [Fact]
    public async Task ResumingAPausedParent_SupersedesThePausedGeneration_AndDispatchesAFreshOne()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var launcher = new CascadingChildLauncher(h)
        {
            ExpectedChildren = 2,
            // Generation 1 holds until the cascade reaches it; generation 2 (after Continue) completes.
            BehaviorFor = i => i < 2 ? ChildBehavior.HoldUntilCancelled : ChildBehavior.Complete,
        };
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("a", 1), ("b", 1)), false));

        using var dispatchCts = new CancellationTokenSource();
        RegisterDispatch(h, run.Id, dispatchCts);

        var loop = h.BuildOrchestrator(planner, childLauncher: launcher).RunAsync(
            run, new ParentExecutor(), Persona(), Provider(), RunProfile.Interactive, dispatchCts.Token);

        await launcher.AllChildrenInFlight.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await h.WaitForStateAsync(run.Id, AgentRunState.WaitingForChildren, ct);
        Assert.True(await h.Steering.PauseAsync(run.Id, ct));
        await loop.WaitAsync(TimeSpan.FromSeconds(10), ct);

        var generationOne = launcher.Dispatches.Select(d => d.RunId).ToList();
        Assert.Equal(2, generationOne.Count);

        // CONTINUE: claim from Paused, then re-enter with resume: true — what HeadlessRunLauncher.ResumeAsync does.
        Assert.True(await h.Runs.TryResumeFromPauseAsync(run.Id, ct));
        var resumed = (await h.Runs.GetAsync(run.Id, ct))!;
        using var resumeCts = new CancellationTokenSource();
        RegisterDispatch(h, run.Id, resumeCts);
        await h.BuildOrchestrator(new FakePlanner(), childLauncher: launcher).RunAsync(
            resumed, new ParentExecutor(), Persona(), Provider(), RunProfile.Interactive, resumeCts.Token,
            resume: true);

        // The paused generation was superseded — terminal, not lingering.
        foreach (var oldChild in generationOne)
        {
            var old = await h.Runs.GetAsync(oldChild, ct);
            Assert.Equal(AgentRunState.Cancelled, old!.State);
            Assert.Contains("superseded", old.ExtraJson ?? string.Empty);
        }

        // A FRESH generation ran, with new run ids, and the parent completed on their work.
        Assert.Equal(4, launcher.Dispatches.Count);
        var generationTwo = launcher.Dispatches.Skip(2).Select(d => d.RunId).ToList();
        Assert.All(generationTwo, id => Assert.DoesNotContain(id, generationOne));
        foreach (var newChild in generationTwo)
            Assert.Equal(AgentRunState.Completed, (await h.Runs.GetAsync(newChild, ct))!.State);

        var final = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.All(final.Plan, s => Assert.Equal(AgentStepStatus.Done, s.Status));
    }

    [Fact]
    public async Task APausedChild_LeftBehind_IsNotOrphaned()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");

        // A user-paused child of this parent, from a "previous process": the launcher double has never heard of
        // it, and the states it passes through are the real ones (Running → Paused, through the CAS).
        var stale = await h.NewRunAsync("stale child", parentRunId: run.Id);
        await h.Runs.SetStateAsync(stale.Id, AgentRunState.Running, ct);
        Assert.True(await h.Runs.TryPauseUserAsync(stale.Id, ct));

        var launcher = new CascadingChildLauncher(h) { BehaviorFor = _ => ChildBehavior.Complete };
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("a", 1), ("b", 1)), false));

        await h.BuildOrchestrator(planner, childLauncher: launcher).RunAsync(
            run, new ParentExecutor(), Persona(), Provider(), RunProfile.Interactive, ct);

        var settled = await h.Runs.GetAsync(stale.Id, ct);
        Assert.Equal(AgentRunState.Cancelled, settled!.State); // the row-level fallback did the work
        Assert.Contains("superseded", settled.ExtraJson ?? string.Empty);

        // No non-terminal child row survives the completed parent.
        var children = await h.Runs.GetChildRunsAsync(run.Id, ct);
        Assert.Equal(3, children.Count); // the stale one plus the fresh pair — non-vacuity for the All below
        Assert.All(children, c => Assert.True(
            c.State is AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Cancelled,
            $"child {c.Id} survived its parent in {c.State}"));
        Assert.Equal(AgentRunState.Completed, (await h.Runs.GetAsync(run.Id, ct))!.State);
    }

    /// <summary>The slot pools are private, so which launch method was called is the only observable.</summary>
    [Fact]
    public async Task CascadePause_NeverAcquiresTheParentSlotPoolForAChild()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var launcher = new CascadingChildLauncher(h)
        {
            ExpectedChildren = 2,
            BehaviorFor = i => i < 2 ? ChildBehavior.HoldUntilCancelled : ChildBehavior.Complete,
        };
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("a", 1), ("b", 1)), false));

        using var dispatchCts = new CancellationTokenSource();
        RegisterDispatch(h, run.Id, dispatchCts);

        var loop = h.BuildOrchestrator(planner, childLauncher: launcher).RunAsync(
            run, new ParentExecutor(), Persona(), Provider(), RunProfile.Interactive, dispatchCts.Token);

        await launcher.AllChildrenInFlight.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await h.WaitForStateAsync(run.Id, AgentRunState.WaitingForChildren, ct);
        Assert.True(await h.Steering.PauseAsync(run.Id, ct));
        await loop.WaitAsync(TimeSpan.FromSeconds(10), ct);

        var paused = launcher.Dispatches.Select(d => d.RunId).ToList();

        Assert.True(await h.Runs.TryResumeFromPauseAsync(run.Id, ct));
        var resumed = (await h.Runs.GetAsync(run.Id, ct))!;
        using var resumeCts = new CancellationTokenSource();
        RegisterDispatch(h, run.Id, resumeCts);
        await h.BuildOrchestrator(new FakePlanner(), childLauncher: launcher).RunAsync(
            resumed, new ParentExecutor(), Persona(), Provider(), RunProfile.Interactive, resumeCts.Token,
            resume: true);

        Assert.Equal(0, launcher.TopLevelLaunchAttempts);      // nothing ever took the parent pool for a child
        Assert.Equal(4, launcher.Dispatches.Count);            // every dispatch came through the child path
        var fresh = launcher.Dispatches.Skip(2).Select(d => d.RunId);
        Assert.All(fresh, id => Assert.DoesNotContain(id, paused)); // a NEW generation, not the paused rows again
        foreach (var old in paused)
            Assert.Equal(AgentRunState.Cancelled, (await h.Runs.GetAsync(old, ct))!.State);
    }

    [Fact]
    public async Task AChildThatPausedItself_ReParksTheParentAtItsBudgetShape_NotAsAUserPause()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var launcher = new CascadingChildLauncher(h)
        {
            ExpectedChildren = 2,
            // One child finishes; the other pauses ITSELF. Nobody pauses the parent.
            BehaviorFor = i => i == 0 ? ChildBehavior.Complete : ChildBehavior.PauseItself,
        };
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("a", 1), ("b", 1)), false));
        var exec = new ParentExecutor();

        using var dispatchCts = new CancellationTokenSource();
        RegisterDispatch(h, run.Id, dispatchCts);

        await h.BuildOrchestrator(planner, childLauncher: launcher).RunAsync(
            run, exec, Persona(), Provider(), RunProfile.Interactive, dispatchCts.Token);

        var parent = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.WaitingForInput, parent!.State);                          // the budget shape …
        Assert.Equal(AgentRunOrchestrator.ChildrenParkedReason, RunPauseEnvelope.ReadReason(parent)); // … verbatim
        Assert.NotEqual(AgentRunService.UserPausedReason, RunPauseEnvelope.ReadReason(parent));
        Assert.True(exec.PausedCalled);

        // Non-vacuity: a child really did end at Paused(4), and the parked sibling's step is back at Pending
        // rather than failed — i.e. the widened arm, not the default one, is what handled it.
        var children = await h.Runs.GetChildRunsAsync(run.Id, ct);
        Assert.Contains(children, c => c.State == AgentRunState.Paused);
        Assert.Contains(children, c => c.State == AgentRunState.Completed);
        Assert.Equal(AgentStepStatus.Done, Assert.Single(parent.Plan, s => s.Title == "a").Status);
        Assert.Equal(AgentStepStatus.Pending, Assert.Single(parent.Plan, s => s.Title == "b").Status);
        Assert.Equal(0, planner.ReplanCalls);
    }

    // The four facts below pause INSIDE the fan-out dispatch prologue, where the row still reads Running.
    // Every fact above waits for WaitingForChildren first, so that window sits structurally outside them.

    /// <summary>The plan leads with an ordinary step: on a launch's first step a pause is refused outright.</summary>
    [Fact]
    public async Task APauseInsideTheFanOutDispatchPrologue_ParksTheRunResumable_NeverCancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var launcher = new CascadingChildLauncher(h)
        {
            BehaviorFor = _ => ChildBehavior.Complete,
            GateBeforeLaunchIndex = 0, // hold the loop before the FIRST child is even created
        };
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("s1", null), ("g1", 1), ("g2", 1)), false));
        var exec = new ParentExecutor();

        using var dispatchCts = new CancellationTokenSource();
        RegisterDispatch(h, run.Id, dispatchCts);

        var loop = h.BuildOrchestrator(planner, childLauncher: launcher).RunAsync(
            run, exec, Persona(), Provider(), RunProfile.Interactive, dispatchCts.Token);

        await launcher.PrologueReached.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // THE WINDOW, asserted rather than assumed — this is the line that stops a later edit from drifting
        // back outside it.
        var inPrologue = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Running, inPrologue!.State);
        Assert.NotEqual(AgentRunState.WaitingForChildren, inPrologue.State);
        Assert.Empty(await h.Runs.GetChildRunsAsync(run.Id, ct)); // nothing dispatched yet: a pure prologue pause

        Assert.True(await h.Steering.PauseAsync(run.Id, ct));  // accepted …
        Assert.False(dispatchCts.IsCancellationRequested);     // … and the parent's own token was NOT fired

        launcher.ReleasePrologue.TrySetResult();
        await loop.WaitAsync(TimeSpan.FromSeconds(10), ct);

        var parent = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Paused, parent!.State);
        Assert.NotEqual(AgentRunState.Cancelled, parent.State);
        Assert.Null(parent.CompletedAt);
        Assert.Equal(AgentRunService.UserPausedReason, RunPauseEnvelope.ReadReason(parent));
        Assert.True(exec.PausedCalled);
        Assert.False(exec.EndCalled);
        Assert.False(exec.EndCancelled);

        // The children the prologue was in the middle of dispatching ran and their work was KEPT.
        Assert.Equal(2, launcher.Dispatches.Count);
        var children = await h.Runs.GetChildRunsAsync(run.Id, ct);
        Assert.Equal(2, children.Count);
        Assert.All(children, c => Assert.Equal(AgentRunState.Completed, c.State));
        Assert.All(parent.Plan, s => Assert.Equal(AgentStepStatus.Done, s.Status));
        Assert.Equal(0, planner.ReplanCalls);

        // RESUMABLE, exercised rather than inferred: the claim CAS actually takes the row.
        Assert.True(await h.Runs.TryResumeFromPauseAsync(run.Id, ct));
    }

    [Fact]
    public async Task APauseMidPrologue_CascadesToTheChildAlreadyDispatched_AndStillParksTheParent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var launcher = new CascadingChildLauncher(h)
        {
            ExpectedChildren = 1,
            BehaviorFor = i => i == 0 ? ChildBehavior.HoldUntilCancelled : ChildBehavior.Complete,
            GateBeforeLaunchIndex = 1, // child 0 is live; hold before child 1
        };
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("s1", null), ("g1", 1), ("g2", 1)), false));
        var exec = new ParentExecutor();

        using var dispatchCts = new CancellationTokenSource();
        RegisterDispatch(h, run.Id, dispatchCts);

        var loop = h.BuildOrchestrator(planner, childLauncher: launcher).RunAsync(
            run, exec, Persona(), Provider(), RunProfile.Interactive, dispatchCts.Token);

        await launcher.AllChildrenInFlight.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await launcher.PrologueReached.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        Assert.Equal(AgentRunState.Running, (await h.Runs.GetAsync(run.Id, ct))!.State); // still the window
        var liveChild = launcher.Dispatches[0].RunId;

        Assert.True(await h.Steering.PauseAsync(run.Id, ct));
        Assert.False(dispatchCts.IsCancellationRequested);        // the parent: never fired
        Assert.Contains(liveChild, launcher.SinkFired);           // the live child: fired, which is the cascade

        launcher.ReleasePrologue.TrySetResult();
        await loop.WaitAsync(TimeSpan.FromSeconds(10), ct);

        var parent = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Paused, parent!.State);
        Assert.Null(parent.CompletedAt);
        Assert.Equal(AgentRunService.UserPausedReason, RunPauseEnvelope.ReadReason(parent));
        Assert.False(exec.EndCalled);

        Assert.Equal(AgentRunState.Paused, (await h.Runs.GetAsync(liveChild, ct))!.State);
        Assert.Null((await h.Runs.GetAsync(liveChild, ct))!.CompletedAt);
        var lateChild = launcher.Dispatches[1].RunId;
        Assert.Equal(AgentRunState.Completed, (await h.Runs.GetAsync(lateChild, ct))!.State);

        // The paused child's sibling step is back in the plan; the finished one's is kept.
        Assert.Equal(AgentStepStatus.Pending, Assert.Single(parent.Plan, s => s.Title == "g1").Status);
        Assert.Equal(AgentStepStatus.Done, Assert.Single(parent.Plan, s => s.Title == "g2").Status);
        Assert.Equal(0, planner.ReplanCalls);
        Assert.Empty(planner.ReplanFailures);

        Assert.True(await h.Runs.TryResumeFromPauseAsync(run.Id, ct));
    }

    /// <summary>Ordering, not luck: the request is consumed at the BeginFanOut handshake.</summary>
    [Fact]
    public async Task APauseInTheResumeRampUp_OfADelegatingParent_ParksItBeforeAnyChildIsDispatched()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var launcher = new CascadingChildLauncher(h)
        {
            ExpectedChildren = 2,
            BehaviorFor = _ => ChildBehavior.HoldUntilCancelled,
        };
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("a", 1), ("b", 1)), false));

        // Generation 1: dispatch, cascade-pause it the ordinary way, so the run is genuinely Paused with both
        // group steps back at Pending — the state a Continue actually claims.
        using var dispatchA = new CancellationTokenSource();
        RegisterDispatch(h, run.Id, dispatchA);
        var loop = h.BuildOrchestrator(planner, childLauncher: launcher).RunAsync(
            run, new ParentExecutor(), Persona(), Provider(), RunProfile.Interactive, dispatchA.Token);
        await launcher.AllChildrenInFlight.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await h.WaitForStateAsync(run.Id, AgentRunState.WaitingForChildren, ct);
        Assert.True(await h.Steering.PauseAsync(run.Id, ct));
        await loop.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(AgentRunState.Paused, (await h.Runs.GetAsync(run.Id, ct))!.State);
        Assert.Equal(2, launcher.Dispatches.Count);

        // CONTINUE: the claim CAS puts the row back at Running, then the resume registers its sink — and only
        // then does the user press Pause. The row is Running and the run is NOT yet fanning out, so this takes
        // the ordinary fire-the-cancel branch: the request is the new dispatch's, and so is the fired token.
        Assert.True(await h.Runs.TryResumeFromPauseAsync(run.Id, ct));
        var resumed = (await h.Runs.GetAsync(run.Id, ct))!;
        using var dispatchB = new CancellationTokenSource();
        RegisterDispatch(h, run.Id, dispatchB);
        Assert.True(await h.Steering.PauseAsync(run.Id, ct));
        Assert.True(dispatchB.IsCancellationRequested);

        var exec = new ParentExecutor();
        await h.BuildOrchestrator(new FakePlanner(), childLauncher: launcher).RunAsync(
            resumed, exec, Persona(), Provider(), RunProfile.Interactive, dispatchB.Token, resume: true);

        var parent = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Paused, parent!.State);          // NOT Cancelled
        Assert.NotEqual(AgentRunState.Cancelled, parent.State);
        Assert.Null(parent.CompletedAt);
        Assert.Equal(AgentRunService.UserPausedReason, RunPauseEnvelope.ReadReason(parent));
        Assert.True(exec.PausedCalled);
        Assert.False(exec.EndCalled);

        // Nothing was dispatched into the pause: the generation count is still generation 1's, and both group
        // steps are still Pending, so one Continue re-dispatches the whole group.
        Assert.Equal(2, launcher.Dispatches.Count);
        Assert.All(parent.Plan, s => Assert.Equal(AgentStepStatus.Pending, s.Status));
        Assert.True(await h.Runs.TryResumeFromPauseAsync(run.Id, ct));
    }

    [Fact]
    public async Task ACascade_LeavesAChildStillPlanningAlone_AndTheParentStillParksPaused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var launcher = new CascadingChildLauncher(h)
        {
            ExpectedChildren = 1, // only child 0 ever reaches a step; child 1 is held in its plan turn
            BehaviorFor = i => i == 0 ? ChildBehavior.HoldUntilCancelled : ChildBehavior.Complete,
            HoldPlanningChildIndex = 1,
        };
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("a", 1), ("b", 1)), false));
        var exec = new ParentExecutor();

        using var dispatchCts = new CancellationTokenSource();
        RegisterDispatch(h, run.Id, dispatchCts);

        var loop = h.BuildOrchestrator(planner, childLauncher: launcher).RunAsync(
            run, exec, Persona(), Provider(), RunProfile.Interactive, dispatchCts.Token);

        await launcher.AllChildrenInFlight.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await launcher.PlanningChildReached.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await h.WaitForStateAsync(run.Id, AgentRunState.WaitingForChildren, ct);

        var liveChild = launcher.Dispatches[0].RunId;
        var planningChild = launcher.Dispatches[1].RunId;
        Assert.Equal(AgentRunState.Planning, (await h.Runs.GetAsync(planningChild, ct))!.State); // the window

        Assert.True(await h.Steering.PauseAsync(run.Id, ct));

        // THE CLAIM, at the mechanism: pausable child fired at, non-pausable child left alone.
        Assert.Contains(liveChild, launcher.SinkFired);
        Assert.DoesNotContain(planningChild, launcher.SinkFired);

        launcher.ReleasePlanning.TrySetResult(); // the untouched child plans and finishes on its own
        await loop.WaitAsync(TimeSpan.FromSeconds(10), ct);

        var parent = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Paused, parent!.State);
        Assert.NotEqual(AgentRunState.Failed, parent.State);
        Assert.Null(parent.CompletedAt);
        Assert.Equal(AgentRunService.UserPausedReason, RunPauseEnvelope.ReadReason(parent));
        Assert.False(dispatchCts.IsCancellationRequested);
        Assert.False(exec.EndCalled);

        // Neither child is destroyed: one parked through its own abort, the other simply ran.
        Assert.Equal(AgentRunState.Paused, (await h.Runs.GetAsync(liveChild, ct))!.State);
        var held = await h.Runs.GetAsync(planningChild, ct);
        Assert.Equal(AgentRunState.Completed, held!.State);
        Assert.NotEqual(AgentRunState.Cancelled, held.State);
        Assert.NotEqual(AgentRunState.Planning, held.State); // not stranded, which is the pre-fix ending

        // No sibling step was charged as a failure, so no replan was burnt.
        Assert.Equal(AgentStepStatus.Pending, Assert.Single(parent.Plan, s => s.Title == "a").Status);
        Assert.Equal(AgentStepStatus.Done, Assert.Single(parent.Plan, s => s.Title == "b").Status);
        Assert.Equal(0, planner.ReplanCalls);
        Assert.Empty(planner.ReplanFailures);

        Assert.True(await h.Runs.TryResumeFromPauseAsync(run.Id, ct));
    }

    /// <summary>A table rather than a scenario, so a new AgentRunState must be classified here deliberately.</summary>
    [Fact]
    public async Task ACascade_FiresAtExactlyThePausableChildren_AndAtNoOthers()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var parent = await h.NewRunAsync("goal");
        await h.Runs.SetStateAsync(parent.Id, AgentRunState.WaitingForChildren, ct);
        using var parentCts = new CancellationTokenSource();
        RegisterDispatch(h, parent.Id, parentCts);

        var fired = new List<AgentRunState>();
        var byState = new Dictionary<AgentRunState, Guid>();
        foreach (var state in new[]
                 {
                     AgentRunState.Planning, AgentRunState.Running, AgentRunState.Verifying,
                     AgentRunState.WaitingForInput, AgentRunState.Paused, AgentRunState.Completed,
                     AgentRunState.Failed, AgentRunState.Cancelled, AgentRunState.WaitingForChildren,
                 })
        {
            var child = await h.NewRunAsync($"child {state}", parentRunId: parent.Id);
            await h.Runs.SetStateAsync(child.Id, state, ct);
            byState[state] = child.Id;
            var captured = state;
            h.Store.RegisterDispatch(child.Id, () => { lock (fired) fired.Add(captured); });
        }

        Assert.True(await h.Steering.PauseAsync(parent.Id, ct));

        Assert.False(parentCts.IsCancellationRequested); // never the parent's own token, in any arm
        List<AgentRunState> expected =
            [AgentRunState.Running, AgentRunState.Verifying, AgentRunState.WaitingForChildren];
        lock (fired) Assert.Equal(expected.OrderBy(s => s), fired.OrderBy(s => s));

        // And the REQUEST follows the fire exactly: recorded for the pausable children, for nobody else. A
        // request left standing against a child that was never interrupted would be honoured by whatever
        // dispatch of it came next.
        foreach (var (state, id) in byState)
            Assert.Equal(expected.Contains(state), h.Store.TryConsumePauseRequest(id));

        // Non-vacuity for the negative half: the parent's OWN request was recorded, so the run really was
        // steered — the children above were skipped by the set test, not by a refused pause.
        Assert.True(h.Store.TryConsumePauseRequest(parent.Id));
    }

    /// <summary>Driven through <c>resume: true</c> so no planner turn can rewrite the skipped step away.</summary>
    [Fact]
    public async Task AGroupThatDroppedBelowTwoPendingMembers_StillSupersedesItsPausedChild()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var parent = await h.NewRunAsync("goal");

        // The plan a paused-then-skipped fan-out leaves behind: both members are in group 1, one was skipped
        // by the user, so exactly ONE pending member remains.
        await h.Runs.ReplaceStepsAsync(parent.Id, MakeSteps(("a", 1), ("b", 1)), ct);
        var stepA = (await h.Runs.GetAsync(parent.Id, ct))!.Plan.Single(s => s.Title == "a");
        await h.Runs.SetStepStatusAsync(stepA.Id, AgentStepStatus.Skipped, ct);

        // The generation behind it: a child this parent dispatched and the user paused.
        var child = await h.NewRunAsync("a", parent.Id);
        await h.Runs.SetStateAsync(child.Id, AgentRunState.Running, ct);
        Assert.True(await h.Runs.TryPauseUserAsync(child.Id, ct));

        var launcher = new CascadingChildLauncher(h);
        var exec = new ParentExecutor();
        await h.Runs.SetStateAsync(parent.Id, AgentRunState.Paused, ct);
        Assert.True(await h.Runs.TryResumeFromPauseAsync(parent.Id, ct)); // the Continue, exactly as the UI does it
        var resumed = (await h.Runs.GetAsync(parent.Id, ct))!;

        await h.BuildOrchestrator(new FakePlanner(), childLauncher: launcher)
            .RunAsync(resumed, exec, Persona(), Provider(), RunProfile.Interactive, ct, resume: true);

        // Non-vacuity: this really did take the DECLINE path — the surviving member ran in-process and no new
        // child was dispatched. If it had fanned out, the cleanup would have been the pre-existing one inside
        // FanOutCoreAsync and this fact would prove nothing about the early return.
        Assert.Equal(new[] { "b" }, exec.Executed);
        Assert.Empty(launcher.Dispatches);

        // THE CLAIM: the previous generation was superseded on the way past, not orphaned.
        Assert.Contains(child.Id, launcher.Cancelled);
        var settled = await h.Runs.GetAsync(child.Id, ct);
        Assert.Equal(AgentRunState.Cancelled, settled!.State); // terminal — a parked child is never swept
        Assert.NotNull(settled.CompletedAt);

        var final = await h.Runs.GetAsync(parent.Id, ct);
        Assert.Equal(AgentRunState.Completed, final!.State);   // and the parent itself is unaffected
    }
}
