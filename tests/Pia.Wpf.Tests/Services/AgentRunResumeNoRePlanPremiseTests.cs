using System.IO;
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
/// <b>Batch 18 G4's premise, MEASURED against today's loop before G4 changes it.</b> The invariant
/// <c>08 D1</c> (<c>AgentRunOrchestrator.cs:186-191</c>, "a resume must NOT re-plan", implemented as a bare
/// <c>if (!resume)</c> around the whole planning block) has until now been READ rather than measured — no fact
/// in this suite asserts the ABSENCE of the planner call on a resume; the existing resume facts
/// (<c>AgentRunOrchestratorTests.Run_Resume_*</c>) assert end states and drained steps, which a re-planning
/// resume could also produce. <c>18 D2</c> contradicts that invariant head-on (a <c>needs-goal</c> park resumes
/// INTO re-planning), so this file is the "before" half: test-only, landing ahead of the code that relies on it,
/// which is the precedent Batch 08's own G1 set for exactly this situation.
/// <para>
/// <b>18 G4 HAS NOW LANDED, and every fact below is still green — deliberately, not by luck.</b> The guard it
/// added re-plans only when the resume is TOLD the park reason was <c>needs-goal</c> AND the run has zero step
/// rows, and the reason travels as a new <c>RunAsync(parkReason:)</c> argument because both resume claims NULL
/// the pause envelope it comes from. Not one fixture in this file passes that argument, so all of them exercise
/// the fail-safe default — "the caller cannot say why this run parked" ⇒ <c>08 D1</c> verbatim. That is the
/// arm G4 must not have broken, so this file did not become obsolete when the invariant changed; it became the
/// regression fact for the half of the invariant that survived. The half that did NOT survive is measured next
/// door, in <c>AgentRunClarificationResumeTests</c>, which is where both directions of spec §8.4 live.
/// </para>
/// <para>
/// <b>What each fact is for, and what G4 did to it.</b>
/// </para>
/// <list type="bullet">
/// <item><see cref="Resume_CallsNeitherPlanAsyncNorReplaceSteps_AndNeverEntersPlanning"/> — the premise itself.
/// G4 made the guard conditional (spec §4.1), and this fact still holds for every flavour of resume it does NOT
/// re-plan (a mid-plan <c>needs-input</c> park, an unstated reason, and every park that existed before 18). If it
/// ever goes red for THIS fixture — a run with persisted step rows — the guard became too wide and the hazard
/// <c>08 D1</c>'s comment names (ReplaceStepsAsync writing the plan verbatim over the Done rows) is back.</item>
/// <item><see cref="Launch_CallsPlanAsyncOnce_AndWritesThePlan"/> and
/// <see cref="LaunchThenResume_OnOneWiring_AddsNoSecondPlanCall"/> — the controls. Without them the fact above
/// could pass because nothing in the fixture ever reaches a planner at all.</item>
/// <item><see cref="ZeroStepResume_PlansNothing_DrainsNothing_AndSettlesCompleted"/> — the CHARACTERISATION
/// spec §4.1 asks for, and the one G4 inverted. A resume of a run with zero persisted step rows does not merely
/// "drop into the drain loop with an empty plan": it drains nothing, passes the critic on an empty
/// completed-step list and settles <c>Completed</c>, un-truncated — i.e. it reports the goal as DONE having done
/// nothing. <b>G4 inverted that only for a resume that SAYS it is answering a <c>needs-goal</c> park</b>
/// (<c>AgentRunClarificationResumeTests.Resume_NeedsGoalPark_WithNoStepRows_RePlans</c> is the same fixture with
/// the reason supplied, and it plans). This fact keeps measuring the UNSTATED-reason resume, which still behaves
/// exactly as described — deliberately: see the amended note on the test itself.</item>
/// <item><see cref="ZeroStepResume_WhoseVerifyFails_ReachesReplanAsync"/> — the boundary of the invariant, so
/// G4 does not over-trust its own wording: a resume already CAN reach <see cref="IAgentPlanner"/> today, via the
/// verify-fail branch's <c>ReplanAsync</c>. "A resume must NOT re-plan" is true of the PLANNING BLOCK
/// (<c>PlanAsync</c> + the plan-time <c>ReplaceStepsAsync</c>), not of the planner as such.</item>
/// </list>
/// <para>
/// Absence is asserted on SPIES, never on end state: <see cref="SpyPlanner"/> counts both planner entry points
/// and <see cref="SpyRunService"/> counts <c>ReplaceStepsAsync</c> and records every <c>SetStateAsync</c>. The
/// run store underneath is the real SQLite <see cref="AgentRunService"/>, so the persisted-Pending re-query
/// (<c>R2</c>) that a resume depends on is exercised rather than faked.
/// </para>
/// </summary>
public sealed class AgentRunResumeNoRePlanPremiseTests
{
    private static Persona Persona() => new() { Name = "Pia", SystemPrompt = "sys" };

