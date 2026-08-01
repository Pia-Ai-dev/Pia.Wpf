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
/// Batch 08 G3 — the pause/cancel DISCRIMINATOR and the <c>Pending</c> restore, on the HEADLESS shape (the
/// executor returns a cancelled <c>StepTurnResult</c> rather than throwing, which is what
/// <c>HeadlessTurnExecutor</c>'s cancel arm does). Live parity is G4's file; the pause is not yet reachable
/// from any UI on either executor, which is what makes the split legal.
/// <para>
/// The whole batch turns on one property, so every fact here asserts some part of it: <b>a user pause must
/// leave a RESUMABLE run, not a Cancelled one.</b> The failure is silent in the sense that the run does settle —
/// just terminally, with <c>CompletedAt</c> stamped by <c>FailAsync</c>. The four-part shape is
/// <c>State == Paused</c> · <c>CompletedAt is null</c> · the aborted step back at <c>Pending</c> ·
/// <c>RunPauseEnvelope.ReadReason(run) == UserPausedReason</c>, and then the run is actually RESUMED and
/// asserted to complete: a fact that only checks the state has not checked the thing.
/// </para>
/// <para>
/// These drive the real <see cref="AgentRunOrchestrator"/> against a real SQLite <see cref="AgentRunService"/>
/// and the real <see cref="RunSteeringStore"/> + <see cref="AgentRunSteeringService"/> — the pause is requested
/// the way the UI will request it (through the service, which also exercises its state pre-check), and the
/// dispatch's cancel sink is registered the way <c>HeadlessRunLauncher</c> registers it.
/// </para>
/// </summary>
public sealed class AgentRunOrchestratorUserPauseTests
{
    private static Persona Persona() => new() { Name = "Pia", SystemPrompt = "sys" };

    private static AiProvider Provider() => new() { Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };

    private static StepTurnResult Ok(string text = "done") =>
        new(true, false, null, text, null, Guid.NewGuid(), Guid.NewGuid());

    private static List<AgentStep> MakeSteps(params string[] intents)
    {
        var result = new List<AgentStep>();
        for (var i = 0; i < intents.Length; i++)
        {
            result.Add(new AgentStep
            {
                Id = Guid.Empty,
                Ordinal = i,
                Title = intents[i],
                Intent = intents[i],
                Status = AgentStepStatus.Pending,
            });
        }

        return result;
    }

    private static (long In, long Out, int PerStep) Ledger(AgentRun run)
    {
        using var doc = JsonDocument.Parse(run.LedgerJson!);
        var root = doc.RootElement;
        return (root.GetProperty("inputTokens").GetInt64(), root.GetProperty("outputTokens").GetInt64(),
            root.GetProperty("perStep").GetArrayLength());
    }

    private sealed class FakePlanner : IAgentPlanner
    {
        public Queue<PlanResult> Plans { get; } = new();

        public int ReplanCalls { get; private set; }

        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(Plans.Count > 0 ? Plans.Dequeue() : PlanResult.Fallback);

        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
        {
            ReplanCalls++;
            return Task.FromResult(PlanResult.Fallback);
        }
    }

    /// <summary>
    /// A step body that models what really happens when the user presses Pause mid-step: the steering service
    /// records the intent and fires this dispatch's cancel sink, and the step then unwinds the way its executor
    /// unwinds. Three unwind shapes, because the loop has to handle all three:
    /// <list type="bullet">
    /// <item><see cref="Unwind.Cancelled"/> — honour the token and report <c>Cancelled: true</c>
    /// (<c>HeadlessTurnExecutor</c>'s cancel arm).</item>
    /// <item><see cref="Unwind.DeclinedTool"/> — report <c>Succeeded: false, Cancelled: false</c>, which is what
    /// a RELEASED action card produces on Live: the release maps to <c>ToolDecision.Decline</c> and the exchange
    /// CONTINUES. Without the pause branch this shape replans.</item>
    /// <item><see cref="Unwind.Throws"/> — throw the OCE instead of returning, which is what an abort inside the
    /// per-step persona resolve does (it leaves the step row <c>Running</c>) and what Live's second escape hatch
    /// does.</item>
    /// </list>
    /// </summary>
    private sealed class PausingExecutor : IAgentTurnExecutor
    {
        internal enum Unwind { Cancelled, DeclinedTool, Throws }

