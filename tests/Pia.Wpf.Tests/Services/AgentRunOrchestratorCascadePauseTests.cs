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

/// <summary>
/// Batch 08 G5 / D6 — the CASCADE pause of a fan-out. Pausing a parent that is
/// <see cref="AgentRunState.WaitingForChildren"/> pauses what is actually working (its children) and lets the
/// parent's own wait end naturally; the parent's token is deliberately never fired.
/// <para>
/// Why that matters, stated once for the whole file: <c>AgentRunOrchestrator</c>'s fan-out checks
/// <c>cts.IsCancellationRequested</c> BEFORE the un-park CAS and returns <c>Cancelled: true</c>, which its
/// caller turns into <c>SafeFail(cancelled: true)</c> — a TERMINAL settle with <c>CompletedAt</c> stamped. So
/// "release the parent by cancelling it" would turn the entire feature into a cancel, silently, with the run
/// still settling. <see cref="PausingAParent_DoesNotFireItsOwnToken_SoItNeverSettlesCancelled"/> is that
/// guard, asserted from both sides (the token AND the row).
/// </para>
/// <para>
/// These facts drive the real <see cref="AgentRunOrchestrator"/> against a real SQLite
/// <see cref="AgentRunService"/>, the real <see cref="RunSteeringStore"/> and the real
/// <see cref="AgentRunSteeringService"/>. Each CHILD runs its own real orchestrator inside the launcher
/// double, so a cascade-paused child pauses through its OWN D1 abort — its step really goes back to
/// <c>Pending</c> and its row really CASes to <c>Paused</c> — rather than through a row the fixture wrote.
/// </para>
/// </summary>
public sealed class AgentRunOrchestratorCascadePauseTests
{
    private static Persona Persona() => new() { Name = "Pia", SystemPrompt = "sys" };

    private static AiProvider Provider() => new() { Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };

    /// <summary>A plan carrying the <c>{"parallelGroup":N}</c> marker the planner writes, produced through
    /// <see cref="JsonSerializer"/> rather than hand-typed so a producer change cannot leave these facts
    /// asserting a stale spelling.</summary>
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

        /// <summary>Every failure string a replan was asked to recover from. The fan-out's
        /// <c>"child run did not settle"</c> arrives here and nowhere else that a test can read, because a
        /// sibling's error text is carried in <c>ctx</c> and never persisted on the step row.</summary>
        public List<string?> ReplanFailures { get; } = new();

        /// <summary>Signalled as the plan turn is entered — at which point the row reads <c>Planning</c>,
        /// because <c>RunAsync</c> sets that state immediately before calling here.</summary>
        public TaskCompletionSource? PlanEntered { get; set; }

