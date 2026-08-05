using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Providers;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// <b>Batch 18 G4 — spec §8.4: "a resumed <c>needs-goal</c> run re-plans; a resumed mid-plan run does not.
/// BOTH directions, in ONE test class, because §4.1's whole risk is that one guard now has to distinguish
/// them."</b> That sentence is why this file exists and why the two directions are not split across two files:
/// one of them alone would pass against a guard that always re-plans, or against a guard that never does.
/// <para>
/// <b>What this group deliberately broke.</b> <c>AgentRunOrchestrator.cs</c> carried Batch 08's D1 — "a resume
/// must NOT re-plan" — as a bare <c>if (!resume)</c> around the whole planning block, whose stated hazard is
/// that <c>ReplaceStepsAsync</c> "writes the plan verbatim and does not preserve Done steps". 18 D2 contradicts
/// it: a run that parked <c>needs-goal</c> has ZERO persisted step rows (the decline branch returns before
/// <c>SafeReplaceSteps</c>), so that hazard cannot apply to it — but the old guard could not tell. It now
/// re-plans on TWO conditions together, and the four facts in the first section below are the four corners of
/// that conjunction: token right + no rows (re-plans), token right + rows (does not), token wrong + no rows
/// (does not), and no token at all (does not).
/// </para>
/// <para>
/// <b>Relationship to <see cref="AgentRunResumeNoRePlanPremiseTests"/>.</b> That file measured the premise
/// BEFORE this change and stays green: every fixture in it resumes without a park reason, which is exactly the
/// fail-safe arm pinned here by <see cref="Resume_WithNoParkReason_DoesNotRePlan"/>. Its
/// <c>ZeroStepResume_PlansNothing_DrainsNothing_AndSettlesCompleted</c> characterisation is the behaviour
/// <see cref="Resume_NeedsGoalPark_WithNoStepRows_RePlans"/> inverts once the reason is actually known — read
/// the two together; the amended doc there says so from its side.
/// </para>
/// <para>
/// Absence is asserted on SPIES (planner call counts, <c>ReplaceStepsAsync</c> counts, the <c>SetStateAsync</c>
/// log), never inferred from an end state: a re-planning resume and a non-re-planning one can settle
/// identically, which is precisely how this went unmeasured until now. The store underneath is the real SQLite
/// <see cref="AgentRunService"/>, so the persisted-Pending re-query (R2) and the new
/// <c>AgentRuns.ClarificationsJson</c> column are exercised rather than faked.
/// </para>
/// <para>
/// net10.0-windows cannot execute on macOS — these tests are written, not run; execution is deferred to
/// Windows/CI.
/// </para>
/// </summary>
public sealed class AgentRunClarificationResumeTests
{
    /// <summary>
    /// The plan-time park token, as a LITERAL. Same discipline as <c>GoalGroundingReproTests</c> and
    /// <c>UnattendedApprovalParkTests</c>: these facts are about the WIRE value a parked row carries and a
    /// resume reads back off it, so a test that referenced <c>AgentRunOrchestrator.NeedsGoalReason</c> could not
    /// catch the constant itself being changed.
    /// </summary>
    private const string NeedsGoalReason = "needs-goal";

    /// <summary>The mid-plan park token (owner Q4's second one). 18 G5 writes it; this file only has to prove
    /// the resume guard tells it apart from <see cref="NeedsGoalReason"/>.</summary>
    private const string NeedsInputReason = "needs-input";

    private const string ThinGoal = "ggg";

    /// <summary>
    /// The user's answer. USER CONTENT: a literal here, and nothing in this file logs it — production may only
    /// put text like this through <c>SensitiveDebug</c> (CLAUDE.md).
    /// </summary>
    private const string FirstAnswer = "I mean the golden-gate gateway config";

    private const string SecondAnswer = "the staging one, not production";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Persona Persona() => new() { Id = Guid.NewGuid(), Name = "Pia", SystemPrompt = "sys" };

    private static AiProvider Provider() => new()
    {
        Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI, SupportsToolCalling = true,
    };

    private static StepTurnResult Ok(string text = "done") =>
        new(true, false, null, text, null, Guid.NewGuid(), Guid.NewGuid());

    /// <summary>Steps as a PLANNER hands them over: <c>Id = Guid.Empty</c>, so the store mints the ids.</summary>
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

    /// <summary>A step row as a PARKED run's plan already holds it, with its status — seeded directly so a zero
    /// call count on the resume means zero, not "one launch's calls minus one".</summary>
    private static AgentStep Persisted(int ordinal, string intent, AgentStepStatus status) => new()
    {
        Id = Guid.NewGuid(),
        Ordinal = ordinal,
        Title = intent,
        Intent = intent,
        Status = status,
    };

    // ---------------------------------------------------------------- the doubles

    /// <summary>
    /// Counts both planner entry points and — the addition this file needs — snapshots
    /// <c>ctx.Clarifications</c> as each call saw it. The seed is not observable from the goal argument:
    /// <c>PlanAsync</c> is still handed <c>ctx.Goal</c> verbatim (owner Q3 — the goal is never rewritten), and
    /// the answers travel BESIDE it on the context.
    /// </summary>
    private sealed class SpyPlanner : IAgentPlanner
    {
        public Queue<PlanResult> Plans { get; } = new();

        public int PlanCalls { get; private set; }

        public int ReplanCalls { get; private set; }

        public List<string> PlannedGoals { get; } = new();

        public List<IReadOnlyList<string>> SeenClarifications { get; } = new();

        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
        {
            PlanCalls++;
            PlannedGoals.Add(goal);
            SeenClarifications.Add(ctx.Clarifications.ToList());
            return Task.FromResult(Plans.Count > 0 ? Plans.Dequeue() : PlanResult.Fallback);
        }

        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
        {
            ReplanCalls++;
            return Task.FromResult(PlanResult.Fallback);
        }
    }

    /// <summary>
    /// Pass-through decorator over the REAL run store (delegation list lifted from
    /// <c>AgentRunResumeNoRePlanPremiseTests.SpyRunService</c>, which is private there) counting the two writes
    /// 08 D1's comment names as the hazard: <c>ReplaceStepsAsync</c>, and every <c>SetStateAsync</c> so
    /// "the run entered Planning" is observable.
    /// </summary>
    private sealed class SpyRunService : IAgentRunService
    {
        private readonly IAgentRunService _inner;