    private static AiProvider Provider() => new() { Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };

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

    /// <summary>
    /// A step row as a PARKED run's plan already holds it — with its status, which is the whole point of seeding
    /// a park directly instead of driving one: a <c>Done</c> row and a <c>Pending</c> row, and no dispatch has
    /// happened yet, so a zero call count on the resume means zero rather than "one launch's calls, minus one".
    /// </summary>
    private static AgentStep Persisted(int ordinal, string intent, AgentStepStatus status) => new()
    {
        Id = Guid.NewGuid(),
        Ordinal = ordinal,
        Title = intent,
        Intent = intent,
        Status = status,
    };

    /// <summary>
    /// Counts BOTH planner entry points, because the premise is about the absence of a call and the two
    /// absences are not the same claim: <c>PlanAsync</c> is reached only from the planning block the
    /// <c>if (!resume)</c> guards, while <c>ReplanAsync</c> is reachable from the drain loop and from the
    /// verify-fail branch on a resume too (<see cref="ZeroStepResume_WhoseVerifyFails_ReachesReplanAsync"/>).
    /// </summary>
    private sealed class SpyPlanner : IAgentPlanner
    {
        public Queue<PlanResult> Plans { get; } = new();

        public Queue<PlanResult> Replans { get; } = new();

        public int PlanCalls { get; private set; }

        public int ReplanCalls { get; private set; }

        /// <summary>The goal each <c>PlanAsync</c> was handed — recorded so a control fact can show the planner
        /// really was wired to THIS run rather than merely invoked.</summary>
        public List<string> PlannedGoals { get; } = new();

        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
        {
            PlanCalls++;
            PlannedGoals.Add(goal);
            return Task.FromResult(Plans.Count > 0 ? Plans.Dequeue() : PlanResult.Fallback);
        }

        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
        {
            ReplanCalls++;
            return Task.FromResult(Replans.Count > 0 ? Replans.Dequeue() : PlanResult.Fallback);
        }
    }

    /// <summary>
    /// Pass-through decorator over the REAL run store (the delegation list is lifted from
    /// <c>AgentRunOrchestratorTests.FaultyRunService</c>, which is private there) with the two writes this
    /// file measures the absence of:
    /// <list type="bullet">
    /// <item><see cref="ReplaceStepsCalls"/> — the plan write <c>08 D1</c>'s comment names as the hazard ("writes
    /// the plan verbatim and does not preserve Done steps").</item>
    /// <item><see cref="States"/> — every <c>SetStateAsync</c>, so "the run never entered <c>Planning</c>" is
    /// observable. Deliberately NOT every state the row ever held: <c>PauseAsync</c>, <c>CompleteAsync</c>,
    /// <c>FailAsync</c> and the resume CAS write their states with their own statements, and the claim here is
    /// about the planning block's <c>SafeSetState(Planning)</c> specifically.</item>
    /// </list>
    /// Everything else delegates, so the run really executes against real persisted rows.
    /// </summary>
    private sealed class SpyRunService : IAgentRunService
    {
        private readonly IAgentRunService _inner;

        public SpyRunService(IAgentRunService inner) => _inner = inner;