        private readonly Func<Guid, Task<bool>>? _pause;
        private readonly Unwind _unwind;
        private readonly string _pauseOnIntent;

        public PausingExecutor(string pauseOnIntent, Func<Guid, Task<bool>>? pause, Unwind unwind = Unwind.Cancelled)
        {
            _pauseOnIntent = pauseOnIntent;
            _pause = pause;
            _unwind = unwind;
        }

        /// <summary>Usage the ABORTED step reports. Null models both real executors today (they report none on a
        /// cancel); non-null is the day one of them reports partial spend, which D2 says is still billed.</summary>
        public UsageDetails? AbortedUsage { get; set; }

        /// <summary>Usage every SUCCESSFUL step reports. A step's per-step ledger entry is only written when it
        /// carries usage, so a fact that counts those entries needs this to have anything to count.</summary>
        public UsageDetails? StepUsage { get; set; }

        /// <summary>Message ids the aborted step reports, so "the aborted step was not recorded" is provable
        /// rather than vacuous — a result with empty ids could not have written them anyway.</summary>
        public Guid AbortedFirstMessageId { get; } = Guid.NewGuid();

        public Guid AbortedLastMessageId { get; } = Guid.NewGuid();

        public List<string> Executed { get; } = new();

        public bool PausedCalled { get; private set; }

        public bool EndCalled { get; private set; }

        public bool EndCancelled { get; private set; }

        /// <summary>What <c>IAgentRunSteeringService.PauseAsync</c> returned — asserted, so a fact can never pass
        /// on a pause that was refused (the run would then just be cancelled, and "not Paused" is the pass
        /// condition of nothing here).</summary>
        public bool? PauseAccepted { get; private set; }

        public Task BeginRunAsync(AgentRun run, RunContext ctx, CancellationToken ct) => Task.CompletedTask;

        public async Task<StepTurnResult> ExecuteStepAsync(AgentRun run, AgentStep step, RunContext ctx, CancellationToken ct)
        {
            Executed.Add(step.Intent ?? step.Title);
            if (step.Intent != _pauseOnIntent || _pause is null)
                return new StepTurnResult(true, false, null, "done", StepUsage, Guid.NewGuid(), Guid.NewGuid());

            PauseAccepted = await _pause(run.Id);

            if (_unwind == Unwind.DeclinedTool)
            {
                // No token honouring at all: the exchange kept going after the card was declined.
                return new StepTurnResult(false, false, "User declined the write_file operation.", string.Empty,
                    AbortedUsage, AbortedFirstMessageId, AbortedLastMessageId);
            }

            try
            {
                await Task.Delay(Timeout.Infinite, ct); // the sink's cancel reaches the in-flight step (R13)
            }
            catch (OperationCanceledException)
            {
                if (_unwind == Unwind.Throws)
                    throw;
            }

            return new StepTurnResult(false, true, "cancelled", string.Empty,
                AbortedUsage, AbortedFirstMessageId, AbortedLastMessageId);
        }

        public Task<StepTurnResult> RunSingleTurnFallbackAsync(AgentRun run, RunContext ctx, CancellationToken ct)
            => Task.FromResult(Ok("fallback"));

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

    private sealed class Harness : IDisposable
    {
        private readonly string _dir;

        public Harness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "PiaUserPause_" + Guid.NewGuid().ToString("N"));
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

        /// <param name="steering">Omitted ⇒ the pre-Batch-08 loop: no request can ever be consumed.</param>
        public AgentRunOrchestrator BuildOrchestrator(
            IAgentPlanner planner, IAgentVerifier? verifier = null, IRunSteeringStore? steering = null) =>
            new(Runs, planner, verifier ?? new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance,
                workspaces: null, childLauncher: null, chats: null, steering: steering);