        public SpyRunService(IAgentRunService inner) => _inner = inner;

        public int ReplaceStepsCalls { get; private set; }

        /// <summary>
        /// Every <c>GetAsync</c> this dispatch made. The guard's condition (b) is one of them, which is what
        /// makes the SHORT-CIRCUIT observable at all — see
        /// <see cref="Launch_IgnoresTheParkReason_AndStillPlansExactlyOnce"/>, which compares two otherwise
        /// identical launches rather than pinning an absolute number (the loop makes several other reads, and
        /// a fact that counted them would red on any unrelated change to them).
        /// </summary>
        public int GetCalls { get; private set; }

        public List<AgentRunState> States { get; } = new();

        public Task ReplaceStepsAsync(Guid runId, IReadOnlyList<AgentStep> steps, CancellationToken ct = default)
        {
            ReplaceStepsCalls++;
            return _inner.ReplaceStepsAsync(runId, steps, ct);
        }

        public Task SetStateAsync(Guid runId, AgentRunState state, CancellationToken ct = default)
        {
            States.Add(state);
            return _inner.SetStateAsync(runId, state, ct);
        }

        public Task<AgentRun> CreateAsync(AgentRunCreateRequest request, CancellationToken ct = default) => _inner.CreateAsync(request, ct);
        public Task<AgentRun?> GetAsync(Guid runId, CancellationToken ct = default)
        {
            GetCalls++;
            return _inner.GetAsync(runId, ct);
        }

        public Task AddUsageAsync(Guid runId, Guid? stepId, UsageDetails usage, CancellationToken ct = default)
            => _inner.AddUsageAsync(runId, stepId, usage, ct);
        public Task SetRunMessageRangeAsync(Guid runId, Guid firstMessageId, Guid lastMessageId, CancellationToken ct = default)
            => _inner.SetRunMessageRangeAsync(runId, firstMessageId, lastMessageId, ct);
        public Task CompleteAsync(Guid runId, bool truncated = false, string? truncationReason = null, CancellationToken ct = default)
            => _inner.CompleteAsync(runId, truncated, truncationReason, ct);
        public Task FailAsync(Guid runId, string? error, bool cancelled = false, CancellationToken ct = default)
            => _inner.FailAsync(runId, error, cancelled, ct);
        public Task PauseAsync(Guid runId, string? reason, CancellationToken ct = default, string? approvalTool = null)
            => _inner.PauseAsync(runId, reason, ct, approvalTool);
        public Task UpdatePolicyJsonAsync(Guid runId, string? policyJson, CancellationToken ct = default)
            => _inner.UpdatePolicyJsonAsync(runId, policyJson, ct);
        public Task<IReadOnlyList<string>> AppendClarificationAsync(Guid runId, string? answer, CancellationToken ct = default)
            => _inner.AppendClarificationAsync(runId, answer, ct);
        public Task<bool> TryBeginResumeAsync(Guid runId, CancellationToken ct = default) => _inner.TryBeginResumeAsync(runId, ct);
        public Task<bool> TryPauseUserAsync(Guid runId, CancellationToken ct = default) => _inner.TryPauseUserAsync(runId, ct);
        public Task<bool> TryResumeFromPauseAsync(Guid runId, CancellationToken ct = default) => _inner.TryResumeFromPauseAsync(runId, ct);
        public Task BeginChildWaitAsync(Guid runId, int childCount, CancellationToken ct = default) => _inner.BeginChildWaitAsync(runId, childCount, ct);
        public Task<bool> TryEndChildWaitAsync(Guid runId, CancellationToken ct = default) => _inner.TryEndChildWaitAsync(runId, ct);
        public Task<int> FailInterruptedRunsAsync(CancellationToken ct = default) => _inner.FailInterruptedRunsAsync(ct);
        public Task<IReadOnlyList<AgentRun>> GetByChatAsync(Guid chatId, CancellationToken ct = default) => _inner.GetByChatAsync(chatId, ct);
        public Task<IReadOnlyList<AgentRun>> GetChildRunsAsync(Guid parentRunId, CancellationToken ct = default) => _inner.GetChildRunsAsync(parentRunId, ct);
        public Task<bool> ChatHasPlannedRunAsync(Guid chatId, CancellationToken ct = default) => _inner.ChatHasPlannedRunAsync(chatId, ct);
        public Task<bool> AnyExecutingRunForTriggerAsync(Guid triggerRef, CancellationToken ct = default) => _inner.AnyExecutingRunForTriggerAsync(triggerRef, ct);
        public Task<IReadOnlyList<ScheduledFiringOutcome>> GetLatestSettledFiringsAsync(CancellationToken ct = default) => _inner.GetLatestSettledFiringsAsync(ct);
        public Task<IReadOnlyList<ScheduledFiringOutcome>> GetFiringsForTriggerAsync(Guid triggerRef, int limit, CancellationToken ct = default)
            => _inner.GetFiringsForTriggerAsync(triggerRef, limit, ct);
        public Task<PlanMutationResult> ApplyPlanMutationAsync(Guid runId, IReadOnlyList<PlanStepEdit> pendingSteps, CancellationToken ct = default)
            => _inner.ApplyPlanMutationAsync(runId, pendingSteps, ct);
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

    private sealed class RecordingExecutor : IAgentTurnExecutor
    {
        public List<string> Executed { get; } = new();

        public bool FallbackCalled { get; private set; }

        public bool EndCalled { get; private set; }

        public bool PausedCalled { get; private set; }