        /// <summary>Holds the plan turn open until the fact releases it. Awaited WITH the dispatch's token, so
        /// a fired cancel throws <see cref="OperationCanceledException"/> out of the plan turn exactly as a
        /// real provider call would — which is the whole shape a cascade aimed at a <c>Planning</c> child
        /// produces.</summary>
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
            PausedCalled = true; // the NON-terminal release (guardrail 5) — never EndRunAsync
            return Task.CompletedTask;
        }
    }

    private enum ChildBehavior
    {
        /// <summary>Sit inside the step until this dispatch's own token is cancelled, then report the cancel
        /// the way <c>HeadlessTurnExecutor</c>'s cancel arm does. The shape the cascade interrupts.</summary>
        HoldUntilCancelled,

        /// <summary>Finish the step immediately — a fresh generation after a resume.</summary>
        Complete,

        /// <summary>Pause ITSELF (the user pausing a child from the child's own chat) and then unwind. Produces
        /// a <c>Paused</c> child with NO pause request standing against the parent.</summary>
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
                await Task.Delay(Timeout.Infinite, ct); // the sink's cancel reaches the in-flight step (R13)
            }
            catch (OperationCanceledException)
            {
                // Swallowed on purpose: this is the RETURNING unwind shape, which is what the headless
                // executor does. The throwing shape has its own coverage in G3's file.
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

    /// <summary>
    /// Launcher double that behaves like the real one in the four ways these facts depend on: it creates a REAL
    /// child run row through the real service; it registers each dispatch's cancel sink with the SAME steering
    /// store the parent uses; it runs a REAL <see cref="AgentRunOrchestrator"/> for the child, so a cascade
    /// pause is honoured by the child's own loop rather than by the fixture; and its <c>CancelAsync</c> reaches
    /// only a dispatch that is still live — exactly like <c>HeadlessRunLauncher</c>'s <c>_inflight</c> lookup.
    /// <para>
    /// That last property is what makes the supersede facts real: once a child has PAUSED, its dispatch has
    /// returned and its registration is gone, so <c>CancelAsync</c> is a no-op against it. That is the same
    /// shape a child parked by a PREVIOUS PROCESS presents, and it is the only reason
    /// <c>SafeCancelStaleChildrenAsync</c>'s row-level settle exists.
    /// </para>
    /// </summary>
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

        /// <summary>Calls that came through <see cref="LaunchAsync"/> — the PARENT slot pool (<c>_slots</c>) —
        /// rather than <see cref="LaunchChildAsync"/>, which is the child pool (<c>_childSlots</c>).</summary>
        public int TopLevelLaunchAttempts { get; private set; }

        /// <summary>Behaviour by dispatch index, so one fact can hold generation 1 and complete generation 2.</summary>
        public Func<int, ChildBehavior> BehaviorFor { get; set; } = _ => ChildBehavior.HoldUntilCancelled;

        public int ExpectedChildren { get; set; } = int.MaxValue;

        /// <summary>Signalled once <see cref="ExpectedChildren"/> children are inside their step.</summary>
        public TaskCompletionSource AllChildrenInFlight { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Batch 08 F2. Which <c>LaunchChildAsync</c> call blocks — before it creates anything — so the parent's
        /// loop is held INSIDE the fan-out dispatch prologue, the window in which the persisted row still reads
        /// <c>Running</c>. <c>-1</c> ⇒ no gate. Deliberately takes NO cancellation token: the gate exists to keep
        /// the loop in the window, and a cancel must not be able to release it early.
        /// </summary>
        public int GateBeforeLaunchIndex { get; set; } = -1;

        /// <summary>Signalled when <see cref="GateBeforeLaunchIndex"/> is reached.</summary>
        public TaskCompletionSource PrologueReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completed by the fact to let the prologue continue.</summary>
        public TaskCompletionSource ReleasePrologue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Which child is held inside its PLAN turn — a child sitting at <c>Planning</c>, which is the
        /// state a cascade must leave alone. <c>-1</c> ⇒ none.</summary>
        public int HoldPlanningChildIndex { get; set; } = -1;

        /// <summary>Signalled when that child's plan turn is entered.</summary>
        public TaskCompletionSource PlanningChildReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completed by the fact to let that child plan and run.</summary>
        public TaskCompletionSource ReleasePlanning { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Every child whose cancel SINK was invoked, in order — the direct observable of "the cascade
        /// fired at this child". Snapshot, taken under the lock every append uses.</summary>
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

        /// <summary>
        /// Poll the persisted row until it reads <paramref name="state"/>. The parent's park
        /// (<c>SafeBeginChildWait</c>) happens AFTER the last child was dispatched, so "both children are inside
        /// their step" does not imply "the parent's row says WaitingForChildren" — and pausing one instant too
        /// early would take the <c>Running</c> arm, which fires the parent's own token. Polls the row rather
        /// than sleeping a fixed span, so the fact is a wait and not a race.
        /// </summary>
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

    /// <summary>Register a dispatch's cancel sink exactly the way the launcher does, and hand back the delegate
    /// instance so the release can be ownership-guarded on it.</summary>
    private static Action RegisterDispatch(Harness h, Guid runId, CancellationTokenSource cts)
    {
        Action sink = () => { try { cts.Cancel(); } catch { /* already disposed */ } };
        h.Store.RegisterDispatch(runId, sink);
        return sink;
    }

    /// <summary>
    /// <b>THE HEADLINE FACT, and the group's mandatory red demo.</b> Pausing a <c>WaitingForChildren</c> parent
    /// parks every child and records NONE of them as a failed sibling.
    /// <para>
    /// Red before the <c>WaitingForInput or Paused</c> widening of the fan-out's per-child arm: a <c>Paused</c>
    /// child falls through to <c>default:</c>, which sets <c>anyFailed</c>, calls <c>SettleSiblingAsync</c> with
    /// the error <c>"child run did not settle"</c> (persisting the step <c>Failed</c> AND recording it into
    /// <c>ctx</c> for the critic), and rolls up nothing — so the user presses Pause and the parent replans
    /// around work that is sitting there resumable.
    /// </para>
    /// <para>
    /// The <c>ctx</c> half is asserted through its co-located call site rather than by reading a private field:
    /// <c>SettleSiblingAsync</c> is the ONLY writer of both the persisted step status and <c>ctx.RecordStep</c>
    /// for a sibling, so a step row still at <c>Pending</c> plus a planner that was never asked to recover from
    /// <c>"child run did not settle"</c> is exactly the evidence that it never ran.
    /// </para>
    /// </summary>
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

        // Every child parked through its OWN D1 abort, with its own step given back to its own plan.
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
        Assert.False(exec.EndCalled);    // … and the terminal one did not (guardrail 5)
    }

    /// <summary>
    /// <b>The guardrail this whole group is one mistake away from.</b> The cascade must never fire the PARENT's
    /// own token: the fan-out reads a cancelled token before the un-park CAS and returns <c>Cancelled: true</c>,
    /// which settles the run terminally with <c>CompletedAt</c> stamped and nothing left to resume.
    /// <para>
    /// Asserted from both sides, because either alone is weak: the parent's dispatch token was never cancelled
    /// (the direct claim — a builder reaching for <c>FireCancel(parent)</c> or a <c>.WaitAsync(cts.Token)</c> on
    /// the child <c>WhenAll</c> reds this instantly), and the row is <c>Paused</c> with no <c>CompletedAt</c>
    /// and no <c>EndRunAsync(cancelled: true)</c> (the consequence).
    /// </para>
    /// </summary>
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

    /// <summary>
    /// Continue on a cascade-paused parent: the paused generation is SUPERSEDED and a FRESH one is dispatched,
    /// which is the shipped D13 park→resume shape exactly. The children are never resumed — D6 build item 8
    /// forbids it, because a child resumed from inside its parent would queue on the PARENT pool the parent is
    /// already holding, and with two concurrent headless parents that is a permanent deadlock.
    /// <para>
    /// Red before the <c>WaitingForInput or Paused</c> widening of the stale-child settle: by the time the
    /// resumed parent supersedes them, the paused children's dispatches have RETURNED, so they are not in
    /// <c>_inflight</c> and <c>CancelAsync</c> cannot reach them (the launcher double reproduces that exactly).
    /// Without the widening they stay <c>Paused</c> forever, each owning a visible stub chat — the very leak
    /// the row-level settle exists to prevent, surviving exactly the restart path its comment names.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// The RESTART shape of the same leak, and the narrowest red demo for the stale-child widening: a child
    /// left <c>Paused</c> by a PREVIOUS PROCESS — nothing here has ever heard of it, so <c>CancelAsync</c> is a
    /// no-op against it and states at or above <c>WaitingForInput</c> are deliberately never swept. Without the
    /// row-level settle it lingers forever with its own stub chat while its parent runs to completion.
    /// <para>
    /// Its sibling fact for the budget park (<c>WaitingForInput</c>) is
    /// <c>AgentRunOrchestratorFanOutTests.APreviousGenerationsParkedChildIsSupersededBeforeANewDispatch</c>;
    /// this is the <c>Paused(4)</c> half, which Batch 08 makes reachable on demand.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// D6 build item 8, from the outside: a cascade never puts a child on the PARENT's slot pool. The resumed
    /// parent re-dispatches through <c>LaunchChildAsync</c> — which in the real launcher is the one call that
    /// selects <c>_childSlots</c>, exactly as <c>LaunchAsync</c> is the one that selects <c>_slots</c> — and the
    /// paused children are superseded rather than resumed.
    /// <para>
    /// Why the method IS the claim: the pools are private to <c>HeadlessRunLauncher</c> and are chosen at
    /// <c>LaunchAsync</c>/<c>LaunchChildAsync</c> and nowhere else, so the observable form of "nothing nested
    /// waits on the pool it holds" is that no child was ever launched top-level and no paused child was ever
    /// re-dispatched under its own id (which is what resuming one through <c>IAgentRunResumeService</c> — the
    /// path that would take <c>_slots</c> — would look like from here).
    /// </para>
    /// </summary>
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

    /// <summary>
    /// <b>GUARD.</b> The widened per-child arm must not hijack the BUDGET park. Here a child pauses itself (the
    /// user pausing a child from the child's own chat) while NO pause request stands against the parent, so the
    /// parent takes the existing <c>ChildrenParkedReason</c> shape byte for byte: <c>WaitingForInput</c>, not
    /// <c>Paused</c>, and not the user reason.
    /// <para>
    /// Without it, widening the arm to accept <c>Paused</c> would be pinned only in the presence of a parent
    /// request, and a consume moved one line up — or peeked instead of consumed — would go unnoticed.
    /// </para>
    /// </summary>
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

    // ---- Batch 08 F2/F1: the two windows in which a pause used to settle the run TERMINALLY ----
    //
    // Stated once for the four facts below, because it is the reason the shipped suite missed both. EVERY
    // cascade fact above waits for WaitingForChildren before it pauses. The row does not say that until the
    // fan-out's dispatch prologue has finished — superseding the previous generation, then one
    // LaunchChildAsync per sibling — so the whole prologue, where the row still reads Running, is
    // STRUCTURALLY OUTSIDE those facts. These four pause INSIDE it, held there by a gate the fact controls
    // rather than by a sleep.

    /// <summary>
    /// <b>THE BATCH'S CENTRAL CLAIM, in the window that broke it.</b> A pause landing inside the fan-out
    /// DISPATCH PROLOGUE leaves a run <c>Paused</c> and resumable — not <c>Cancelled</c> with
    /// <c>CompletedAt</c> stamped.
    /// <para>
    /// Red before the fix, and the review reproduced exactly this by execution:
    /// <c>ROW-IN-PROLOGUE=Running ACCEPTED=True FINAL=Cancelled COMPLETEDAT=SET RESUMABLE=False</c> with
    /// <c>STEPS=[s1:Done,g1:Done,g2:Done]</c> — i.e. both children finished their work and the run was thrown
    /// away terminally anyway. D6's "never fire a fan-out parent's own token" was keyed on the persisted row
    /// reading <c>WaitingForChildren</c>; in this window it reads <c>Running</c>, so the pause took the
    /// FireCancel branch, the fan-out read its own cancelled token and reported the run cancelled, and the
    /// caller called <c>SafeFail(cancelled: true)</c>.
    /// </para>
    /// <para>
    /// The plan carries an ORDINARY step before the group on purpose. On the first step of a fresh launch the
    /// row still reads <c>Planning</c> and the pause is refused outright, so a fact built on
    /// <c>MakeSteps(("a",1),("b",1))</c> would pass without ever entering the window. The
    /// <c>Running</c> assertion below is what keeps it honest.
    /// </para>
    /// </summary>
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

        // The whole shape the review's probe printed, inverted.
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

    /// <summary>
    /// The same window, HALF DISPATCHED — the shape a real fan-out spends most of its prologue in. One child is
    /// already live when the pause lands and the loop is still inside the launch loop for the next one. The
    /// cascade must reach the live child and the parent's own token must still never fire.
    /// <para>
    /// Red before the fix for the same reason as its sibling above (the row reads <c>Running</c>, so the pause
    /// fired the parent), with the extra evidence that the already-dispatched child was revoked and cancelled by
    /// the parent's own <c>cts.Token.Register</c> callback rather than paused.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// <b>F1.</b> A cascade must not fire at a child outside the PAUSABLE set. Here one child is live inside its
    /// step and the other is still in its PLANNING turn — the longest single phase of a child's life, and the
    /// state the review calls the most likely real-world timing in the whole batch.
    /// <para>
    /// Red before the fix, executed by the review's lens 6: the cascade filtered terminal-versus-not, so the
    /// <c>Planning</c> child was cancelled, its own loop consumed the request, and <c>TryPauseUserAsync</c>'s CAS
    /// then LOST because <c>Planning</c> is not one of its source states — leaving the child stranded
    /// non-terminal with no dispatch behind it. The parent's settle loop charged that as
    /// <c>"child run did not settle"</c>, recorded both sibling steps <c>Failed</c>, burnt a replan and settled
    /// the parent terminally <c>Failed</c>.
    /// </para>
    /// <para>
    /// The claim is asserted at the MECHANISM — the held child's cancel sink was never invoked — and then at the
    /// consequence, because the downstream chain has four links and only the first is the edit.
    /// </para>
    /// </summary>
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

        // Neither child is destroyed: one parked through its own D1 abort, the other simply ran.
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

    /// <summary>
    /// F1 at the edit itself, and the cheapest place it can be pinned: the cascade fires at exactly the members
    /// of the PAUSABLE set and at nothing else. A table rather than a scenario, so the seventh state someone
    /// adds to <see cref="AgentRunState"/> has to be classified here deliberately.
    /// <para>
    /// The two rows that matter are <c>Planning</c> — a child mid-plan, and also the shape of one still QUEUED
    /// behind the child slot pool, which the real launcher settles <c>Cancelled(7)</c> when its token fires
    /// before it starts — and the two parked states, which have already stopped and must not be re-fired at.
    /// </para>
    /// </summary>
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
}