        public int ReplaceStepsCalls { get; private set; }

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
        public Task<AgentRun?> GetAsync(Guid runId, CancellationToken ct = default) => _inner.GetAsync(runId, ct);
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

    /// <summary>
    /// The step executor, recording what it was asked to run. <see cref="FallbackCalled"/> is here because
    /// spec §4.2's hazard is that a decline (and, on this file's evidence, an empty resume) could be routed into
    /// the <c>R10</c> degrade — so every fact can say the single-turn fallback was NOT taken.
    /// </summary>
    private sealed class RecordingExecutor : IAgentTurnExecutor
    {
        private readonly Func<AgentStep, StepTurnResult> _result;

        public RecordingExecutor(Func<AgentStep, StepTurnResult> result) => _result = result;

        public List<string> Executed { get; } = new();

        public bool BeginCalled { get; private set; }

        public bool EndCalled { get; private set; }

        public bool EndCancelled { get; private set; }

        public bool EndFailed { get; private set; }

        public bool FallbackCalled { get; private set; }

        public bool PausedCalled { get; private set; }

        public Task BeginRunAsync(AgentRun run, RunContext ctx, CancellationToken ct)
        {
            BeginCalled = true;
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
            return Task.FromResult(Ok("fallback"));
        }

        public Task EndRunAsync(AgentRun run, RunContext ctx, bool cancelled, bool failed, CancellationToken ct)
        {
            EndCalled = true;
            EndCancelled = cancelled;
            EndFailed = failed;
            return Task.CompletedTask;
        }

        public Task OnPausedAsync(AgentRun run, RunContext ctx, CancellationToken ct)
        {
            PausedCalled = true; // the non-terminal park hook (guardrail 5) — never EndRunAsync
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Same shape as every other orchestrator fixture in this folder (each file carries its own private copy —
    /// <c>AgentRunOrchestratorTests</c>, <c>…UserPauseTests</c>, <c>…FanOutTests</c>, <c>…CascadePauseTests</c>),
    /// with one addition: <see cref="BuildOrchestrator"/> takes the <see cref="SpyRunService"/> so the loop's
    /// plan writes are countable while the tests still read and seed through the real store.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly string _dir;

        public Harness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "PiaResumePremise_" + Guid.NewGuid().ToString("N"));
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

        public AgentRunOrchestrator BuildOrchestrator(
            IAgentPlanner planner, IAgentRunService runService, IAgentVerifier? verifier = null) =>
            new(runService, planner, verifier ?? new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance,
                workspaces: null, childLauncher: null, chats: null, steering: null);

        public void Dispose()
        {
            Runs.Dispose();
            Ctx.Dispose();
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }
    }

    // ------------------------------------------------------- the premise itself

    /// <summary>
    /// <b>THE PREMISE (<c>08 D1</c>).</b> A <c>resume: true</c> dispatch calls <c>PlanAsync</c> ZERO times,
    /// writes the plan ZERO times, and never puts the row through <c>Planning</c> — measured on spies, not
    /// inferred from the end state, because a re-planning resume that happened to produce the same steps would
    /// pass every existing resume fact in this suite.
    /// <para>
    /// The park is SEEDED (rows written straight through the store, then <c>PauseAsync</c>) rather than driven by
    /// a first dispatch, so the resume is the only dispatch this fixture has ever made and "zero" means zero.
    /// <see cref="LaunchThenResume_OnOneWiring_AddsNoSecondPlanCall"/> is the same claim with a REAL park in
    /// front of it, which is what makes the shortcut here safe to read.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Resume_CallsNeitherPlanAsyncNorReplaceSteps_AndNeverEntersPlanning()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");

        // A parked run whose plan is half done — the shape the invariant's stated hazard is about: a
        // ReplaceStepsAsync here would delete the Done row (it writes the plan verbatim, preserving nothing).
        await h.Runs.ReplaceStepsAsync(run.Id, new List<AgentStep>
        {
            Persisted(0, "s1", AgentStepStatus.Done),
            Persisted(1, "s2", AgentStepStatus.Pending),
        }, ct);
        await h.Runs.PauseAsync(run.Id, "step-cap", ct);
        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, ct)); // the CAS claim the launcher makes first