        public Task BeginRunAsync(AgentRun run, RunContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task<StepTurnResult> ExecuteStepAsync(AgentRun run, AgentStep step, RunContext ctx, CancellationToken ct)
        {
            Executed.Add(step.Intent ?? step.Title);
            return Task.FromResult(Ok());
        }

        public Task<StepTurnResult> RunSingleTurnFallbackAsync(AgentRun run, RunContext ctx, CancellationToken ct)
        {
            FallbackCalled = true; // §4.2: a decline must never reach the R10 degrade, on a resume either
            return Task.FromResult(Ok("fallback"));
        }

        public Task EndRunAsync(AgentRun run, RunContext ctx, bool cancelled, bool failed, CancellationToken ct)
        {
            EndCalled = true;
            return Task.CompletedTask;
        }

        public Task OnPausedAsync(AgentRun run, RunContext ctx, CancellationToken ct)
        {
            PausedCalled = true; // the non-terminal park hook (guardrail 5)
            return Task.CompletedTask;
        }
    }

    /// <summary>Same shape as every other orchestrator fixture in this folder (each carries its own private
    /// copy), with the spy store threaded in so the plan writes are countable while the tests read and seed
    /// through the real one.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly string _dir;

        public Harness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "PiaClarifyResume_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Ctx = new SqliteContext(Path.Combine(_dir, "history.db"));
            Runs = new AgentRunService(Ctx, NullLogger<AgentRunService>.Instance);
            Chats = new AssistantChatService(Ctx, Runs);
        }

        public SqliteContext Ctx { get; }

        public AgentRunService Runs { get; }

        public AssistantChatService Chats { get; }

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

        /// <summary>Park the run with <paramref name="reason"/> and win the resume claim, exactly as
        /// <c>HeadlessRunLauncher.ResumeAsync</c> does — including the fact that the claim NULLs
        /// <c>ExtraJson</c>, which is why the reason has to travel as a parameter afterwards.</summary>
        public async Task ParkAndClaimAsync(Guid runId, string reason)
        {
            await Runs.PauseAsync(runId, reason, Ct);
            Assert.True(await Runs.TryBeginResumeAsync(runId, Ct));
        }

        public AgentRunOrchestrator BuildOrchestrator(IAgentPlanner planner, IAgentRunService runService) =>
            new(runService, planner, new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance,
                workspaces: null, childLauncher: null, chats: null, steering: null);

        public void Dispose()
        {
            Runs.Dispose();
            Ctx.Dispose();
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }
    }

    // ============================================================================================
    // §8.4, direction 1: a resumed needs-goal run RE-PLANS
    // ============================================================================================

    /// <summary>
    /// <b>THE FACT 18 D2 ADDS.</b> A resume whose park reason is <c>needs-goal</c> and whose run has zero
    /// persisted step rows re-enters the planning block: <c>PlanAsync</c> once, the plan written once, the row
    /// through <c>Planning</c>, and the emitted steps actually executed.
    /// <para>
    /// This is the exact fixture <c>AgentRunResumeNoRePlanPremiseTests.ZeroStepResume_PlansNothing_DrainsNothing
    /// _AndSettlesCompleted</c> measured before G4, where it settled <c>Completed</c> having planned nothing,
    /// drained nothing and run nothing — i.e. it answered the user's clarification by declaring their goal done.
    /// The ONE difference is that the resume now says why the run parked.
    /// </para>
    /// <para>
    /// <c>FallbackCalled</c> is asserted false because §4.2's hazard survives the resume: routing a
    /// clarification park into <c>RunSingleTurnFallbackAsync</c> would send the ungroundable goal as one
    /// ordinary chat turn and call whatever came back the run's result.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Resume_NeedsGoalPark_WithNoStepRows_RePlans()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync(ThinGoal);

        // No ReplaceStepsAsync: a plan-time decline returns before it, so the park has an EMPTY plan.
        await h.ParkAndClaimAsync(run.Id, NeedsGoalReason);
        Assert.Empty((await h.Runs.GetAsync(run.Id, Ct))!.Plan); // the premise of this fact, asserted

        var planner = new SpyPlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1", "s2"), false));
        var spy = new SpyRunService(h.Runs);
        var exec = new RecordingExecutor();
        var resumed = (await h.Runs.GetAsync(run.Id, Ct))!;

        await h.BuildOrchestrator(planner, spy).RunAsync(
            resumed, exec, Persona(), Provider(), RunProfile.Interactive, Ct,
            resume: true, parkReason: NeedsGoalReason);

        // THE CLAIM: the planning block ran, on a resume: true dispatch.
        Assert.Equal(1, planner.PlanCalls);
        Assert.Equal(1, spy.ReplaceStepsCalls);
        Assert.Contains(AgentRunState.Planning, spy.States);
        Assert.False(exec.FallbackCalled);