        public void Dispose()
        {
            Runs.Dispose();
            Ctx.Dispose();
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// THE headline fact. A pause landing mid-step leaves the four-part resumable shape, and the run then
    /// actually RESUMES and completes. If the pause branch were gated on <c>r.Cancelled</c> after
    /// <c>SafeRecordStep</c>, or if it took <c>SafeFail(cancelled: true)</c>, the state would be
    /// <c>Cancelled</c> with a stamped <c>CompletedAt</c> and there would be nothing left to resume.
    /// </summary>
    [Fact]
    public async Task UserPause_MidStep_LeavesTheRunResumable_OnHeadless()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1", "s2"), false));

        using var dispatchCts = new CancellationTokenSource();
        Action sink = () => { try { dispatchCts.Cancel(); } catch { /* disposed */ } };
        h.Store.RegisterDispatch(run.Id, sink); // exactly what HeadlessRunLauncher's dispatch does

        var exec = new PausingExecutor("s1", runId => h.Steering.PauseAsync(runId, ct));
        await h.BuildOrchestrator(planner, steering: h.Store)
            .RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, dispatchCts.Token);

        Assert.True(exec.PauseAccepted);          // the pause was accepted, not refused: this fact is not vacuous
        var paused = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Paused, paused!.State);                              // (1) not Cancelled
        Assert.Null(paused.CompletedAt);                                                // (2) not settled
        Assert.Equal(AgentRunService.UserPausedReason, RunPauseEnvelope.ReadReason(paused)); // (4) a USER pause
        var aborted = Assert.Single(paused.Plan, s => s.Title == "s1");
        Assert.Equal(AgentStepStatus.Pending, aborted.Status);                          // (3) back in the plan
        // …and visible to the drain the resume uses. Failed(3) — what SafeRecordStep would have written — is
        // invisible to this query AND dropped by KeepDoneAsync, so this is the assertion that "Pending" is not
        // merely a nicer-looking number in a column.
        var next = await h.Runs.NextPendingStepAsync(run.Id, ct);
        Assert.Equal("s1", next!.Title);
        Assert.True(exec.PausedCalled);   // the NON-terminal executor release ran
        Assert.False(exec.EndCalled);     // …and the terminal one did not (guardrail 5)

        // NOW RESUME IT, the way the launcher does: claim from Paused, then re-enter with resume: true.
        Assert.True(await h.Runs.TryResumeFromPauseAsync(run.Id, ct));
        var resumed = (await h.Runs.GetAsync(run.Id, ct))!;
        var second = new PausingExecutor("never", pause: null);
        await h.BuildOrchestrator(new FakePlanner(), steering: h.Store)
            .RunAsync(resumed, second, Persona(), Provider(), RunProfile.Interactive, ct, resume: true);