        var planner = new SpyPlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("must-not-be-used"), false)); // armed, so silence is a CHOICE
        var spy = new SpyRunService(h.Runs);
        var exec = new RecordingExecutor(_ => Ok());
        // Fetched from the store, exactly as HeadlessRunLauncher.ResumeAsync hands it over (R3 reads
        // FirstMessageId off the passed run).
        var resumed = (await h.Runs.GetAsync(run.Id, ct))!;

        await h.BuildOrchestrator(planner, spy)
            .RunAsync(resumed, exec, Persona(), Provider(), RunProfile.Interactive, ct, resume: true);

        // THE CLAIM, as three absences.
        Assert.Equal(0, planner.PlanCalls);
        Assert.Equal(0, spy.ReplaceStepsCalls);
        Assert.DoesNotContain(AgentRunState.Planning, spy.States);
        // …and a fourth: no replan either, because nothing failed and the critic accepted.
        Assert.Equal(0, planner.ReplanCalls);
        Assert.False(exec.FallbackCalled); // nor the R10 degrade (§4.2)

        // NON-VACUITY. The dispatch really ran: it drained the persisted Pending remainder and only that, the
        // pre-pause Done row survived un-re-executed, and the run settled. A fixture where RunAsync did nothing
        // at all would satisfy the four absences above.
        Assert.Equal(new[] { "s2" }, exec.Executed);
        Assert.True(exec.BeginCalled);
        Assert.True(exec.EndCalled);
        var final = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.Equal(2, final.Plan.Count); // the plan was never rewritten
        Assert.All(final.Plan, s => Assert.Equal(AgentStepStatus.Done, s.Status));
    }

    // ------------------------------------------------------------- the controls

    /// <summary>
    /// The control for the fact above: on the SAME fixture, a <c>resume: false</c> dispatch does call
    /// <c>PlanAsync</c> (once, with this run's goal) and does write the plan. Without this, the premise fact
    /// could be passing because this harness never wires a planner the loop can reach.
    /// </summary>
    [Fact]
    public async Task Launch_CallsPlanAsyncOnce_AndWritesThePlan()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("the goal");

        var planner = new SpyPlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1"), false));
        var spy = new SpyRunService(h.Runs);
        var exec = new RecordingExecutor(_ => Ok());

        await h.BuildOrchestrator(planner, spy)
            .RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, ct);

        Assert.Equal(1, planner.PlanCalls);
        Assert.Equal(new[] { "the goal" }, planner.PlannedGoals); // the planner was reached for THIS run
        Assert.Equal(1, spy.ReplaceStepsCalls);
        Assert.Contains(AgentRunState.Planning, spy.States);
        Assert.Equal(new[] { "s1" }, exec.Executed);
        Assert.Equal(AgentRunState.Completed, (await h.Runs.GetAsync(run.Id, ct))!.State);
    }

    /// <summary>
    /// The premise again, with the launch and the resume on ONE planner instance and ONE spy store, and with a
    /// REAL park in between (the step-cap park, which is how a run reaches <c>WaitingForInput</c> today) — so
    /// the difference between the two dispatches is only the <c>resume</c> flag, and the seeded park used by
    /// <see cref="Resume_CallsNeitherPlanAsyncNorReplaceSteps_AndNeverEntersPlanning"/> is shown to be faithful.
    /// The counts do not move across the resume.
    /// </summary>
    [Fact]
    public async Task LaunchThenResume_OnOneWiring_AddsNoSecondPlanCall()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");

        var planner = new SpyPlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("s1", "s2", "s3"), false));
        var spy = new SpyRunService(h.Runs);

        // Launch with a 2-step budget: s1 and s2 run, then the run parks at the cap with s3 still Pending.
        var capped = new RunProfile(MaxSteps: 2, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20));
        var first = new RecordingExecutor(_ => Ok());
        await h.BuildOrchestrator(planner, spy).RunAsync(run, first, Persona(), Provider(), capped, ct);

        Assert.Equal(new[] { "s1", "s2" }, first.Executed);
        Assert.Equal(AgentRunState.WaitingForInput, (await h.Runs.GetAsync(run.Id, ct))!.State);
        Assert.Equal(1, planner.PlanCalls);      // the launch's one plan turn …
        Assert.Equal(1, spy.ReplaceStepsCalls);  // … and its one plan write

        // Resume the way the launcher does: claim the row, re-fetch it, re-enter with resume: true.
        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, ct));
        var resumed = (await h.Runs.GetAsync(run.Id, ct))!;
        var second = new RecordingExecutor(_ => Ok());
        await h.BuildOrchestrator(planner, spy)
            .RunAsync(resumed, second, Persona(), Provider(), RunProfile.Interactive, ct, resume: true);

        // THE CLAIM: neither count moved, on the very planner instance that answered the launch.
        Assert.Equal(1, planner.PlanCalls);
        Assert.Equal(1, spy.ReplaceStepsCalls);
        Assert.Equal(0, planner.ReplanCalls);
        // Non-vacuity: the resume drained the remainder, so it did reach the drain loop.
        Assert.Equal(new[] { "s3" }, second.Executed);
        Assert.Equal(AgentRunState.Completed, (await h.Runs.GetAsync(run.Id, ct))!.State);
    }

    // ------------------------------------- §4.1's zero-step resume, characterised

    /// <summary>
    /// <b>Spec §4.1's open question, MEASURED.</b> A <c>needs-goal</c> park has zero persisted step rows, and
    /// §4.1 says such a resume "would drop into the drain loop with an empty plan". What today's loop actually
    /// does is worse than that phrasing suggests, and this is the fact G4 has to invert:
    /// <list type="number">
    /// <item><c>NextPendingStepAsync</c> returns null on the first probe, so NOTHING is executed.</item>
    /// <item>The terminal critic runs once, on an EMPTY completed-step list, and accepts.</item>
    /// <item>The run settles <c>Completed</c>, with <c>CompletedAt</c> stamped and NOT marked truncated —
    /// i.e. indistinguishable, to the panel and to a scheduled job's bookkeeping, from a run that did the
    /// work.</item>
    /// </list>
    /// So resuming a <c>needs-goal</c> park through today's <c>RunAsync(resume: true)</c> would answer the
    /// user's clarification by declaring the goal DONE. The park reason is written as <c>"needs-goal"</c> here
    /// purely to name the shape under test — the row's token is destroyed by the <c>TryBeginResumeAsync</c>
    /// claim above (it NULLs <c>ExtraJson</c>), so this loop cannot read it at all, which is precisely the hole
    /// G4's guard had to fill (<c>18 D2</c>).
    /// <para>
    /// <b>AMENDED, 18 G4 — this fact was kept rather than deleted, and its subject narrowed.</b> G4 landed the
    /// guard, and it re-plans only when the caller HANDS DOWN the reason (<c>RunAsync(parkReason:)</c>), because
    /// the claim above means it can be read nowhere else. This dispatch supplies none, so it still measures what
    /// it always did — and that is now a deliberate fact rather than a defect report: an unstated reason keeps
    /// <c>08 D1</c>, the conservative side. The INVERTED half is
    /// <c>AgentRunClarificationResumeTests.Resume_NeedsGoalPark_WithNoStepRows_RePlans</c>: same empty plan, same
    /// armed planner, one added argument, and it plans, writes and executes. Read the two together — the
    /// difference between them IS 18 D2.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ZeroStepResume_PlansNothing_DrainsNothing_AndSettlesCompleted()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("ggg");

        // No ReplaceStepsAsync at all: the run parks with an EMPTY plan, which is what a plan-time refusal
        // leaves behind.
        await h.Runs.PauseAsync(run.Id, "needs-goal", ct);
        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, ct));
        Assert.Empty((await h.Runs.GetAsync(run.Id, ct))!.Plan); // the premise of this fact, asserted

        var planner = new SpyPlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps("would-be-a-plan"), false)); // armed and still not called
        var spy = new SpyRunService(h.Runs);
        var verifier = new FakeVerifier();
        var exec = new RecordingExecutor(_ => Ok());
        var resumed = (await h.Runs.GetAsync(run.Id, ct))!;

        await h.BuildOrchestrator(planner, spy, verifier)
            .RunAsync(resumed, exec, Persona(), Provider(), RunProfile.Interactive, ct, resume: true);

        // (1) nothing planned, nothing written, nothing run — the guard held, and that is the problem.
        Assert.Equal(0, planner.PlanCalls);
        Assert.Equal(0, planner.ReplanCalls);
        Assert.Equal(0, spy.ReplaceStepsCalls);
        Assert.DoesNotContain(AgentRunState.Planning, spy.States);
        Assert.Empty(exec.Executed);
        Assert.False(exec.FallbackCalled); // it does not fall into the R10 degrade either (§4.2)

        // (2) the critic ran once, on nothing.
        Assert.Equal(1, verifier.VerifyCalls);
        Assert.Empty(Assert.Single(verifier.SeenCompletedSteps));

        // (3) and the run reports SUCCESS. Not parked again, not failed, not truncated.
        Assert.True(exec.EndCalled);
        Assert.False(exec.EndCancelled);
        Assert.False(exec.EndFailed);
        Assert.False(exec.PausedCalled);
        var final = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.NotNull(final.CompletedAt);
        Assert.Empty(final.Plan);
        Assert.DoesNotContain("truncated", final.ExtraJson ?? string.Empty);
    }

    /// <summary>
    /// The BOUNDARY of <c>08 D1</c>, so G4 does not over-trust the invariant's wording. A resume can already
    /// reach <see cref="IAgentPlanner"/> today: on a failed verdict the outer loop spends the shared replan
    /// budget, and on a zero-step resume that is the FIRST thing that can happen. <c>ReplanAsync</c> is called,
    /// its steps are written through the very <c>ReplaceStepsAsync</c> the invariant warns about, and they
    /// execute — on a <c>resume: true</c> dispatch.
    /// <para>
    /// This is not a defect report: with steps present, <c>KeepDoneAsync</c> is what protects the Done rows on
    /// that path (it re-writes them ahead of the revised ones), which is why the invariant's comment is written
    /// about the PLANNING BLOCK. It is here because "a resume must NOT re-plan" read literally is already false,
    /// and §4.1 asks G4 to amend that comment rather than reason from it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ZeroStepResume_WhoseVerifyFails_ReachesReplanAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();
        var run = await h.NewRunAsync("ggg");
        await h.Runs.PauseAsync(run.Id, "needs-goal", ct);
        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, ct));

        var planner = new SpyPlanner();
        planner.Replans.Enqueue(new PlanResult(MakeSteps("r1"), false));
        var spy = new SpyRunService(h.Runs);
        var verifier = new FakeVerifier();
        verifier.Verdicts.Enqueue(new VerdictResult(false, "nothing was done", new[] { "x" }, null)); // fail → replan
        // Second verdict left to the default Accept, so the re-drain settles instead of exhausting the budget.
        var exec = new RecordingExecutor(_ => Ok());
        var resumed = (await h.Runs.GetAsync(run.Id, ct))!;

        await h.BuildOrchestrator(planner, spy, verifier)
            .RunAsync(resumed, exec, Persona(), Provider(), RunProfile.Interactive, ct, resume: true);

        // THE CLAIM: the planner WAS reached on a resume — through ReplanAsync, never through PlanAsync.
        Assert.Equal(0, planner.PlanCalls);
        Assert.Equal(1, planner.ReplanCalls);
        Assert.Equal(1, spy.ReplaceStepsCalls); // and the revised plan really was written
        Assert.Equal(new[] { "r1" }, exec.Executed);

        var final = await h.Runs.GetAsync(run.Id, ct);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.Equal(AgentStepStatus.Done, Assert.Single(final.Plan).Status);
    }
}