        // …and it was a REAL plan, not a plan written and ignored: both steps executed and the run settled.
        Assert.Equal(new[] { "s1", "s2" }, exec.Executed);
        Assert.True(exec.EndCalled);
        var final = await h.Runs.GetAsync(run.Id, Ct);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.Equal(2, final.Plan.Count);
        Assert.All(final.Plan, s => Assert.Equal(AgentStepStatus.Done, s.Status));
    }

    // ============================================================================================
    // §8.4, direction 2: a resumed mid-plan run does NOT re-plan — three ways of being direction 2
    // ============================================================================================

    /// <summary>
    /// <b>THE TOKEN IS WHAT DISCRIMINATES.</b> Identical fixture to
    /// <see cref="Resume_NeedsGoalPark_WithNoStepRows_RePlans"/> — same empty plan, same armed planner, same
    /// dispatch — with ONE character of difference: the park reason is <c>needs-input</c> (18 G5's mid-plan ask)
    /// instead of <c>needs-goal</c>. Nothing plans.
    /// <para>
    /// This pair is what §4.1 means by "one guard now has to distinguish them". A guard that re-planned on any
    /// clarification token, or on "no step rows" alone, passes the fact above and fails here.
    /// </para>
    /// <para>
    /// The zero-step <c>needs-input</c> shape is not hypothetical bookkeeping: G5's tool can park a run on its
    /// FIRST step, and the resume must land back in that partially executed step rather than throw the plan away.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Resume_NeedsInputPark_WithNoStepRows_DoesNotRePlan()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync(ThinGoal);
        await h.ParkAndClaimAsync(run.Id, NeedsInputReason);

        var planner = new SpyPlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("must-not-be-used"), false)); // armed, so silence is a CHOICE
        var spy = new SpyRunService(h.Runs);
        var exec = new RecordingExecutor();
        var resumed = (await h.Runs.GetAsync(run.Id, Ct))!;

        await h.BuildOrchestrator(planner, spy).RunAsync(
            resumed, exec, Persona(), Provider(), RunProfile.Interactive, Ct,
            resume: true, parkReason: NeedsInputReason);

        Assert.Equal(0, planner.PlanCalls);
        Assert.Equal(0, spy.ReplaceStepsCalls);
        Assert.DoesNotContain(AgentRunState.Planning, spy.States);
        Assert.Empty(exec.Executed);
        Assert.False(exec.FallbackCalled);
    }

    /// <summary>
    /// <b>THE STEP ROWS ARE WHAT DISCRIMINATE.</b> The token is right — <c>needs-goal</c> — but the run carries
    /// a Done row and a Pending row, so condition (b) fails and 08 D1 holds verbatim: no plan call, no
    /// <c>ReplaceStepsAsync</c>, and the Done row survives un-re-executed while only the Pending remainder
    /// drains (R2).
    /// <para>
    /// A run in this shape should not exist — the decline branch writes no steps — which is why the guard logs a
    /// warning for it. The fact is here anyway, because "should not exist" is not a guarantee, and this is the
    /// exact state in which re-planning would do what 08 D1's author was protecting against: <c>ReplaceStepsAsync</c>
    /// writes the plan verbatim and would delete the Done row.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Resume_NeedsGoalPark_ButStepRowsExist_DoesNotRePlan()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync(ThinGoal);
        await h.Runs.ReplaceStepsAsync(run.Id, new List<AgentStep>
        {
            Persisted(0, "s1", AgentStepStatus.Done),
            Persisted(1, "s2", AgentStepStatus.Pending),
        }, Ct);
        await h.ParkAndClaimAsync(run.Id, NeedsGoalReason);

        var planner = new SpyPlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("would-wipe-the-done-row"), false));
        var spy = new SpyRunService(h.Runs);
        var exec = new RecordingExecutor();
        var resumed = (await h.Runs.GetAsync(run.Id, Ct))!;

        await h.BuildOrchestrator(planner, spy).RunAsync(
            resumed, exec, Persona(), Provider(), RunProfile.Interactive, Ct,
            resume: true, parkReason: NeedsGoalReason);

        Assert.Equal(0, planner.PlanCalls);
        Assert.Equal(0, spy.ReplaceStepsCalls);
        Assert.DoesNotContain(AgentRunState.Planning, spy.States);

        // The hazard 08 D1 names, asserted as an outcome and not only as an absence: the Done row was neither
        // re-run nor deleted, and only the Pending remainder executed.
        Assert.Equal(new[] { "s2" }, exec.Executed);
        var final = await h.Runs.GetAsync(run.Id, Ct);
        Assert.Equal(2, final!.Plan.Count);
        Assert.Equal(new[] { "s1", "s2" }, final.Plan.OrderBy(s => s.Ordinal).Select(s => s.Title));
    }

    /// <summary>
    /// <b>18 G5's OWN shape, and the one this class was missing.</b>
    /// <see cref="Resume_NeedsInputPark_WithNoStepRows_DoesNotRePlan"/> above discriminates on the TOKEN using a
    /// zero-step fixture, which its own doc admits is the edge case (a run that asked on its very first step).
    /// The shape G5 actually produces is this one: a PARTIALLY EXECUTED plan — one Done row, and the row of the
    /// step that asked handed back to <c>Pending</c> — which spec §7 G5 calls "territory no existing resume path
    /// covers".
    /// <para>
    /// Both conditions of the guard fail here, and that is the point: the token is not <c>needs-goal</c> AND the
    /// run has step rows. 08 D1 therefore holds verbatim — no plan call, no <c>ReplaceStepsAsync</c>, the Done row
    /// neither re-run nor deleted, and only the asking step drains. A guard that re-planned on any clarification
    /// token would wipe the Done row through <c>ReplaceStepsAsync</c> and re-run the whole goal, which is exactly
    /// the hazard 08 D1's author named.
    /// </para>
    /// <para>
    /// The user's ANSWER is still recorded and still reaches the run — asserted here so that "does not re-plan"
    /// is not mistaken for "ignores the answer". The answer rides <c>ClarificationsJson</c> (persisted by
    /// <c>HeadlessRunLauncher.ResumeAsync</c> before the dispatch) and the transient nudge, and the re-run step
    /// sees the nudge on its instruction.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Resume_NeedsInputPark_WithAPartiallyExecutedPlan_DrainsTheRemainder_AndNeverRePlans()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("a real goal");
        await h.Runs.ReplaceStepsAsync(run.Id, new List<AgentStep>
        {
            Persisted(0, "s1", AgentStepStatus.Done),
            // The step that ASKED: the orchestrator's mid-plan-ask branch put it back to Pending rather than
            // recording it, so this is exactly what the row looks like when the resume arrives.
            Persisted(1, "s2", AgentStepStatus.Pending),
        }, Ct);
        await h.ParkAndClaimAsync(run.Id, NeedsInputReason);
        await h.Runs.AppendClarificationAsync(run.Id, FirstAnswer, Ct);

        var planner = new SpyPlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("would-wipe-the-done-row"), false)); // armed: silence is a CHOICE
        var spy = new SpyRunService(h.Runs);
        var exec = new RecordingExecutor();
        var resumed = (await h.Runs.GetAsync(run.Id, Ct))!;

        await h.BuildOrchestrator(planner, spy).RunAsync(
            resumed, exec, Persona(), Provider(), RunProfile.Interactive, Ct,
            resume: true, nudge: FirstAnswer, parkReason: NeedsInputReason);

        Assert.Equal(0, planner.PlanCalls);
        Assert.Equal(0, spy.ReplaceStepsCalls);
        Assert.DoesNotContain(AgentRunState.Planning, spy.States);
        Assert.False(exec.FallbackCalled);

        // Only the step that asked re-ran; the Done row survived un-re-executed and undeleted.
        Assert.Equal(new[] { "s2" }, exec.Executed);
        var final = await h.Runs.GetAsync(run.Id, Ct);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.Equal(2, final.Plan.Count);
        Assert.Equal(new[] { "s1", "s2" }, final.Plan.OrderBy(s => s.Ordinal).Select(s => s.Title));

        // The answer was not thrown away by the park it belongs to.
        Assert.Contains(FirstAnswer, RunClarifications.Read(final.ClarificationsJson));
    }

    /// <summary>
    /// <b>THE FAIL-SAFE DEFAULT.</b> A resume that says nothing about why the run parked keeps the pre-18
    /// behaviour, even on the exact fixture that WOULD re-plan if the token were supplied. This is what makes
    /// <c>parkReason</c>'s default safe, and it is why every pre-18 call site — and every existing test in
    /// <see cref="AgentRunResumeNoRePlanPremiseTests"/> — keeps meaning what it meant.
    /// <para>
    /// It is also the honest degrade for an unreadable pause envelope: <c>RunPauseEnvelope.ReadReason</c> answers
    /// null for a malformed or foreign document, and 08 D1 is the conservative side to land on.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Resume_WithNoParkReason_DoesNotRePlan()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync(ThinGoal);
        await h.ParkAndClaimAsync(run.Id, NeedsGoalReason); // the ROW parked needs-goal …

        var planner = new SpyPlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("must-not-be-used"), false));
        var spy = new SpyRunService(h.Runs);
        var exec = new RecordingExecutor();
        var resumed = (await h.Runs.GetAsync(run.Id, Ct))!;

        // … but this dispatch does not say so (parkReason left at its default), which is what a caller that
        // never read the envelope looks like. The token cannot be recovered here: the claim above NULLed it.
        await h.BuildOrchestrator(planner, spy).RunAsync(
            resumed, exec, Persona(), Provider(), RunProfile.Interactive, Ct, resume: true);

        Assert.Equal(0, planner.PlanCalls);
        Assert.Equal(0, spy.ReplaceStepsCalls);
        Assert.DoesNotContain(AgentRunState.Planning, spy.States);
    }

    /// <summary>
    /// The ORDINARY mid-plan resume — a budget park, the shape every resume in the app has had until now —
    /// drains its Pending remainder and never plans, with a park reason present and irrelevant. The control
    /// that says the guard did not simply become "re-plan whenever a reason is known".
    /// </summary>
    [Fact]
    public async Task Resume_StepCapPark_DrainsTheRemainder_AndNeverRePlans()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("a real goal");
        await h.Runs.ReplaceStepsAsync(run.Id, new List<AgentStep>
        {
            Persisted(0, "s1", AgentStepStatus.Done),
            Persisted(1, "s2", AgentStepStatus.Pending),
        }, Ct);
        await h.ParkAndClaimAsync(run.Id, "step-cap");

        var planner = new SpyPlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("must-not-be-used"), false));
        var spy = new SpyRunService(h.Runs);
        var exec = new RecordingExecutor();
        var resumed = (await h.Runs.GetAsync(run.Id, Ct))!;

        await h.BuildOrchestrator(planner, spy).RunAsync(
            resumed, exec, Persona(), Provider(), RunProfile.Interactive, Ct,
            resume: true, parkReason: "step-cap");

        Assert.Equal(0, planner.PlanCalls);
        Assert.Equal(0, spy.ReplaceStepsCalls);
        Assert.Equal(new[] { "s2" }, exec.Executed);
        Assert.Equal(AgentRunState.Completed, (await h.Runs.GetAsync(run.Id, Ct))!.State);
    }

    /// <summary>
    /// A LAUNCH is untouched by the new parameter: <c>resume: false</c> short-circuits the whole guard, so a
    /// (nonsensical) park reason on a launch changes nothing.
    /// <para>
    /// <b>The short-circuit is OBSERVED, not just asserted about in prose.</b> The guard's condition (b) is a
    /// <c>GetAsync</c> against the store, so a launch that entered it would make one MORE store read than a
    /// launch that could not. Two otherwise identical launches are run and their read counts compared —
    /// deliberately a DIFFERENTIAL rather than an absolute number, because the loop makes several other reads
    /// of its own and a fact that pinned their total would red on any unrelated change to them. Delete the
    /// <c>resume &amp;&amp;</c> short-circuit at the call site and the counts diverge by exactly one while
    /// every other assertion here stays green — which is the hole this replaces.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Launch_IgnoresTheParkReason_AndStillPlansExactlyOnce()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("a real goal");

        var planner = new SpyPlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1"), false));
        var spy = new SpyRunService(h.Runs);
        var exec = new RecordingExecutor();

        await h.BuildOrchestrator(planner, spy).RunAsync(
            run, exec, Persona(), Provider(), RunProfile.Interactive, Ct, parkReason: NeedsGoalReason);

        Assert.Equal(1, planner.PlanCalls);
        Assert.Equal(1, spy.ReplaceStepsCalls);
        Assert.Equal(new[] { "s1" }, exec.Executed);

        // The control: the SAME launch with no reason at all. Same goal, same one-step plan, same executor —
        // so any difference in store reads is the guard, and there must not be one.
        var control = await h.NewRunAsync("a real goal");
        var controlPlanner = new SpyPlanner();
        controlPlanner.Plans.Enqueue(new PlanResult(MakeSteps("s1"), false));
        var controlSpy = new SpyRunService(h.Runs);

        await h.BuildOrchestrator(controlPlanner, controlSpy).RunAsync(
            control, new RecordingExecutor(), Persona(), Provider(), RunProfile.Interactive, Ct);

        Assert.Equal(controlSpy.GetCalls, spy.GetCalls);
    }

    // ============================================================================================
    // The answers: persisted beside the goal, and READ by the re-plan
    // ============================================================================================

    /// <summary>
    /// <b>The re-plan is given what the last one was missing.</b> The answers persisted on the run reach the
    /// planner through <c>ctx.Clarifications</c>, in order — and the goal argument is still the user's own
    /// words, untouched (owner Q3).
    /// <para>
    /// A re-plan that ran WITHOUT them would re-ask the question the user just answered, which is the one
    /// outcome that makes the whole park/resume loop useless. That is why the seed and the guard are one method
    /// in the orchestrator rather than two.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Resume_NeedsGoalPark_SeedsThePersistedAnswersIntoTheRunContext()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync(ThinGoal);
        await h.Runs.AppendClarificationAsync(run.Id, FirstAnswer, Ct);
        await h.Runs.AppendClarificationAsync(run.Id, SecondAnswer, Ct);
        await h.ParkAndClaimAsync(run.Id, NeedsGoalReason);

        var planner = new SpyPlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1"), false));
        var spy = new SpyRunService(h.Runs);
        var resumed = (await h.Runs.GetAsync(run.Id, Ct))!;

        await h.BuildOrchestrator(planner, spy).RunAsync(
            resumed, new RecordingExecutor(), Persona(), Provider(), RunProfile.Interactive, Ct,
            resume: true, parkReason: NeedsGoalReason);

        var seen = Assert.Single(planner.SeenClarifications);
        Assert.Equal(new[] { FirstAnswer, SecondAnswer }, seen);         // oldest-first, both of them
        Assert.Equal(new[] { ThinGoal }, planner.PlannedGoals);          // the goal argument is NOT rewritten
    }

    /// <summary>
    /// <b>Owner Q3, as an assertion about the ROW.</b> A whole park → answer → resume → re-plan cycle leaves
    /// <c>AgentRuns.Goal</c> byte-identical to what the user typed. The panel and
    /// <c>ChildRunRowViewModel</c> render that column directly, so folding answers into it would rewrite, in
    /// front of the user, the sentence they use to recognise their own run.
    /// </summary>
    [Fact]
    public async Task ClarificationCycle_LeavesTheGoalColumnExactlyAsTheUserTypedIt()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync(ThinGoal);
        await h.Runs.AppendClarificationAsync(run.Id, FirstAnswer, Ct);
        await h.ParkAndClaimAsync(run.Id, NeedsGoalReason);

        var planner = new SpyPlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1"), false));
        var resumed = (await h.Runs.GetAsync(run.Id, Ct))!;

        await h.BuildOrchestrator(planner, new SpyRunService(h.Runs)).RunAsync(
            resumed, new RecordingExecutor(), Persona(), Provider(), RunProfile.Interactive, Ct,
            resume: true, parkReason: NeedsGoalReason);

        var final = await h.Runs.GetAsync(run.Id, Ct);
        Assert.Equal(ThinGoal, final!.Goal);
        Assert.DoesNotContain(FirstAnswer, final.Goal!);
    }

    /// <summary>
    /// <b>18 D4 — no cap on how many times a run may ask, and no answer is lost when it asks again.</b> The run
    /// parks, is answered, re-plans, the model declines AGAIN, it parks again, is answered again — and the
    /// SECOND re-plan sees BOTH answers. Without accumulation the second park would silently discard the first
    /// answer, which is the concrete cost the owner was shown when they chose "no cap".
    /// <para>
    /// Note what the re-decline does to the row: the decline branch parks with zero steps again, so the run is
    /// left in exactly the state the first park left it in and the cycle is genuinely repeatable rather than
    /// one-shot.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TwoParks_TwoAnswers_AndTheSecondRePlanSeesBoth()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync(ThinGoal);
        var planner = new SpyPlanner();
        var spy = new SpyRunService(h.Runs);

        // ---- park 1, answered → the re-plan declines again and re-parks ----
        await h.Runs.AppendClarificationAsync(run.Id, FirstAnswer, Ct);
        await h.ParkAndClaimAsync(run.Id, NeedsGoalReason);
        planner.Plans.Enqueue(PlanResult.Decline("still not sure what you mean"));
        await h.BuildOrchestrator(planner, spy).RunAsync(
            (await h.Runs.GetAsync(run.Id, Ct))!, new RecordingExecutor(), Persona(), Provider(),
            RunProfile.Interactive, Ct, resume: true, parkReason: NeedsGoalReason);

        var afterFirst = await h.Runs.GetAsync(run.Id, Ct);
        Assert.Equal(AgentRunState.WaitingForInput, afterFirst!.State); // parked again, not terminal (18 D4)
        Assert.Empty(afterFirst.Plan);                                  // still zero step rows

        // ---- park 2, answered → the second re-plan gets BOTH answers ----
        await h.Runs.AppendClarificationAsync(run.Id, SecondAnswer, Ct);
        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, Ct));
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1"), false));
        await h.BuildOrchestrator(planner, spy).RunAsync(
            (await h.Runs.GetAsync(run.Id, Ct))!, new RecordingExecutor(), Persona(), Provider(),
            RunProfile.Interactive, Ct, resume: true, parkReason: NeedsGoalReason);

        Assert.Equal(2, planner.PlanCalls);
        Assert.Equal(new[] { FirstAnswer }, planner.SeenClarifications[0]);
        Assert.Equal(new[] { FirstAnswer, SecondAnswer }, planner.SeenClarifications[1]);
        Assert.Equal(AgentRunState.Completed, (await h.Runs.GetAsync(run.Id, Ct))!.State);
    }

    // ============================================================================================
    // The store: accumulation, and where the text must NOT end up
    // ============================================================================================

    /// <summary>
    /// <c>AppendClarificationAsync</c> accumulates oldest-first, survives the resume claim that NULLs
    /// <c>ExtraJson</c>, and never writes the answer into the pause envelope. That last assertion is the one
    /// implementer decision 3 turns on: <see cref="RunPauseEnvelope"/>'s doc licenses a consumer to LOG every
    /// member it carries, so a user's answer must never be inside it.
    /// </summary>
    [Fact]
    public async Task AppendClarificationAsync_Accumulates_SurvivesTheResumeClaim_AndStaysOutOfThePauseEnvelope()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync(ThinGoal);

        Assert.Equal(new[] { FirstAnswer }, await h.Runs.AppendClarificationAsync(run.Id, FirstAnswer, Ct));
        Assert.Equal(new[] { FirstAnswer, SecondAnswer },
            await h.Runs.AppendClarificationAsync(run.Id, SecondAnswer, Ct));

        await h.Runs.PauseAsync(run.Id, NeedsGoalReason, Ct);
        var parked = await h.Runs.GetAsync(run.Id, Ct);
        Assert.DoesNotContain(FirstAnswer, parked!.ExtraJson ?? string.Empty);
        Assert.DoesNotContain(SecondAnswer, parked.ExtraJson ?? string.Empty);

        // The claim NULLs ExtraJson — that is exactly why the answers cannot live there.
        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, Ct));
        var claimed = await h.Runs.GetAsync(run.Id, Ct);
        Assert.Null(claimed!.ExtraJson);
        Assert.Equal(new[] { FirstAnswer, SecondAnswer }, RunClarifications.Read(claimed.ClarificationsJson));
    }

    /// <summary>
    /// <b>The two stored bounds, measured — and 18 D4 is not among them.</b> An answer past
    /// <c>RunClarifications.MaxAnswers</c> drops the OLDEST kept answer, and one past
    /// <c>MaxAnswerChars</c> is head-kept with an ellipsis. Both bound what a later plan turn ships (the
    /// reliability argument <c>AgentPlanner.MaxGroundingEntries</c> makes, applied to a listing); NEITHER is a
    /// cap on ASKING, which 18 D4 forbids — the ninth park below is answered like every other one and the run
    /// is never refused a question.
    /// <para>
    /// Written after an adversarial review pointed out that the drop arm had no coverage at all, so the bound
    /// was a claim in a doc comment rather than a measured behaviour. It is deliberately a WRITE-time bound:
    /// the column's only consumer is <c>RunContext.AppendClarifications</c>, and letting it grow without limit
    /// would keep an unbounded amount of user-typed text on the row of a run 18 D4 permits to park forever.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AppendClarificationAsync_PastTheStoredBounds_KeepsTheNewest_AndHeadKeepsALongAnswer()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync(ThinGoal);

        // One more than the keep cap, each answer identifiable by its own index.
        for (var i = 1; i <= RunClarifications.MaxAnswers + 1; i++)
            await h.Runs.AppendClarificationAsync(run.Id, "answer " + i, Ct);

        var kept = RunClarifications.Read((await h.Runs.GetAsync(run.Id, Ct))!.ClarificationsJson);
        Assert.Equal(RunClarifications.MaxAnswers, kept.Count);
        Assert.Equal("answer 2", kept[0]);                                    // the oldest was dropped
        Assert.Equal("answer " + (RunClarifications.MaxAnswers + 1), kept[^1]); // the newest is kept, last
        Assert.DoesNotContain("answer 1", kept);

        // The per-answer bound, on a fresh run so the keep cap is not also in play. Head-kept + ellipsis, so
        // a pasted log file cannot be re-sent whole on every later plan turn of the run.
        var other = await h.NewRunAsync(ThinGoal);
        var stored = Assert.Single(
            await h.Runs.AppendClarificationAsync(other.Id, new string('x', RunClarifications.MaxAnswerChars + 50), Ct));
        Assert.Equal(RunClarifications.MaxAnswerChars + 1, stored.Length); // the cap, plus the one-char ellipsis
        Assert.EndsWith("…", stored, StringComparison.Ordinal);
    }

    /// <summary>
    /// A blank answer writes nothing and is not an error: the Flow Continue card carries no text input at all
    /// (spec §4.3), so "resumed with nothing typed" is an ordinary path. The run still re-plans on such a resume
    /// — with no answers — which costs one plan turn and, under 18 D4, simply parks again with the same
    /// question. That is strictly better than the alternative it replaces (settling <c>Completed</c> having done
    /// nothing).
    /// </summary>
    [Fact]
    public async Task AppendClarificationAsync_BlankAnswer_WritesNothing_AndAnAnswerlessResumeStillRePlans()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync(ThinGoal);

        Assert.Empty(await h.Runs.AppendClarificationAsync(run.Id, "   ", Ct));
        Assert.Empty(await h.Runs.AppendClarificationAsync(run.Id, null, Ct));
        Assert.Null((await h.Runs.GetAsync(run.Id, Ct))!.ClarificationsJson);

        await h.ParkAndClaimAsync(run.Id, NeedsGoalReason);
        var planner = new SpyPlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1"), false));
        await h.BuildOrchestrator(planner, new SpyRunService(h.Runs)).RunAsync(
            (await h.Runs.GetAsync(run.Id, Ct))!, new RecordingExecutor(), Persona(), Provider(),
            RunProfile.Interactive, Ct, resume: true, parkReason: NeedsGoalReason);

        Assert.Equal(1, planner.PlanCalls);
        Assert.Empty(Assert.Single(planner.SeenClarifications));
    }

    /// <summary>
    /// THE EXISTING-USER FACT for the new column: a profile created before <c>ClarificationsJson</c> existed
    /// must open, gain it, and keep every run it already had. The fresh-install path (the <c>CREATE TABLE</c>
    /// literal) is covered by every other fact in this file; only this one covers <c>MigrateSchema</c>'s ALTER,
    /// and getting one of the two right is the classic half-fix on this table.
    /// <para>
    /// The pre-18 shape is reproduced by DROPping the column rather than by pasting an old <c>CREATE TABLE</c>,
    /// which is <c>SqliteContextTests.EnsureSchema_AddsTheCorrelationColumns_ToAPreT214Database</c>'s method and
    /// stays defined against whatever this build actually creates.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AnExistingDatabase_GainsClarificationsJson_AndKeepsItsRuns()
    {
        // Its own directory rather than the Harness's: that one deletes its temp folder on Dispose, and this
        // fact needs the FILE to outlive the first "launch" so the second one can migrate it.
        var dir = Path.Combine(Path.GetTempPath(), "PiaClarifyMigrate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "history.db");
        try
        {
            Guid runId;
            // ---- launch 1: create a run, then rewind the schema to the pre-18 shape ----
            using (var ctx = new SqliteContext(dbPath))
            using (var runs = new AgentRunService(ctx, NullLogger<AgentRunService>.Instance))
            {
                var chats = new AssistantChatService(ctx, runs);
                var chatId = Guid.NewGuid();
                var now = DateTime.UtcNow;
                await chats.SaveAsync(new SyncAssistantChat
                {
                    Id = chatId,
                    SchemaVersion = 1,
                    Title = "t",
                    CreatedAt = now,
                    UpdatedAt = now,
                    LastAccessedAt = now,
                    WindowMode = WindowMode.Assistant.ToString(),
                    Messages = [],
                }, Ct);
                runId = (await runs.CreateAsync(
                    new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: ThinGoal), Ct)).Id;

                // DROP rather than a pasted old CREATE TABLE: defined against whatever this build creates.
                using var drop = ctx.GetConnection().CreateCommand();
                drop.CommandText = "ALTER TABLE AgentRuns DROP COLUMN ClarificationsJson";
                drop.ExecuteNonQuery();
            }

            // ---- launch 2: EnsureSchema + MigrateSchema run against the existing file ----
            using var reopened = new SqliteContext(dbPath);
            var columns = new List<string>();
            using (var pragma = reopened.GetConnection().CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info(AgentRuns)";
                using var r = pragma.ExecuteReader();
                while (r.Read()) columns.Add(r.GetString(1));
            }
            // CREATE TABLE IF NOT EXISTS is a no-op on an existing table, so the only thing that can have added
            // the column is MigrateSchema's ALTER pass.
            Assert.Contains("ClarificationsJson", columns);

            // The pre-18 run survived, still readable through the service, and reads NULL in the new column —
            // "this run was never asked anything", which is true of every run that predates the batch.
            using var migratedRuns = new AgentRunService(reopened, NullLogger<AgentRunService>.Instance);
            var migrated = await migratedRuns.GetAsync(runId, Ct);
            Assert.NotNull(migrated);
            Assert.Equal(ThinGoal, migrated!.Goal);
            Assert.Null(migrated.ClarificationsJson);
            Assert.Empty(RunClarifications.Read(migrated.ClarificationsJson));

            // …and it is writable straight away, i.e. the ALTER produced a usable column and not just a name.
            Assert.Equal(new[] { FirstAnswer }, await migratedRuns.AppendClarificationAsync(runId, FirstAnswer, Ct));

            // Idempotent: a THIRD launch must not re-issue the ALTER (SQLite errors on a duplicate column name
            // and MigrateSchema has no try/catch — an unguarded ALTER takes startup down on every later launch).
            migratedRuns.Dispose();
            reopened.Dispose();
            using var third = new SqliteContext(dbPath);
            Assert.NotNull(third.GetConnection());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    // ============================================================================================
    // The prompt: what the model actually receives
    // ============================================================================================

    /// <summary>
    /// <b>The last link in the chain.</b> The seeded answers are not merely on the context — the real
    /// <see cref="AgentPlanner"/> puts them in the plan turn's <c>ChatRole.User</c> message, together with the
    /// goal.
    /// <para>
    /// <b>USER role, asserted explicitly.</b> <c>TokenizingAiClientService.TokenizeMessages</c> rewrites only
    /// <c>ChatRole.User</c> text to PII placeholders, so an answer folded into the System prompt would ship the
    /// user's raw keystrokes past the tokenizer even with tokenization ON — the same rule the nudge and the
    /// grounding digest already follow, and the reason this is a fact rather than a detail.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PlanTurn_CarriesTheAnswersOnTheUserMessage_NeverOnTheSystemPrompt()
    {
        var captured = await CapturePlanMessagesAsync(ctx => ctx.SetClarifications([FirstAnswer, SecondAnswer]));

        var user = Assert.Single(captured, m => m.Role == ChatRole.User);
        Assert.Contains(ThinGoal, user.Text);
        Assert.Contains(FirstAnswer, user.Text);
        Assert.Contains(SecondAnswer, user.Text);

        var system = Assert.Single(captured, m => m.Role == ChatRole.System);
        Assert.DoesNotContain(FirstAnswer, system.Text);
        Assert.DoesNotContain(SecondAnswer, system.Text);
    }

    /// <summary>
    /// The control: with nothing recorded, the plan turn's user message is the goal and only the goal — so a
    /// launch (and every resume that is not a clarification re-plan) sends the prompt this batch inherited,
    /// unchanged. Without this fact the one above could be passing while every plan turn in the app carried an
    /// empty fence.
    /// </summary>
    [Fact]
    public async Task PlanTurn_WithNoAnswers_SendsTheGoalUnchanged()
    {
        var captured = await CapturePlanMessagesAsync(_ => { });

        var user = Assert.Single(captured, m => m.Role == ChatRole.User);
        Assert.Equal(ThinGoal, user.Text);
    }

    /// <summary>
    /// Drives ONE real plan turn through a stubbed provider and returns the messages it sent. The AI client is
    /// the only double: the planner, its prompt building and <see cref="RunContext"/> are real, which is the
    /// point — the claim is about what a provider receives.
    /// </summary>
    private static async Task<IList<ChatMessage>> CapturePlanMessagesAsync(Action<RunContext> seed)
    {
        var ai = Substitute.For<IAiClientService>();
        IList<ChatMessage>? sent = null;
        ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                sent ??= ci.ArgAt<IList<ChatMessage>>(0);
                return PlanStream();
            });

        var settings = Substitute.For<ISettingsService>();
        // The default AppSettings leaves the reason-then-emit turn OFF, so this is the single constrained turn.
        settings.GetSettingsAsync().Returns(_ => Task.FromResult(new AppSettings()));
        var handler = Substitute.For<IAiProviderHandler>();
        handler.ProviderType.Returns(AiProviderType.OpenAI);
        handler.DropsReasoningEffortWithTools.Returns(false);
        var planner = new AgentPlanner(ai, new AiProviderHandlerResolver([handler]), settings,
            NullLogger<AgentPlanner>.Instance);

        var ctx = new RunContext(ThinGoal, RunProfile.Interactive);
        seed(ctx);
        await planner.PlanAsync(ThinGoal, ctx, Persona(), Provider(), Ct);

        Assert.NotNull(sent); // a fixture that never reached the provider would make either fact vacuous
        return sent!;
    }

    /// <summary>A plan turn that calls nothing and just finishes — enough to capture the messages; what comes
    /// back is irrelevant to these two facts (the planner degrades, which is fine).</summary>
    private static async IAsyncEnumerable<ChatStreamItem> PlanStream()
    {
        await Task.Yield();
        yield return new Finished(null, "test-model");
    }
}