        Assert.Equal(new[] { "s1", "s2" }, second.Executed); // the aborted step RE-RAN, and so did its successor
        var final = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.NotNull(final.CompletedAt);
        Assert.All(final.Plan, s => Assert.Equal(AgentStepStatus.Done, s.Status));
    }

    /// <summary>
    /// D2 made literal: the aborted step's TEXT is discarded, so the step re-runs clean. The branch returns
    /// BEFORE <c>SafeRecordStep</c>, so the row keeps no <c>First</c>/<c>LastMessageId</c> and gains no per-step
    /// ledger entry — and the run-level message range still points at the step that really finished, not at a
    /// transcript slice the resumed step will rewrite.
    /// </summary>
    [Fact]
    public async Task UserPause_DoesNotRecordTheAbortedStep()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1", "s2"), false));

        using var dispatchCts = new CancellationTokenSource();
        Action sink = () => { try { dispatchCts.Cancel(); } catch { /* disposed */ } };
        h.Store.RegisterDispatch(run.Id, sink);

        // s1 succeeds (so the run really has a recorded step to compare against), s2 is the aborted one.
        var exec = new PausingExecutor("s2", runId => h.Steering.PauseAsync(runId, ct))
        {
            StepUsage = new UsageDetails { InputTokenCount = 11, OutputTokenCount = 3 },
        };
        await h.BuildOrchestrator(planner, steering: h.Store)
            .RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, dispatchCts.Token);

        Assert.True(exec.PauseAccepted);
        var paused = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Paused, paused!.State);

        var aborted = Assert.Single(paused.Plan, s => s.Title == "s2");
        Assert.Equal(AgentStepStatus.Pending, aborted.Status);
        Assert.Null(aborted.FirstMessageId);
        Assert.Null(aborted.LastMessageId);
        Assert.Equal(AgentStepStatus.Done, Assert.Single(paused.Plan, s => s.Title == "s1").Status);

        // ONE per-step ledger entry — s1's. SafeRecordStep is where a step's entry is written, and it never ran
        // for s2. (The count is the non-vacuity control: an empty ledger would satisfy "no entry for s2".)
        Assert.Equal(1, Ledger(paused).PerStep);

        // The run-level slice is s1's, not the aborted step's: its ids were never folded in.
        Assert.NotEqual(exec.AbortedFirstMessageId, paused.FirstMessageId);
        Assert.NotEqual(exec.AbortedLastMessageId, paused.LastMessageId);
    }

    /// <summary>
    /// <b>The W3 fact.</b> On Live, a pause releases a pending action card — and that release is a
    /// <c>ToolDecision.Decline</c>, not a cancellation: the exchange CONTINUES and can return
    /// <c>Succeeded: false, Cancelled: false</c>. So the pause branch may not be gated on <c>r.Cancelled</c>,
    /// and it is not: the consumed REQUEST alone decides. Gate it the other way and the user presses Pause and
    /// the run REPLANS around work it thinks failed — which <c>ReplanCalls</c> below is what catches.
    /// </summary>
    [Fact]
    public async Task UserPause_WhoseStepReturnsSucceededFalseAndCancelledFalse_StillPauses()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1"), false));

        using var dispatchCts = new CancellationTokenSource();
        Action sink = () => { try { dispatchCts.Cancel(); } catch { /* disposed */ } };
        h.Store.RegisterDispatch(run.Id, sink);

        var exec = new PausingExecutor("s1", runId => h.Steering.PauseAsync(runId, ct),
            PausingExecutor.Unwind.DeclinedTool);
        await h.BuildOrchestrator(planner, steering: h.Store)
            .RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, dispatchCts.Token);

        Assert.True(exec.PauseAccepted);
        var paused = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Paused, paused!.State);
        Assert.Null(paused.CompletedAt);
        Assert.Equal(AgentRunService.UserPausedReason, RunPauseEnvelope.ReadReason(paused));
        Assert.Equal(AgentStepStatus.Pending, Assert.Single(paused.Plan).Status);
        Assert.Equal(0, planner.ReplanCalls); // NOT the replan arm
        Assert.True(exec.PausedCalled);
        Assert.False(exec.EndCalled);
    }

    /// <summary>
    /// The SECOND consume site. An abort can leave the loop by THROWING rather than returning a result — an OCE
    /// out of the per-step persona resolve (awaited before either executor's exchange try/catch, so the step row
    /// is left <c>Running(1)</c>), and Live's second escape hatch. The <c>catch (OperationCanceledException)</c>
    /// arm has to restore the step from the hoisted id, because <c>step</c> is not in scope there.
    /// <para>
    /// Not in §8's fact table; added because this commit adds that arm and G4 would otherwise be the first
    /// place it is exercised — for Live only.
    /// </para>
    /// </summary>
    [Fact]
    public async Task UserPause_WhoseStepThrowsInsteadOfReturning_AlsoLeavesTheRunResumable()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1", "s2"), false));

        using var dispatchCts = new CancellationTokenSource();
        Action sink = () => { try { dispatchCts.Cancel(); } catch { /* disposed */ } };
        h.Store.RegisterDispatch(run.Id, sink);

        var exec = new PausingExecutor("s1", runId => h.Steering.PauseAsync(runId, ct),
            PausingExecutor.Unwind.Throws);
        await h.BuildOrchestrator(planner, steering: h.Store)
            .RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, dispatchCts.Token);

        Assert.True(exec.PauseAccepted);
        var paused = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Paused, paused!.State);
        Assert.Null(paused.CompletedAt);
        Assert.Equal(AgentRunService.UserPausedReason, RunPauseEnvelope.ReadReason(paused));
        // The step was left Running(1) by the throw and is restored from the hoisted id.
        Assert.Equal(AgentStepStatus.Pending, Assert.Single(paused.Plan, s => s.Title == "s1").Status);
        Assert.Equal("s1", (await h.Runs.NextPendingStepAsync(run.Id, ct))!.Title);
        Assert.True(exec.PausedCalled);
        Assert.False(exec.EndCalled); // a pause is not terminal, so no EndRun even on the throw path
    }

    /// <summary>
    /// D2, both halves. The tokens the aborted step already spent are BILLED — run-level (<c>stepId: null</c>),
    /// because a per-step entry belongs to a step that finished and this one will re-run. And when the executor
    /// reports NO usage (which is what both real executors do on a cancel today) nothing is written: no
    /// estimate, no synthesized number, no fallback.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UserPause_BillsTheRunLevelLedger_AndSynthesizesNothingWhenUsageIsNull(bool reportsUsage)
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1"), false));

        using var dispatchCts = new CancellationTokenSource();
        Action sink = () => { try { dispatchCts.Cancel(); } catch { /* disposed */ } };
        h.Store.RegisterDispatch(run.Id, sink);

        var exec = new PausingExecutor("s1", runId => h.Steering.PauseAsync(runId, ct))
        {
            AbortedUsage = reportsUsage ? new UsageDetails { InputTokenCount = 40, OutputTokenCount = 7 } : null,
        };
        await h.BuildOrchestrator(planner, steering: h.Store)
            .RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, dispatchCts.Token);

        Assert.True(exec.PauseAccepted);
        var paused = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Paused, paused!.State);
        var ledger = Ledger(paused);
        Assert.Equal(reportsUsage ? 40 : 0, ledger.In);
        Assert.Equal(reportsUsage ? 7 : 0, ledger.Out);
        Assert.Equal(0, ledger.PerStep); // never a per-step entry for a step that will re-run
    }

    /// <summary>
    /// The no-regression guardrail. With no pause request, a cancel is a STOP and settles exactly as it did
    /// before this batch: <c>Cancelled</c>, <c>CompletedAt</c> stamped, <c>EndRunAsync(cancelled: true)</c>.
    /// A dispatch IS registered here, so the difference is the request and nothing else.
    /// </summary>
    [Fact]
    public async Task GenuineCancel_StillSettlesCancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1", "s2"), false));

        using var dispatchCts = new CancellationTokenSource();
        Action sink = () => { try { dispatchCts.Cancel(); } catch { /* disposed */ } };
        h.Store.RegisterDispatch(run.Id, sink);

        // The Stop shape: fire the sink WITHOUT recording an intent.
        var exec = new PausingExecutor("s1", runId => { h.Store.FireCancel(runId); return Task.FromResult(false); });
        await h.BuildOrchestrator(planner, steering: h.Store)
            .RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, dispatchCts.Token);

        var final = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Cancelled, final!.State);
        Assert.NotNull(final.CompletedAt);
        Assert.True(exec.EndCalled);
        Assert.True(exec.EndCancelled);
        Assert.False(exec.PausedCalled);
        Assert.Equal(new[] { "s1" }, exec.Executed); // s2 never dispatched
    }

    /// <summary>
    /// Collision hardening 2, and Batch 08 F3's OWNERSHIP RULE from the side that must be refused: <b>a request
    /// recorded against a PREVIOUS dispatch must not abort the first step of the next one.</b> The launcher's
    /// per-run entry is overwritten by a resume while the old dispatch is still unwinding, and a
    /// <c>!started</c> arm can leave a request behind, so this is reachable.
    /// <para>
    /// TWO dispatches are constructed here on purpose. Until F3 this fact registered ONE sink, recorded against
    /// it and then ran <c>RunAsync</c> for that same registration — which under the ownership rule is not a
    /// stale request at all but a live one belonging to the dispatch that is running, i.e. the fact's body
    /// asserted the F3 defect (blind revoke) as correct behaviour while its name described a scenario it never
    /// built. Its twin below is the other side.
    /// </para>
    /// </summary>
    [Fact]
    public async Task StalePauseRequest_FromAPreviousDispatch_IsNotHonoured()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1", "s2"), false));

        // Dispatch A, and a pause it never consumed.
        using var dispatchA = new CancellationTokenSource();
        Action sinkA = () => { try { dispatchA.Cancel(); } catch { /* disposed */ } };
        h.Store.RegisterDispatch(run.Id, sinkA);
        Assert.True(h.Store.RecordPauseRequest(run.Id));

        // Dispatch B supersedes A — the resume registering its own sink, exactly as HeadlessRunLauncher does
        // before it schedules the loop. The drop happens HERE, at the boundary, not inside the loop.
        using var dispatchB = new CancellationTokenSource();
        Action sinkB = () => { try { dispatchB.Cancel(); } catch { /* disposed */ } };
        h.Store.RegisterDispatch(run.Id, sinkB);

        var exec = new PausingExecutor("never", pause: null);
        await h.BuildOrchestrator(planner, steering: h.Store)
            .RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, dispatchB.Token);

        var final = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.Equal(new[] { "s1", "s2" }, exec.Executed);   // A's intent aborted neither step of B
        Assert.False(exec.PausedCalled);
        Assert.False(dispatchA.IsCancellationRequested);     // nothing fired A's sink either
        Assert.False(h.Store.TryConsumePauseRequest(run.Id)); // dropped, not merely ignored
    }

    /// <summary>
    /// <b>Batch 08 F3 — the twin, and the leg that was broken.</b> Continue, then Pause a beat later: the
    /// resume CAS has already put the row at <c>Running</c> and the panel's Pause button is live, so the
    /// request is accepted and fires the RESUME's own sink — and it must then be honoured by the very dispatch
    /// it was aimed at, not thrown away by it.
    /// <para>
    /// Before the fix the loop revoked any request for its own run id on entry, which discarded this one while
    /// the cancel it had fired stood: the first step came back cancelled with nothing to consume, and
    /// <c>SafeFail(cancelled: true)</c> settled the run <c>Cancelled</c> with <c>CompletedAt</c> stamped and no
    /// claim path back — after <c>PauseAsync</c> had already returned <c>true</c> to the user. The review
    /// executed exactly that through the real launcher (<c>FINAL state=Cancelled … resumable=False</c>).
    /// </para>
    /// <para>
    /// Deterministic, not timed: the request is recorded strictly BEFORE <c>RunAsync</c> is entered, which is
    /// the whole window. No sleep, no gate.
    /// </para>
    /// </summary>
    [Fact]
    public async Task APauseInTheResumeRampUp_IsHonouredByTheDispatchItFired_NotRevokedByIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1", "s2"), false));

        // Dispatch A: a fresh launch the user pauses mid-step, so the run really is Paused and really is the
        // kind of run a Continue claims.
        using var dispatchA = new CancellationTokenSource();
        Action sinkA = () => { try { dispatchA.Cancel(); } catch { /* disposed */ } };
        h.Store.RegisterDispatch(run.Id, sinkA);
        var first = new PausingExecutor("s1", runId => h.Steering.PauseAsync(runId, ct));
        await h.BuildOrchestrator(planner, steering: h.Store)
            .RunAsync(run, first, Persona(), Provider(), RunProfile.Interactive, dispatchA.Token);
        Assert.Equal(AgentRunState.Paused, (await h.Runs.GetAsync(run.Id, ct))!.State);

        // CONTINUE. The claim CAS moves the row to Running, then the launcher registers the resume's sink —
        // both before the loop is even scheduled (HeadlessRunLauncher.ResumeAsync: the CAS, then
        // RegisterDispatch, then Task.Run).
        Assert.True(await h.Runs.TryResumeFromPauseAsync(run.Id, ct));
        var resumed = (await h.Runs.GetAsync(run.Id, ct))!;
        using var dispatchB = new CancellationTokenSource();
        Action sinkB = () => { try { dispatchB.Cancel(); } catch { /* disposed */ } };
        h.Store.RegisterDispatch(run.Id, sinkB);

        // PAUSE, inside the ramp-up. The row reads Running, so the panel's button is live and the service
        // accepts — through the real steering service, i.e. the real pre-check and the real record-then-fire.
        Assert.Equal(AgentRunState.Running, (await h.Runs.GetAsync(run.Id, ct))!.State);
        Assert.True(await h.Steering.PauseAsync(run.Id, ct));
        // It fired THIS dispatch's token, which is what makes the request B's to honour. (dispatchA is already
        // cancelled from leg 1's own pause, so it says nothing here.)
        Assert.True(dispatchB.IsCancellationRequested);

        // NOW the loop starts, on the token the pause already cancelled. The delegate below does not pause
        // again — the pause already happened; it only routes the step into the token-honouring unwind a real
        // executor performs when it is handed an already-cancelled token.
        var second = new PausingExecutor("s1", _ => Task.FromResult(true));
        await h.BuildOrchestrator(new FakePlanner(), steering: h.Store)
            .RunAsync(resumed, second, Persona(), Provider(), RunProfile.Interactive, dispatchB.Token, resume: true);

        var final = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Paused, final!.State);                                    // NOT Cancelled
        Assert.Null(final.CompletedAt);                                                      // not settled
        Assert.Equal(AgentRunService.UserPausedReason, RunPauseEnvelope.ReadReason(final));  // a USER pause
        Assert.All(final.Plan, s => Assert.Equal(AgentStepStatus.Pending, s.Status));        // nothing lost
        Assert.True(second.PausedCalled);   // the non-terminal release …
        Assert.False(second.EndCalled);     // … and not the terminal one (guardrail 5)

        // Resumable for real, which is the property the whole batch rests on: claim it and drain it.
        Assert.True(await h.Runs.TryResumeFromPauseAsync(run.Id, ct));
        var again = (await h.Runs.GetAsync(run.Id, ct))!;
        var third = new PausingExecutor("never", pause: null);
        await h.BuildOrchestrator(new FakePlanner(), steering: h.Store)
            .RunAsync(again, third, Persona(), Provider(), RunProfile.Interactive, ct, resume: true);
        Assert.Equal(new[] { "s1", "s2" }, third.Executed);
        Assert.Equal(AgentRunState.Completed, (await h.Runs.GetAsync(run.Id, ct))!.State);
    }

    /// <summary>
    /// The additive property the whole batch rests on: a null steering store makes this loop byte-for-byte the
    /// pre-Batch-08 one. A request can exist in a store the loop does not have, and the cancel is still a plain
    /// cancel — which is what keeps a dozen positional test constructions, and every build that has not
    /// registered the singleton, on exactly their old behaviour.
    /// </summary>
    [Fact]
    public async Task NullSteeringStore_BehavesExactlyAsBeforeThisBatch()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1", "s2"), false));

        using var dispatchCts = new CancellationTokenSource();
        Action sink = () => { try { dispatchCts.Cancel(); } catch { /* disposed */ } };
        h.Store.RegisterDispatch(run.Id, sink);

        // A REAL pause request, through the real service — the loop just cannot see the store.
        var exec = new PausingExecutor("s1", runId => h.Steering.PauseAsync(runId, ct));
        await h.BuildOrchestrator(planner) // steering: null
            .RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, dispatchCts.Token);

        Assert.True(exec.PauseAccepted);
        var final = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Cancelled, final!.State);
        Assert.NotNull(final.CompletedAt);
        Assert.True(exec.EndCancelled);
        Assert.False(exec.PausedCalled);
        // The request is still sitting there unconsumed — nothing in this loop ever read it.
        Assert.True(h.Store.TryConsumePauseRequest(run.Id));
    }
}
