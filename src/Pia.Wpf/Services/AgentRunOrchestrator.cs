using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Shared.Models;

namespace Pia.Services;

/// <summary>
/// The plan → act → failure-only-replan → complete loop. UI-agnostic — never captures a
/// <see cref="SynchronizationContext"/> and uses <c>ConfigureAwait(false)</c> throughout; each
/// executor owns its own threading. Owns the run's linked <see cref="CancellationTokenSource"/>,
/// the ledger accrual, and a <see cref="RunContext"/>. Run state/ledger writes are
/// failure-isolated (the Safe* wrappers); planner-cannot-plan and executor crashes are
/// on the critical path and fail the run.
/// </summary>
public sealed class AgentRunOrchestrator
{
    private readonly IAgentRunService _runService;
    private readonly IAgentPlanner _planner;
    private readonly IAgentVerifier _verifier;
    private readonly ILogger<AgentRunOrchestrator> _logger;
    private readonly IRunWorkspaceService? _workspaces;
    private readonly IHeadlessRunLauncher? _childLauncher;
    private readonly IAssistantChatService? _chats;
    private readonly IRunSteeringStore? _steering;
    private readonly ILocalizationService? _localization;

    /// <summary>
    /// Cap on a delegated run's answer text as it is folded into the parent's context. Same number as
    /// <c>AgentPlanner</c>'s own analysis cap, for the same reason: this text lands in the replan and verify
    /// prompts, and one verbose child must not crowd out its siblings.
    /// </summary>
    private const int MaxChildAnswerChars = 4000;

    /// <summary>
    /// The pause <c>reason</c> written when a fan-out's CHILD parked at its own (halved) budget, so the parent
    /// re-parks rather than failing. An app-owned token from the same fixed vocabulary as
    /// <c>"step-cap"</c> / <c>"wall-clock"</c> / <c>"children-interrupted"</c> — never user content, and read by
    /// the panel and the Flow surface, which is why it is a named constant rather than a literal: neither of them
    /// may announce this park as a budget stop, because none of the PARENT's budgets was reached.
    /// </summary>
    internal const string ChildrenParkedReason = "children-parked";

    /// <summary>
    /// The pause <c>reason</c> written when an unattended step hit a promptable capability the run
    /// was not granted, and stopped to ask a human instead of hard-denying it.
    /// A named constant for the reason the vocabulary's other tokens are: adding one OBLIGES an arm in
    /// <c>RunProgressViewModel.DescribePause</c> and in <c>AgentRunNotificationSurface.PausedBodyKey</c>, and a
    /// literal cannot carry that obligation. Both fall back to the BUDGET wording, so a missing arm does not
    /// fail — it tells the user their run stopped at a budget it never reached, and sends them to Settings
    /// instead of to the Continue button (the F19 defect, restated).
    /// It is the ONE pause token whose envelope carries a second member: <c>tool</c>, the name the human is
    /// being asked to approve. Read back by <c>RunPauseEnvelope.ReadApprovalTool</c>.
    /// </summary>
    internal const string ToolApprovalReason = "tool-approval";

    /// <summary>Pause reason when the plan turn declined to ground the goal; parks with zero step rows, and a
    /// resume with this reason re-plans instead of draining (see <see cref="TryEnterClarificationRePlanAsync"/>).</summary>
    internal const string NeedsGoalReason = "needs-goal";

    /// <summary>Pause reason when a step mid-plan asks the user a question; unlike <see cref="NeedsGoalReason"/>,
    /// resuming this one does not re-plan — it drains the remaining steps.</summary>
    internal const string NeedsInputReason = "needs-input";

    /// <summary>The TRUNCATION reason written when the verify pass exhausted its replans — a different envelope
    /// field from the pause reasons above, read back by <c>RunProgressViewModel.DescribeTruncation</c>.</summary>
    internal const string UnverifiedTruncationReason = "unverified";

    /// <summary>The failure reason a run gets when a re-dispatched fan-out replaced it. App-owned and named
    /// rather than repeated so the run panel can localize it instead of showing it verbatim.</summary>
    internal const string SupersededFailureReason = "superseded by a re-dispatched fan-out";

    /// <summary>Pause reason when a run's FIRST plan is big enough that a human approves or rejects it before any
    /// step runs; a later replan never re-triggers it.</summary>
    internal const string PlanApprovalReason = "plan-approval";

    /// <summary>Fallback for the proposed-plan chat intro when no localization service was injected (the
    /// positional test constructions).</summary>
    private const string DefaultPlanProposedIntro =
        "Proposed plan — review the steps below, then Approve or Reject in the run panel:";

    /// <summary>What a settled child's step reports when its answer could not be read. Says the work ran
    /// elsewhere rather than implying the step produced nothing (the failure mode
    /// <c>CompletedStepSummary.FromEarlierSegment</c> exists for).</summary>
    private const string DelegatedAnswerUnavailable = "(this step ran as a delegated run; its result text is not available here)";

    private static readonly JsonSerializerOptions LedgerJsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <param name="workspaces">TRAILING and DEFAULTED, like every dependency this batch adds:
    /// this type is hand-constructed positionally in a dozen test sites, and a required parameter would break
    /// all of them at once. Null ⇒ no promotion, which is the pre-Batch-06 loop exactly.</param>
    /// <param name="childLauncher"> G10, trailing and defaulted for the same reason. Null ⇒ <b>no
    /// delegation, ever</b>: a plan's parallel groups are recorded and ignored, and every step runs in-process
    /// exactly as before. No DI cycle — the launcher is a singleton that resolves this type lazily from a
    /// per-run scope, so nothing is constructed twice.</param>
    /// <param name="chats"> G10, trailing and defaulted. Only used to read a settled child's answer back
    /// into the parent's context; null ⇒ the fan-out still works and the parent's replan/verify prompts see
    /// <see cref="DelegatedAnswerUnavailable"/> instead of the child's text.</param>
    /// <param name="steering">TRAILING and DEFAULTED, like every dependency this loop has gained:
    /// null ⇒ no pause request can ever be consumed ⇒ this loop is byte-for-byte the pre-Batch-08 one, which is
    /// what keeps a dozen positional test constructions unchanged. It is the DISCRIMINATOR that tells a user
    /// pause from a stop: without it every cancel is a stop, exactly as before.</param>
    /// <param name="localization">TRAILING and DEFAULTED, like every dependency this loop has gained: null ⇒
    /// <see cref="PostPlanRejectedNoticeAsync"/> posts nothing.</param>
    public AgentRunOrchestrator(
        IAgentRunService runService,
        IAgentPlanner planner,
        IAgentVerifier verifier,
        ILogger<AgentRunOrchestrator> logger,
        IRunWorkspaceService? workspaces = null,
        IHeadlessRunLauncher? childLauncher = null,
        IAssistantChatService? chats = null,
        IRunSteeringStore? steering = null,
        ILocalizationService? localization = null)
    {
        _runService = runService;
        _planner = planner;
        _verifier = verifier;
        _logger = logger;
        _workspaces = workspaces;
        _childLauncher = childLauncher;
        _chats = chats;
        _steering = steering;
        _localization = localization;
    }

    /// <param name="nudge">TRAILING and DEFAULTED, like every dependency this loop has gained:
    /// null ⇒ <see cref="RunContext.Nudge"/> stays null for this dispatch, which is what keeps a launch (never
    /// nudged) and a resume that supplies none identical to the pre-Batch-08 loop. Scoped to THIS dispatch only
    /// a fresh <see cref="RunContext"/> is built below on every call, so a nudge never survives a second
    /// resume that does not repeat it.</param>
    /// <param name="parkReason">Why the run parked before this resume; must be read by the caller pre-claim,
    /// since the resume claim clears the pause envelope's <c>ExtraJson</c>. Null keeps the never-re-plan-on-resume
    /// behavior.</param>
    public async Task RunAsync(
        AgentRun run,
        IAgentTurnExecutor executor,
        Persona persona,
        AiProvider provider,
        RunProfile profile,
        CancellationToken externalToken,
        bool resume = false,
        string? nudge = null,
        string? parkReason = null)
    {
        // EVERY line this dispatch writes — from this loop, from the planner and verifier it awaits, and
        // from the tool handlers inside a step turn — carries the run id. That is what makes a log readable at
        // all now that T1-1/T1-2 let several unattended runs execute at once and interleave their lines in one
        // file. IDs ONLY in a scope: whatever it stringifies to reaches a RELEASE log verbatim
        // (ScopeRenderingLoggerProvider states the rule; the goal, paths and tool arguments stay on the
        // compile-time-erased SensitiveDebug family).
        using var runScope = _logger.BeginScope("run {RunId}", run.Id);

        // Link the run CTS from the caller's token. Interactive passes session.Cts.Token, so
        // ChatSession.Cancel (which cancels session.Cts) propagates to the run + in-flight step.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var ctx = new RunContext(run.Goal ?? string.Empty, profile);
        ctx.SetNudge(nudge); // Batch 08 D4: scope-to-dispatch — set before BeginRunAsync/SafeSeedResumeContext read ctx
        var cancelled = false;
        var failed = false;
        Guid? runFirst = null;
        var runLast = Guid.Empty;

        // The step this loop currently has in flight, hoisted to RunAsync scope because `step` is
        // scoped to the drain `while` below and is NOT in scope in the catch(OperationCanceledException) arm —
        // which is where a step that never returned (an OCE out of the persona resolve, or Live's second escape
        // hatch, both of which leave the row Running) has to be given back to the plan.
        Guid? inflightStepId = null;

        // On resume, seed the range from the persisted pre-pause slice so the terminal PinRange
        // EXTENDS the run's transcript range rather than shrinking it to only the post-resume portion.
        // runFirst's ??= below then keeps this original first message; runLast advances to the latest.
        if (resume)
        {
            runFirst = run.FirstMessageId;
            runLast = run.LastMessageId ?? Guid.Empty;
        }

        // Pin the run-level transcript slice off the STABLE step message Ids accrued so far.
        // Shared by every terminal path (success, truncation, cancel, fail) so a run that executed
        // steps never keeps a null range — symmetric with the clean-success path.
        Task PinRange() => PinRangeAsync(run.Id, runFirst, runLast, cts.Token);

        // D1 collision hardening 2 (no pause request may survive a DISPATCH boundary) used to be a
        // blind `_steering?.RevokePauseRequest(run.Id)` HERE, and F3 is what that cost. THE OWNERSHIP
        // RULE, one sentence: a pause request belongs to the dispatch whose cancel sink was registered when it
        // was recorded — the sink it actually fired. This loop cannot evaluate that rule. Its run id is the
        // only thing it knows, and a request against that id is EITHER one the superseded dispatch left behind
        // (drop it) OR one the user typed a beat ago against THIS dispatch's sink, in the ramp-up between
        // HeadlessRunLauncher's RegisterDispatch and this line (honour it). Revoking blindly gets the second
        // case exactly backwards: it discards the request while the cancel it fired stands, so the first step
        // came back cancelled with nothing to consume, SafeFail(cancelled: true) stamped CompletedAt, and the
        // run settled TERMINALLY — after PauseAsync had already returned true to the user.
        //
        // So the revoke moved to the one place that CAN evaluate the rule: IRunSteeringStore.RegisterDispatch,
        // the exact instant ownership changes hands. By the time this line runs, a superseded dispatch's
        // request is already gone and anything still standing is ours to honour at the first boundary below.
        try
        {
            await executor.BeginRunAsync(run, ctx, cts.Token).ConfigureAwait(false);

            // E2: RunContext is built fresh per RunAsync, so without this a resumed run's critic (and any
            // replan) would judge the goal on ONLY the post-resume steps — the pre-pause work, and with H1
            // its declared artifacts, would be invisible. Seed the persisted Done steps before anything
            // reads ctx.CompletedSteps.
            if (resume)
                await SafeSeedResumeContext(run.Id, ctx, cts.Token).ConfigureAwait(false);

            // A resume must NOT re-plan: ReplaceStepsAsync writes the plan verbatim and does not preserve Done
            // steps, so re-planning would wipe the persisted Done+Pending steps and re-run the goal from scratch.
            // The one exception is a run parked with NeedsGoalReason and zero persisted step rows — it never got
            // a plan (the decline branch below returns before SafeReplaceSteps), so draining it would settle the
            // run Completed without ever answering the user's clarification.
            var rePlanAfterClarification = resume
                && await TryEnterClarificationRePlanAsync(run.Id, ctx, parkReason, cts.Token).ConfigureAwait(false);

            if (!resume || rePlanAfterClarification)
            {
                await SafeSetState(run.Id, AgentRunState.Planning, cts.Token).ConfigureAwait(false);

                var plan = await _planner.PlanAsync(ctx.Goal, ctx, persona, provider, cts.Token).ConfigureAwait(false);
                // I1: the plan turn's rounds (≥2, doubled by the firm retry) are real spend — accrue
                // them run-level BEFORE branching, so neither the degrade path nor the decline path below
                // can drop them.
                await SafeAddUsage(run.Id, plan.Usage, cts.Token).ConfigureAwait(false);

                // Must run BEFORE the R10 single-turn fallback below: falling into that fallback would send
                // an ungroundable goal as one ordinary chat turn and settle the run Completed regardless.
                if (plan.CannotGroundGoal)
                {
                    await ParkForUngroundableGoalAsync(
                        executor, run, ctx, persona, plan.ClarificationQuestion, cts.Token).ConfigureAwait(false);
                    return;
                }

                if (plan.FallBackToSingleTurn) // R10
                {
                    await RunDegradedSingleTurnAsync(executor, run, ctx, cts.Token).ConfigureAwait(false);
                    return;
                }

                await SafeReplaceSteps(run.Id, plan.Steps, cts.Token).ConfigureAwait(false);

                // First plan only: a replan-after-failure and the verify-fail replan both live outside this
                // `if`, and a resume-without-replan never enters it. A re-plan after a clarification answer
                // reaches this line but never gates either — every resume dispatches headless, and only the
                // live executor supports approval.
                if (plan.Steps.Count >= 3 && executor.SupportsPlanApproval)
                {
                    await ParkForPlanApprovalAsync(executor, run, ctx, persona, plan.Steps, cts.Token).ConfigureAwait(false);
                    return;
                }
            }
            // Resume: TryBeginResumeAsync already CAS'd State→Running; the drain loop re-sets Running per
            // step. The persisted Pending remainder drives the loop — no re-plan, no step wipe.

            var replans = 0;
            var unverifiedTruncated = false;

            // The step-failure replan branch, extracted so the in-process path and the fan-out path share ONE
            // copy of the replan budget, the KeepDone re-ordinaling and the two terminal fails. Returns true
            // when the caller should `continue` the drain loop (revised steps were written), false when the run
            // is over — `failed` is already set and the terminal Fail already written in that case.
            async Task<bool> TryReplanAfterFailureAsync(string? error)
            {
                if (replans++ < profile.MaxReplans)
                {
                    // Tell the replanner what the USER removed, read fresh from the persisted
                    // plan. Seeded here rather than at the mutation or at resume-seed time because those are
                    // two places to keep in step with a third (a skip can land at any point while the run is
                    // paused, from the panel or from a second window) and the DB is the only record that sees
                    // all of them. Failure-isolated: a faulted read means the replan runs without the block,
                    // exactly as it did before, never a failed run.
                    ctx.SetSkippedTitles(await SafeSkippedTitlesAsync(run.Id, cts.Token).ConfigureAwait(false));
                    var revised = await _planner.ReplanAsync(ctx, error, persona, provider, cts.Token).ConfigureAwait(false);
                    await SafeAddUsage(run.Id, revised.Usage, cts.Token).ConfigureAwait(false); // I1
                    if (!revised.FallBackToSingleTurn)
                    {
                        // Keep the Done steps (immutable, original Ids preserved), append the revised
                        // steps continuing the ordinal sequence; ReplaceSteps writes ordinals verbatim.
                        var doneSteps = await KeepDoneAsync(run.Id, cts.Token).ConfigureAwait(false);
                        var offset = doneSteps.Count;
                        var revisedSteps = revised.Steps.Select((s, i) => { s.Ordinal = offset + i; return s; });
                        await SafeReplaceSteps(run.Id, doneSteps.Concat(revisedSteps).ToList(), cts.Token).ConfigureAwait(false);
                        return true; // re-query picks up the revised steps (R2)
                    }
                    // Replan itself degraded to Fallback → the same terminal fail as an exhausted budget
                }

                failed = true;
                await PinRange().ConfigureAwait(false); // R3: keep the executed-so-far slice
                await SafeFail(run.Id, error, cancelled: false).ConfigureAwait(false);
                return false;
            }

            // Outer verify → replan → re-drain loop. Verify feeds the SAME `replans`/profile.MaxReplans
            // budget as step-failure replan (guardrail 3: no replan storm — a run that keeps failing
            // verify terminates as Completed+truncated "unverified", never loops forever).
            while (true)
            {
                // Re-query the persisted Pending list each iteration — a foreach over a snapshot
                // would never run replanned steps.
                while (await _runService.NextPendingStepAsync(run.Id, cts.Token).ConfigureAwait(false) is { } step)
                {
                    if (ctx.StepBudgetExceeded || ctx.WallClockExceeded) // R5: both checks, never silent
                    {
                        await ParkAtBudgetAsync(executor, run, ctx, runFirst, runLast, cts.Token).ConfigureAwait(false);
                        return;
                    }

                    // D7/a step the plan put in a PARALLEL GROUP is not executed in-process —
                    // the whole group is dispatched as sibling CHILD runs and awaited here. Null covers every
                    // ordinary step, which is every step of every plan a build with no persona roster produces
                    // (the planner only ever writes a parallelGroup when a roster is configured).
                    var fanOut = await TryFanOutAsync(run, step, ctx, profile, cts).ConfigureAwait(false);
                    if (fanOut is { } children)
                    {
                        if (children.Abandoned)
                        {
                            // 07 the un-park CAS lost — this run's row now belongs to another writer
                            // (cascade-cancelled, or re-parked by a startup reconcile in another process).
                            // Deliberately MINIMAL: no SafeFail, no PinRange, no promotion, no EndRun — every
                            // one of those writes a row whose terminal state we no longer own, which is the
                            // whole reason the CAS exists. The one thing that is still ours is the EXECUTOR:
                            // for a Live run only a release hook clears the session's IsStreaming, so without
                            // SafeOnPaused the foreground chat would sit wedged Running forever with a
                            // disabled Send. That is the same non-terminal release the budget pause uses, and
                            // it touches the session, never the run.
                            _logger.LogInformation(
                                "Run {RunId} is no longer awaiting its children — another writer owns it; releasing", run.Id);
                            await SafeOnPaused(executor, run, ctx).ConfigureAwait(false);
                            return;
                        }

                        if (children.Cancelled)
                        {
                            cancelled = true;
                            await PinRange().ConfigureAwait(false); // R3: keep the executed-so-far slice
                            await SafeFail(run.Id, children.Error, cancelled: true).ConfigureAwait(false);
                            break;
                        }

                        // THE PARENT'S OWN TRANSITION. The cascade paused the children; this is
                        // where the parent's row follows them. Consuming the request is what picks between the
                        // two parks, and it is a consume rather than a peek for the same reason the in-process
                        // branch's is: a request is honoured exactly once.
                        //
                        // UNCONDITIONAL, not nested inside the AnyParked arm ( F2/F6). A request can
                        // reach this boundary with NOTHING parked, and used to be dropped on the floor when it
                        // did: the cascade never fires the parent's own token, so a fan-out whose children were
                        // outside the pausable set, or had already settled, or were dispatched entirely inside
                        // the prologue the pause landed in, comes back clean — and the loop then `continue`d,
                        // ran the next step and settled the run Completed with the user's accepted pause never
                        // honoured. The fan-out boundary is where a delegating parent's request has to land,
                        // whatever its children did.
                        //
                        // By this point the row is already Running — the un-park CAS ran INSIDE TryFanOutAsync,
                        // before this caller ever saw the result — which is why TryPauseUserAsync's source set
                        // contains Running. The ledger clocks line up: TryEndChildWaitAsync opened a work
                        // segment and TryPauseUserAsync closes it.
                        //
                        // Order is D1 item 6's, and the sibling steps are already back at Pending (the fan-out's
                        // parked arm did that per child): PinRange → the CAS → the non-terminal executor
                        // release → return. A lost CAS still releases the executor and returns — the row is not
                        // ours to correct, but the session is. AFTER the Cancelled check above, always: terminal
                        // intent outranks a pause, and it must keep outranking it here.
                        if (children.PauseRequested || _steering?.TryConsumePauseRequest(run.Id) == true)
                        {
                            await PinRange().ConfigureAwait(false);
                            await SafePauseUser(run.Id).ConfigureAwait(false);
                            await SafeOnPaused(executor, run, ctx).ConfigureAwait(false);
                            return;
                        }

                        if (children.AnyParked)
                        {
                            // A PARKED child is not a finished child. Its work is durable and resumable,
                            // so failing the parent would throw it away and burn a replan. Re-park the parent
                            // through the existing budget-pause shape — its fan-out steps are still Pending, so
                            // one Continue on the parent re-dispatches the group (and cancels this generation
                            // first). Deliberately NO SafeEndRun and no promotion: a park is not terminal.
                            await PinRange().ConfigureAwait(false);
                            await SafePause(run.Id, cts.Token, reason: ChildrenParkedReason).ConfigureAwait(false);
                            await SafeOnPaused(executor, run, ctx).ConfigureAwait(false);
                            return;
                        }

                        if (children.AnyFailed)
                        {
                            if (await TryReplanAfterFailureAsync(children.Error).ConfigureAwait(false))
                                continue;
                            break;
                        }

                        continue; // every sibling settled Done — re-query for the next pending step (R2)
                    }

                    await SafeSetState(run.Id, AgentRunState.Running, cts.Token).ConfigureAwait(false);
                    await SafeSetStepStatus(step.Id, AgentStepStatus.Running, cts.Token).ConfigureAwait(false);
                    inflightStepId = step.Id; // D1: what the catch(OCE) arm has to restore (see the hoist above)

                    // Nested inside the run scope, so a step turn's lines read "[run … step N]". The
                    // ORDINAL, not the step id: it is what the plan, the panel and the audit table all show, and
                    // so it is what a person matches a log line against.
                    //
                    // It brackets the EXECUTOR CALL only, not the rest of this iteration. That call is where the
                    // lines come from — the model exchange, the tool dispatch, every gate decision inside it —
                    // while the bookkeeping below already names its own {StepId}/{RunId}. Widening the scope over
                    // the whole block would re-indent a hundred lines of load-bearing ordering for no line that
                    // is not already attributable.
                    StepTurnResult r;
                    using (_logger.BeginScope("step {StepOrdinal}", step.Ordinal))
                    {
                        r = await executor.ExecuteStepAsync(run, step, ctx, cts.Token).ConfigureAwait(false); // critical path
                    }

                    // ---- USER PAUSE, tested BEFORE the step is recorded and BEFORE r.Cancelled ----
                    // Ordering is the whole design and it is deliberate in three ways.
                    //
                    // (1) BEFORE SafeRecordStep. That call is UNCONDITIONAL and maps !Succeeded → Failed(3), a
                    // status invisible to NextPendingStepAsync AND dropped by KeepDoneAsync — so recording
                    // the aborted step would delete it from the resumed plan while the panel still showed
                    // it. It also writes the step's First/LastMessageId and its per-step ledger entry, and
                    // D2 says the aborted step's TEXT is discarded so the step re-runs clean. ctx.RecordStep
                    // is skipped for the same reason: it would burn a step against ctx.StepsExecuted and
                    // hand the critic a step that never finished.
                    // (2) NOT gated on r.Cancelled, and never &&-ed with it. On Live the pause releases a
                    // pending action card, which ChatSession maps to ToolDecision.Decline — the exchange
                    // CONTINUES and can return Succeeded:false, Cancelled:false, which would fall into the
                    // replan arm below: the user clicks Pause and the run replans. There is no scenario in
                    // which the request exists and a pause is not wanted (only the pause command writes it,
                    // and a request belonging to a superseded dispatch was dropped when this dispatch
                    // registered its sink — the F3 ownership rule), so the request alone decides.
                    // (3) CancellationToken.None throughout the branch: cts.Token is already cancelled by the
                    // sink that produced this abort. Neither SetStepStatusAsync nor SetRunMessageRangeAsync
                    // inspects its token today, but passing None states the intent rather than relying on it.
                    if (_steering?.TryConsumePauseRequest(run.Id) == true)
                    {
                        await ParkForUserPauseAsync(
                            executor, run, ctx, step.Id, r.Usage, runFirst, runLast, cts.Token).ConfigureAwait(false);
                        return;
                    }

                    // ---- THE UNATTENDED APPROVAL PARK ----
                    // The step stopped on a capability a human could legitimately approve. Same slot, same
                    // order and the same four moves as the user pause above, for the same three reasons —
                    // and one more that is specific to this branch:
                    //
                    // (1) BEFORE SafeRecordStep, so the step goes back to Pending rather than being written
                    // Failed(3) — a status NextPendingStepAsync cannot see and KeepDoneAsync drops, i.e.
                    // the resumed run would silently lose the very step that asked the question.
                    // (2) AFTER the user-pause branch, never merged with it. A user pause is a USER's
                    // terminal-ish intent and outranks the run's own request; if both are true the run
                    // parks as a user pause and the approval question is re-asked on the next attempt.
                    // (3) NOT &&-ed with r.Succeeded or r.Cancelled. A denied tool very often makes the model
                    // declare emit_step_result{succeeded:false}, and reading that as an ordinary step
                    // failure would burn a replan on a step that is only waiting. r.Cancelled is checked
                    // further down and cannot be true here: the executor returns the park BEFORE the
                    // cancelled arm can produce a result.
                    // (4) The tokens the abandoned step spent are BILLED run-level (stepId: null), because a
                    // step that will re-run must not carry a per-step ledger entry for the attempt that
                    // did not finish.
                    if (r.ApprovalRequiredTool is { } approvalTool)
                    {
                        await ParkForToolApprovalAsync(
                            executor, run, ctx, step.Id, r.Usage, approvalTool, runFirst, runLast, cts.Token)
                            .ConfigureAwait(false);
                        return;
                    }

                    // The step called request_user_input, blocking on something only the run's owner can
                    // answer. Checked AFTER the approval park above (never merged with it) and NOT &&-ed with
                    // r.Succeeded/r.Cancelled, since a step that stops to ask often also reports
                    // succeeded:false in the same exchange.
                    //
                    // The step's row goes back to Pending and re-runs from the top on resume, so any side
                    // effect it already committed may repeat; tool handlers refuse a pending write once the ask
                    // is recorded. A NeedsInputReason resume does not re-plan (unlike NeedsGoalReason): this run
                    // already has Done/Pending step rows to preserve.
                    if (r.UserInputQuestion is { } question)
                    {
                        await ParkForUserInputAsync(
                            executor, run, ctx, persona, step, r.Usage, question, runFirst, runLast, cts.Token)
                            .ConfigureAwait(false);
                        return;
                    }

                    await SafeRecordStep(step.Id, r, cts.Token).ConfigureAwait(false); // R16 ledger + R3 slice
                    ctx.RecordStep(step, r);
                    // Track only valid (non-empty) message Ids so a step that produced no transcript
                    // (e.g. a cancelled step) never poisons the run-level range with Guid.Empty.
                    if (r.FirstMessageId != Guid.Empty) runFirst ??= r.FirstMessageId;
                    if (r.LastMessageId != Guid.Empty) runLast = r.LastMessageId;
                    inflightStepId = null; // the step has settled; a later OCE must not re-open it

                    if (r.Cancelled)
                    {
                        cancelled = true;
                        await PinRange().ConfigureAwait(false); // R3: keep the executed-so-far slice
                        await SafeFail(run.Id, r.Error, cancelled: true).ConfigureAwait(false);
                        break;
                    }

                    if (!r.Succeeded)
                    {
                        if (await TryReplanAfterFailureAsync(r.Error).ConfigureAwait(false))
                            continue;
                        break;
                    }
                }

                // Cancel/step-fail already wrote their terminal Fail inside the loop — skip verify and
                // leave the outer loop; the terminal-settle block's else-branch runs EndRun only.
                if (cancelled || failed)
                    break;

                // Clean drain → the terminal critic pass (both executors; executor-agnostic like the planner).
                await SafeSetState(run.Id, AgentRunState.Verifying, cts.Token).ConfigureAwait(false);
                var verdict = await SafeVerify(run.Id, ctx, persona, provider, cts.Token).ConfigureAwait(false);
                await SafeAddUsage(run.Id, verdict.Usage, cts.Token).ConfigureAwait(false); // run-level (stepId: null)

                if (verdict.Passed)
                    break; // accept → clean Complete below

                // Verify FAIL → feed the SHARED replan budget.
                if (replans++ < profile.MaxReplans)
                {
                    var revised = await _planner.ReplanAsync(ctx, BuildVerifyFailureReason(verdict), persona, provider, cts.Token).ConfigureAwait(false);
                    await SafeAddUsage(run.Id, revised.Usage, cts.Token).ConfigureAwait(false); // I1
                    if (!revised.FallBackToSingleTurn)
                    {
                        var doneSteps = await KeepDoneAsync(run.Id, cts.Token).ConfigureAwait(false);
                        var offset = doneSteps.Count;
                        var revisedSteps = revised.Steps.Select((s, i) => { s.Ordinal = offset + i; return s; });
                        await SafeReplaceSteps(run.Id, doneSteps.Concat(revisedSteps).ToList(), cts.Token).ConfigureAwait(false);
                        continue; // re-drain: re-enter the outer loop; NextPendingStepAsync picks up revised steps
                    }
                    // Replan itself degraded to Fallback → settle unverified, NOT Failed (steps genuinely ran)
                }

                unverifiedTruncated = true; // replans exhausted OR replan degraded
                break;
            }

            await SettleTerminalAsync(
                executor, run, ctx, cancelled, failed, unverifiedTruncated, runFirst, runLast, cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // D1, the SECOND pause site, and it is not redundant with the one in the drain loop: an
            // abort can leave the loop by THROWING rather than returning a result. Three reachable shapes —
            // an OCE out of the per-step persona resolve (awaited before either executor's exchange try/catch,
            // so the step stays Running(1)), Live's second escape hatch (its UI-thread post rethrows, so
            // ExecuteStepAsync throws instead of returning), and a pause that lands during the terminal critic
            // (no step in flight at all, which is why the restore is conditional).
            if (_steering?.TryConsumePauseRequest(run.Id) == true)
            {
                if (inflightStepId is { } sid)
                    await SafeSetStepStatus(sid, AgentStepStatus.Pending, CancellationToken.None).ConfigureAwait(false);
                // No usage to bill here: the step threw, so there is no StepTurnResult to read one from.
                await PinRange().ConfigureAwait(false);
                await SafePauseUser(run.Id).ConfigureAwait(false);
                await SafeOnPaused(executor, run, ctx).ConfigureAwait(false); // never SafeEndRun — a pause is not terminal
                return;
            }

            // A cancel can now surface here from the in-flight verify turn (SafeVerify rethrows a
            // genuine run cancel) — after the steps drained but before the terminal-settle PinRange. Pin
            // the executed-so-far slice first so a transcript-producing run never settles Cancelled with a
            // null range (mirrors the step-cancel path above; SetRunMessageRange ignores the cancelled ct).
            await PinRange().ConfigureAwait(false);
            await SafeFail(run.Id, null, cancelled: true).ConfigureAwait(false);
            await SafeEndRun(executor, run, ctx, cancelled: true, failed: false).ConfigureAwait(false);
        }
        catch (Exception ex) // planner-cannot-plan (threw) / executor crash — critical path, fail the run
        {
            _logger.LogError(ex, "Agent run {RunId} failed", run.Id);
            await SafeFail(run.Id, ex.Message, cancelled: false).ConfigureAwait(false);
            await SafeEndRun(executor, run, ctx, cancelled: false, failed: true).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// pin the run-level transcript slice off the STABLE step message Ids accrued so far. Shared by every
    /// terminal path (success, truncation, cancel, fail) so a run that executed steps never keeps a null range —
    /// symmetric with the clean-success path.
    /// </summary>
    private async Task PinRangeAsync(Guid runId, Guid? runFirst, Guid runLast, CancellationToken ct)
    {
        if (runFirst is { } first)
            await SafeRange(runId, first, runLast, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The plan turn declined the goal as ungroundable: park with NO steps, which is what lets a resume tell this
    /// park apart from a mid-plan one, and ask the question.
    /// </summary>
    private async Task ParkForUngroundableGoalAsync(
        IAgentTurnExecutor executor, AgentRun run, RunContext ctx, Persona persona,
        string? clarificationQuestion, CancellationToken ct)
    {
        // No PinRange/SafeReplaceSteps: nothing ran, so the run keeps zero step rows.
        //
        // The question is user-derived content already logged via SensitiveDebug in AgentPlanner;
        // don't log it again here — only app-owned facts (run id, token, whether one was worded).
        _logger.LogInformation(
            "Run {RunId}: the plan turn declined the goal as ungroundable → parking {Reason} with no steps "
            + "(question present={Present})",
            run.Id, NeedsGoalReason, clarificationQuestion is not null);

        // Same non-terminal park as the budget cap, differing only by the reason token.
        await SafePause(run.Id, ct, reason: NeedsGoalReason).ConfigureAwait(false);
        // No SafeEndRun/SafeComplete/SafeFail: a park is not terminal. OnPausedAsync clears
        // IsStreaming for a live session — skip it and the chat sits wedged Running with Send
        // disabled.
        await SafeOnPaused(executor, run, ctx).ConfigureAwait(false);

        // Posts the question into the run's chat and mirrors it live under one minted message id;
        // a blank question is a no-op, not a fabricated placeholder.
        await PostAndMirrorClarificationQuestionAsync(executor, run, ctx, persona, clarificationQuestion)
            .ConfigureAwait(false);

        // A resume re-enters planning via TryEnterClarificationRePlanAsync; the answer persists in
        // AgentRuns.ClarificationsJson since the resume claim nulls ExtraJson.
    }

    /// <summary>
    /// Park the run before its first step so a human can Approve or Reject the plan. No PinRange/step handling —
    /// nothing has run yet, mirroring <see cref="ParkForUngroundableGoalAsync"/>.
    /// </summary>
    private async Task ParkForPlanApprovalAsync(
        IAgentTurnExecutor executor, AgentRun run, RunContext ctx, Persona persona,
        IReadOnlyList<AgentStep> steps, CancellationToken ct)
    {
        _logger.LogInformation(
            "Run {RunId}: first plan has {StepCount} step(s) → parking {Reason} for approval",
            run.Id, steps.Count, PlanApprovalReason);

        await SafePause(run.Id, ct, reason: PlanApprovalReason).ConfigureAwait(false);
        // Non-terminal executor release, same as every other park: clears IsStreaming so Send/RunInBackground
        // re-enable while the run sits parked.
        await SafeOnPaused(executor, run, ctx).ConfigureAwait(false);

        var summary = BuildPlanSummaryText(
            steps, _localization?["Run_PlanProposed_ChatIntro"] ?? DefaultPlanProposedIntro);
        await PostAndMirrorClarificationQuestionAsync(executor, run, ctx, persona, summary).ConfigureAwait(false);
    }

    /// <summary>Step titles only — intent/artifact text is longer than what a person needs to decide Approve vs
    /// Reject.</summary>
    private static string BuildPlanSummaryText(IReadOnlyList<AgentStep> steps, string intro)
    {
        var sb = new StringBuilder();
        sb.AppendLine(intro);
        foreach (var step in steps)
            sb.AppendLine($"{step.Ordinal + 1}. {step.Title}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>The "plan rejected" chat notice, posted after the row has already settled. Durable post only:
    /// the run's live session was released at park time, so there is nothing left to mirror into.</summary>
    public async Task PostPlanRejectedNoticeAsync(Guid runId, Persona persona, CancellationToken ct)
    {
        if (_localization is null)
            return;

        var run = await SafeGetRunAsync(runId, ct).ConfigureAwait(false);
        if (run is null)
            return;

        await SafePostClarificationQuestionAsync(
            run, persona, Guid.NewGuid(), _localization["Run_PlanRejected_ChatNote"]).ConfigureAwait(false);
    }

    /// <summary>the planner degraded, so the goal runs as ONE ordinary chat turn and the run settles here —
    /// this arm never reaches the terminal-settle block.</summary>
    private async Task RunDegradedSingleTurnAsync(
        IAgentTurnExecutor executor, AgentRun run, RunContext ctx, CancellationToken ct)
    {
        var cancelled = false;
        var failed = false;

        var fr = await executor.RunSingleTurnFallbackAsync(run, ctx, ct).ConfigureAwait(false);
        // The fallback turn owns no step row, so its usage has no SafeRecordStep to ride on —
        // accrue it run-level here or the whole degrade path bills as zero tokens. Before
        // the branch: a cancelled/failed fallback turn spent its tokens just the same.
        await SafeAddUsage(run.Id, fr.Usage, ct).ConfigureAwait(false);
        if (fr.Cancelled)
        {
            cancelled = true;
            await SafeFail(run.Id, fr.Error, cancelled: true).ConfigureAwait(false);
        }
        else if (!fr.Succeeded)
        {
            // R5/a failed fallback turn is never presented as a clean Completed run.
            failed = true;
            await SafeFail(run.Id, fr.Error, cancelled: false).ConfigureAwait(false);
        }
        else
        {
            if (fr.FirstMessageId != Guid.Empty)
                await SafeRange(run.Id, fr.FirstMessageId, fr.LastMessageId, ct).ConfigureAwait(false);
            // B8, the SECOND terminal path: it settles Complete BEFORE EndRun — the opposite order
            // to the main path. Promotion still goes before CompleteAsync. There is no verify on this arm at
            // all (the planner degraded), so "promote what the turn wrote" is the whole contract.
            await SafePromote(run, ctx, ct).ConfigureAwait(false);
            await SafeComplete(run.Id, ct).ConfigureAwait(false); // clean; zero steps recorded
        }
        await SafeEndRun(executor, run, ctx, cancelled, failed).ConfigureAwait(false);
    }

    /// <summary>the run reached its step cap or wall clock. Not terminal — it parks resumable.</summary>
    private async Task ParkAtBudgetAsync(
        IAgentTurnExecutor executor, AgentRun run, RunContext ctx,
        Guid? runFirst, Guid runLast, CancellationToken ct)
    {
        // ONE tool-free wrap-up turn before the park, so the chat a person opens hours
        // later ends with "here is where I got to" instead of the last step's output. Before
        // PinRange, because its messages belong in the run's transcript slice; it cannot stop
        // the park (see SafeGraceTurn).
        if (await SafeGraceTurn(executor, run, ctx, ct).ConfigureAwait(false) is { } grace)
        {
            await SafeAddUsage(run.Id, grace.Usage, CancellationToken.None).ConfigureAwait(false);
            if (grace.FirstMessageId != Guid.Empty) runFirst ??= grace.FirstMessageId;
            if (grace.LastMessageId != Guid.Empty) runLast = grace.LastMessageId;
        }

        await PinRangeAsync(run.Id, runFirst, runLast, ct).ConfigureAwait(false); // R3: keep the executed-so-far slice
        await SafePause(run.Id, ct,
            reason: ctx.WallClockExceeded ? "wall-clock" : "step-cap").ConfigureAwait(false);
        // Pause is NOT terminal: deliberately NO SafeEndRun — Live must not settle
        // ChatState.Completed or raise TurnCompleted, and Headless must not
        // persist-and-finalize here. But the executor still needs a NON-terminal release
        // hook: for a Live run, only EndRunAsync clears the session's IsStreaming, so
        // without this the foreground chat would be wedged Running forever (spinner +
        // disabled Send). OnPausedAsync settles the live session to Idle (no TurnCompleted,
        // no Completed/Error); Headless no-ops. The run sits WaitingForInput until
        // TryBeginResumeAsync claims it.
        await SafeOnPaused(executor, run, ctx).ConfigureAwait(false);
    }

    /// <summary>
    /// The three moves every mid-step park makes before it writes its own park reason: the step goes back to
    /// Pending, its spent tokens are billed RUN-level, and the executed-so-far slice is pinned.
    /// </summary>
    /// <remarks>
    /// Pending rather than the Failed(3) an unconditional SafeRecordStep would write: that status is invisible to
    /// <c>NextPendingStepAsync</c> AND dropped by <see cref="KeepDoneAsync"/>, so recording the abandoned step
    /// would delete it from the resumed plan while the panel still showed it. Run-level usage (stepId: null) for
    /// the matching reason — a step that will re-run must not carry a per-step ledger entry for the attempt that
    /// did not finish. CancellationToken.None on both writes: the step's own token is typically already cancelled.
    /// </remarks>
    private async Task ReturnStepToPendingAsync(
        Guid runId, Guid stepId, UsageDetails? usage, Guid? runFirst, Guid runLast, CancellationToken pinToken)
    {
        await SafeSetStepStatus(stepId, AgentStepStatus.Pending, CancellationToken.None).ConfigureAwait(false);
        await SafeAddUsage(runId, usage, CancellationToken.None).ConfigureAwait(false);
        await PinRangeAsync(runId, runFirst, runLast, pinToken).ConfigureAwait(false); // R3
    }

    /// <summary>the user asked for a pause and this dispatch owns the request.</summary>
    /// <remarks>
    /// Order is fixed by D1 item 6 — a tidy-up reorder breaks a scheduled job, because
    /// ScheduledJobBackgroundService reads the row AFTER awaiting handle.Completion and books anything not yet
    /// Paused/WaitingForInput as a FAILURE (a strike, and a <c>Once</c> job is retired on the first one). The row
    /// must read Paused before this dispatch returns.
    /// </remarks>
    private async Task ParkForUserPauseAsync(
        IAgentTurnExecutor executor, AgentRun run, RunContext ctx, Guid stepId, UsageDetails? usage,
        Guid? runFirst, Guid runLast, CancellationToken ct)
    {
        await ReturnStepToPendingAsync(run.Id, stepId, usage, runFirst, runLast, ct).ConfigureAwait(false);
        // The CAS may LOSE (another writer settled this run while the step unwound). Release the
        // executor and return either way: the row is not ours to correct, but the SESSION is —
        // the same split the fan-out's Abandoned arm makes, for the same reason. Falling through
        // to SafeRecordStep after a lost CAS would write Failed over the Pending we just set.
        await SafePauseUser(run.Id).ConfigureAwait(false);
        // Non-terminal executor release: NOT SafeEndRun. Live must not settle
        // ChatState.Completed or raise TurnCompleted; OnPausedAsync drops the session to Idle so
        // Send re-enables while the run sits resumable. Headless no-ops.
        await SafeOnPaused(executor, run, ctx).ConfigureAwait(false);
    }

    /// <summary>the step stopped on a capability a human could legitimately approve.</summary>
    private async Task ParkForToolApprovalAsync(
        IAgentTurnExecutor executor, AgentRun run, RunContext ctx, Guid stepId, UsageDetails? usage,
        string approvalTool, Guid? runFirst, Guid runLast, CancellationToken ct)
    {
        await ReturnStepToPendingAsync(run.Id, stepId, usage, runFirst, runLast, ct).ConfigureAwait(false);
        await SafeRequestApproval(run.Id, approvalTool).ConfigureAwait(false);
        // Non-terminal executor release: NOT SafeEndRun. A park is not the end
        // of a run, and the Headless executor must not persist-and-finalize a chat whose last
        // step is going to run again.
        await SafeOnPaused(executor, run, ctx).ConfigureAwait(false);
    }

    /// <summary>
    /// The step called request_user_input. The step's row goes back to Pending and re-runs from the top on resume,
    /// so any side effect it already committed may repeat; tool handlers refuse a pending write once the ask is
    /// recorded. A NeedsInputReason resume does not re-plan (unlike NeedsGoalReason): this run already has
    /// Done/Pending step rows to preserve.
    /// </summary>
    private async Task ParkForUserInputAsync(
        IAgentTurnExecutor executor, AgentRun run, RunContext ctx, Persona persona, AgentStep step,
        UsageDetails? usage, string userInputQuestion, Guid? runFirst, Guid runLast, CancellationToken ct)
    {
        await ReturnStepToPendingAsync(run.Id, step.Id, usage, runFirst, runLast, ct).ConfigureAwait(false);

        // App-owned facts only; the question itself is user content and only ever reaches the
        // SensitiveDebug call below, which is compiled out of Release.
        _logger.LogInformation(
            "Run {RunId}: step {StepOrdinal} asked the user for input → parking {Reason}; the step "
            + "returns to Pending and re-runs from the start on resume",
            run.Id, step.Ordinal, NeedsInputReason);
        _logger.SensitiveDebug("Mid-plan clarification question: {Question}", userInputQuestion);

        // CancellationToken.None: the step's own token may already be cancelled, and a park
        // that doesn't reach the row leaves the run stuck Running, unresumable.
        await SafePause(run.Id, CancellationToken.None, reason: NeedsInputReason).ConfigureAwait(false);
        // Non-terminal executor release: NOT SafeEndRun — same as every other park.
        await SafeOnPaused(executor, run, ctx).ConfigureAwait(false);

        // Reuses the same post-and-mirror call the plan-time decline makes, so the two parks
        // can't drift apart.
        await PostAndMirrorClarificationQuestionAsync(executor, run, ctx, persona, userInputQuestion)
            .ConfigureAwait(false);
    }

    /// <summary>The single terminal settle (SafeEndRun → SafeComplete, exactly once, every path).</summary>
    private async Task SettleTerminalAsync(
        IAgentTurnExecutor executor, AgentRun run, RunContext ctx, bool cancelled, bool failed,
        bool unverifiedTruncated, Guid? runFirst, Guid runLast, CancellationToken ct)
    {
        if (cancelled || failed)
        {
            await SafeEndRun(executor, run, ctx, cancelled, failed).ConfigureAwait(false);
            return;
        }

        await PinRangeAsync(run.Id, runFirst, runLast, ct).ConfigureAwait(false);
        // Order: END the run (Live: settle terminal state; Headless: persist the chat) BEFORE
        // marking it Completed — so no crash / RunChanged consumer observes a Completed run whose chat
        // is not yet persisted (headless persists only in EndRunAsync). A verify-unverified run
        // settles Completed+truncated reason "unverified".
        await SafeEndRun(executor, run, ctx, cancelled, failed).ConfigureAwait(false);
        // Promote BEFORE CompleteAsync. Verify has already run against the RUN ROOT, so the
        // artifacts it confirmed are the files being promoted; and no RunChanged
        // consumer can observe a Completed run whose deliverables are still only in a workspace the
        // sweep may delete (plan R4/R5) — which is what dissolves the "Completed but not yet
        // promoted" window without a promotion-aware sweep. Failure-isolated: a promotion fault
        // leaves the files in the workspace for the publish affordance to offer, and never fails an
        // otherwise-successful run.
        await SafePromote(run, ctx, ct).ConfigureAwait(false);
        await SafeComplete(run.Id, ct,
            truncated: unverifiedTruncated,
            reason: unverifiedTruncated ? UnverifiedTruncationReason : null).ConfigureAwait(false);
    }

    /// <summary>
    /// The steps a replan must CARRY FORWARD, re-ordinaled 0..k-1 with their ORIGINAL Ids preserved so
    /// <see cref="IAgentRunService.ReplaceStepsAsync"/> re-inserts them (it writes ordinals verbatim
    /// and does not itself preserve anything — §F/ <c>KeepDone</c>).
    /// The filter is Done OR <see cref="AgentStepStatus.Skipped"/>: a skip is the USER's
    /// decision about their own plan, so a later replan must not quietly re-add work they removed — and
    /// because this method's output is the whole surviving plan, a status left out here is DELETED from the
    /// run. <b>Deliberately different from <see cref="SafeSeedResumeContext"/>'s filter, which stays
    /// <c>== Done</c> and must not be "aligned" with this one:</b> that one builds the critic's list of
    /// completed work, and a skipped step never ran, so it has no result to report and no
    /// <c>ExpectedArtifact</c> worth probing.
    /// </summary>
    private async Task<List<AgentStep>> KeepDoneAsync(Guid runId, CancellationToken ct)
    {
        AgentRun? run = null;
        try { run = await _runService.GetAsync(runId, ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "KeepDone: failed to read run {RunId}", runId); }

        var done = run?.Plan
                       .Where(s => s.Status is AgentStepStatus.Done or AgentStepStatus.Skipped)
                       .OrderBy(s => s.Ordinal).ToList()
                   ?? new List<AgentStep>();
        for (var i = 0; i < done.Count; i++)
            done[i].Ordinal = i;
        return done;
    }

    // Verify is failure-isolated: a crash/timeout degrades to ACCEPT so it never wedges
    // or fails an otherwise-successful run. EXCEPTION: a genuine run cancel (ct actually cancelled)
    // must PROPAGATE to the outer catch(OperationCanceledException) → SafeFail(cancelled) — not be
    // swallowed into an accept-then-Complete.
    private async Task<VerdictResult> SafeVerify(Guid runId, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
    {
        try
        {
            return await _verifier.VerifyAsync(ctx, persona, provider, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // genuine run cancellation — propagate
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Verify degraded to accept for run {RunId}", runId);
            return VerdictResult.Accept;
        }
    }

    /// <summary>
    /// E2: rebuilds the pre-pause half of a resumed run's context from the persisted plan. Seeds the Done
    /// steps' title/intent/declared artifact and marks them as an earlier segment — their visible result
    /// text is NOT recoverable here (it lives in the chat transcript, not on the step row), so the prompts
    /// say so instead of implying those steps never ran. Failure-isolated: a read fault
    /// leaves the run with the old partial picture rather than failing the resume.
    /// The filter is <c>== Done</c> and is DELIBERATELY narrower than <see cref="KeepDoneAsync"/>'s
    /// <c>Done or Skipped</c> — do not "align" the two. This list becomes
    /// <c>ctx.CompletedSteps</c>, i.e. the work the critic judges and whose declared artifacts it probes on
    /// disk; a user-skipped step never executed, so presenting it as completed would invite a verdict about
    /// an artifact nothing was ever asked to produce.
    /// </summary>
    private async Task SafeSeedResumeContext(Guid runId, RunContext ctx, CancellationToken ct)
    {
        try
        {
            var persisted = await _runService.GetAsync(runId, ct).ConfigureAwait(false);
            var done = persisted?.Plan
                .Where(s => s.Status == AgentStepStatus.Done)
                .OrderBy(s => s.Ordinal)
                // Null Outcome unless an artifact was persisted — a bare success declaration resumes unconfirmed.
                .Select(s => new CompletedStepSummary(
                    s.Ordinal, s.Title, s.Intent ?? string.Empty, Succeeded: true, VisibleText: string.Empty,
                    s.ExpectedArtifact, FromEarlierSegment: true,
                    Outcome: StepExtraJson.ArtifactRefOf(s) is { } artifact
                        ? new StepOutcomeClaim(true, string.Empty, artifact)
                        : null))
                .ToList();
            if (done is null || done.Count == 0)
                return;

            ctx.SeedCompletedSteps(done);
            _logger.LogInformation(
                "Resume seeded {Count} pre-pause step(s) ({WithArtifact} with a reported artifact) into the context of run {RunId}",
                done.Count, done.Count(d => d.Outcome is not null), runId);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Run bookkeeping (resume context seed) failed for {RunId}", runId); }
    }

    /// <summary>Whether a resume should re-enter planning instead of draining: true only when the park reason
    /// is <see cref="NeedsGoalReason"/> and the run has zero persisted step rows, in which case this also seeds
    /// the user's accumulated clarification answers into <paramref name="ctx"/>.</summary>
    private async Task<bool> TryEnterClarificationRePlanAsync(
        Guid runId, RunContext ctx, string? parkReason, CancellationToken ct)
    {
        // NeedsInputReason is deliberately not accepted here: that park has a half-executed plan, and
        // re-planning it is the hazard the resume guard above exists to avoid.
        if (parkReason != NeedsGoalReason)
            return false;

        try
        {
            var persisted = await _runService.GetAsync(runId, ct).ConfigureAwait(false);
            if (persisted is null)
                return false; // the row is gone underneath the dispatch — nothing to plan for

            // Every step status counts, not just Pending — a Done or Skipped row is exactly what must not be
            // overwritten.
            if (persisted.Plan.Count > 0)
            {
                // Reachable in principle: a needs-goal park should never carry step rows, since the decline
                // branch returns before SafeReplaceSteps.
                _logger.LogWarning(
                    "Run {RunId} resumed with reason {Reason} but has {Count} persisted step row(s) — NOT re-planning "
                    + "(re-planning a resume requires both the needs-goal reason and zero step rows)",
                    runId, parkReason, persisted.Plan.Count);
                return false;
            }

            var answers = RunClarifications.Read(persisted.ClarificationsJson);
            ctx.SetClarifications(answers);
            // Count only on the plain line; the answers themselves are user content, logged only via
            // SensitiveDebug below. Zero answers is legitimate — the run still re-plans rather than settling
            // Completed having done nothing.
            _logger.LogInformation(
                "Run {RunId} resumed with reason {Reason} and no step rows → RE-PLANNING (a deliberate exception "
                + "to the usual resume-must-not-re-plan rule) with {Count} recorded clarification answer(s)",
                runId, parkReason, answers.Count);
            _logger.SensitiveDebug("Run {RunId} re-plans with clarifications: {Answers}", runId, string.Join(" | ", answers));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run bookkeeping for the re-plan-on-resume check failed for {RunId} — falling back to no re-plan", runId);
            return false;
        }
    }

    // Run-level usage accrual (stepId: null) for the loop's non-step turns — plan, replan and verify
    // updates ledger totals + wall-clock and raises RunChanged. Step usage goes through
    // SafeRecordStep instead (per-step entry). Skips a null usage so a fake/no-usage provider path adds
    // no spurious ledger write.
    /// <summary>
    /// the grace turn, failure-isolated and time-boxed. Returns null when there was no turn to have —
    /// because the executor spends none (the interface default, which is what the live executor keeps), because
    /// the run is already cancelled, or because the attempt failed.
    /// Three properties, each of which a park depends on:
    /// <list type="bullet">
    /// <item>A THROW STILL PARKS. This is a courtesy round on a run that is stopping either way; letting a
    /// provider error escape here would turn a clean budget park into an unhandled fault in the drain loop.</item>
    /// <item>NOT ON AN ALREADY-CANCELLED TOKEN. At shutdown (or after a cascade cancel) <c>cts.Token</c> is
    /// already down, and spending a round on a provider that is about to be abandoned delays the park for
    /// nothing. Checked rather than left to the provider call's own cancellation, so the intent is visible.</item>
    /// <item>BOUNDED SEPARATELY, at <see cref="GraceTurnBudget"/>. A run's per-request timeout can be five
    /// minutes; a wrap-up nobody asked for must not hold a park open that long, and the run row staying
    /// <c>Running</c> in the meantime is what the bound really protects (a scheduled job's bookkeeping reads that
    /// row). The linked source cancels the turn only — the run's own token is untouched.</item>
    /// </list>
    /// </summary>
    private async Task<StepTurnResult?> SafeGraceTurn(
        IAgentTurnExecutor executor, AgentRun run, RunContext ctx, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return null;

        try
        {
            using var graceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            graceCts.CancelAfter(GraceTurnBudget);
            var result = await executor.RunGraceTurnAsync(run, ctx, graceCts.Token).ConfigureAwait(false);
            if (result is null)
                return null;

            // The log line reads the RESULT, not its null-ness, and that distinction is not pedantry: the
            // headless executor's exchange engine converts a cancellation (this method's own 90 s bound) and a
            // provider fault into a RETURNED failure result rather than a throw, so `result is not null` is
            // always true there. Logging "produced a wrap-up" off that would announce a wrap-up for a turn the
            // bound cut off — a false statement in the support log of the run it is describing.
            if (result.Succeeded && !string.IsNullOrWhiteSpace(result.VisibleText))
                _logger.LogInformation("Budget park: grace turn produced a wrap-up for {RunId}", run.Id);
            else
                _logger.LogInformation(
                    "Budget park: grace turn produced no wrap-up for {RunId} (succeeded={Succeeded}, cancelled={Cancelled})",
                    run.Id, result.Succeeded, result.Cancelled);

            // Returned either way: a failed turn still spent its tokens, and I1 says a paid round is accrued.
            return result;
        }
        catch (Exception ex)
        {
            // Includes the OperationCanceledException from the bound above. The park is the point; this was not.
            _logger.LogWarning(ex, "Budget-park grace turn failed for {RunId}; parking without a wrap-up", run.Id);
            return null;
        }
    }

    /// <summary>Bound on the T2-18 grace turn — see <see cref="SafeGraceTurn"/> for why it is not the run's.</summary>
    private static readonly TimeSpan GraceTurnBudget = TimeSpan.FromSeconds(90);

    private async Task SafeAddUsage(Guid runId, UsageDetails? usage, CancellationToken ct)
    {
        if (usage is null) return;
        try { await _runService.AddUsageAsync(runId, null, usage, ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Run bookkeeping (run-level usage) failed for {RunId}", runId); }
    }

    private static string BuildVerifyFailureReason(VerdictResult v)
    {
        var reason = string.IsNullOrWhiteSpace(v.Reason) ? "The run did not satisfy the goal." : v.Reason!;
        if (v.Missing.Count > 0)
            reason += " Missing: " + string.Join("; ", v.Missing);
        return reason; // fed into ReplanAsync's prompt — never logged (sensitive)
    }

    // ---- the fan-out (delegate a step group to sibling child runs and await them) ----

    /// <summary>How a fan-out settled, as the drain loop needs to see it. Never both parked and cancelled —
    /// the caller checks <paramref name="Cancelled"/> first, because a cascade cancel outranks a park.
    /// <paramref name="Abandoned"/> outranks everything: it means <c>TryEndChildWaitAsync</c> lost its CAS, so
    /// this run's row is owned by another writer and this loop must stop WITHOUT writing to it.
    /// <paramref name="PauseRequested"/> means the fan-out found a user pause standing at its ENTRY and
    /// dispatched nothing — the request is ALREADY CONSUMED (honoured exactly once), so the caller parks the
    /// parent without consuming again.
    /// </para></summary>
    private sealed record FanOutResult(
        bool AnyParked, bool AnyFailed, bool Cancelled, string? Error,
        bool Abandoned = false, bool PauseRequested = false);

    /// <summary>
    /// Dispatch <paramref name="step"/>'s whole parallel group as sibling CHILD runs, await every one of them,
    /// and settle each sibling step from the child's persisted row. Returns <c>null</c> when this step is NOT a
    /// fan-out, which is the overwhelmingly common case and means "run it in-process, exactly as before".
    /// Four independent reasons to decline, each one a degrade rather than a failure: no launcher was injected
    /// (⇒ delegation is off for the whole build), the step declares no <c>parallelGroup</c> (D11 — absence means
    /// sequential), this run is ITSELF a child ('s depth guard), or the group has fewer than two pending
    /// members (D11 — a group of one is not a fan-out; running it as a child would cost all of delegation and
    /// buy none of the parallelism).
    /// Every child interaction is failure-isolated. A launcher fault while dispatching sibling 3 of 4 marks
    /// sibling 3 failed and still awaits siblings 1–2: a dispatched child is never left unawaited, because that
    /// is precisely the orphan D16 exists to rule out.
    /// </summary>
    private async Task<FanOutResult?> TryFanOutAsync(
        AgentRun run, AgentStep step, RunContext ctx, RunProfile profile, CancellationTokenSource cts)
    {
        if (_childLauncher is null)
            return null;

        if (ParallelGroupOf(step) is not { } group)
            return null;

        // Depth guard, one line and structural: a run that is itself a child NEVER delegates. It bounds
        // the wall clock, the child-pool pressure and the scheduled run slot the fan-out holds to a single
        // level — a plan shaped like a tree would otherwise multiply R15 by its depth.
        if (run.ParentRunId is not null)
            return null;

        var siblings = await SafeSiblingGroupAsync(run.Id, group, cts.Token).ConfigureAwait(false);
        if (siblings.Count < 2)
        {
            // SafeCancelStaleChildrenAsync (inside FanOutCoreAsync) is the ONLY cleanup for a
            // previous child generation, and this early return used to skip straight past it — so a group that
            // has dropped below two PENDING members since it was last dispatched orphaned the whole generation
            // behind it. A Paused child is never swept (the startup reconcile's statement 1 is
            // `State < WaitingForInput`), so the orphan is PERMANENT, not one a restart clears: it keeps a
            // visible stub chat and a non-terminal row forever.
            //
            // Two triggers, one defect, and the second is user-driven rather than a race: (a) a MIXED
            // generation — one child Done, one Paused — leaves exactly one Pending member for the resumed
            // parent; (b) the user pauses a 2-way fan-out, both children park, both sibling steps go back to
            // Pending, and the user then clicks "Skip step" on one of them before Continue.
            //
            // Cleaning up HERE rather than hoisting the call above SafeSiblingGroupAsync keeps the F2 handshake
            // exactly as it is (the fan-out mark still brackets the whole committed path and nothing else), and
            // the two branches remain mutually exclusive: the >= 2 branch still cleans up inside
            // FanOutCoreAsync, so the supersede happens exactly once either way. It is idempotent (terminal
            // children are skipped) and failure-isolated, so the price on the common decline path — a group of
            // one on a run that never delegated — is a single GetChildRunsAsync that finds nothing.
            await SafeCancelStaleChildrenAsync(_childLauncher, run.Id, cts.Token).ConfigureAwait(false);
            return null;
        }

        // ---- from here on this run IS fanning out, and the row does not say so ----
        //
        // WaitingForChildren is not persisted until SafeBeginChildWait, AFTER the whole launch loop below, so
        // for the entire prologue the row reads Running and AgentRunSteeringService.PauseAsync used to fire the
        // parent's own token — which the cancelled-token check further down reads as a genuine cancel, settling
        // the run TERMINALLY with CompletedAt stamped and every child's finished work discarded. Publishing the
        // mark here, before the first side effect, is what tells the pause command to CASCADE instead.
        //
        // Cleared in a finally that covers every exit including a faulted one: a leaked mark would make every
        // later pause of this run cascade to children it no longer has, i.e. never interrupt anything.
        _steering?.BeginFanOut(run.Id);
        try
        {
            // The other half of the handshake, and the reason the window closes by CONSTRUCTION rather than by
            // being narrow: set the flag, THEN read the request. The pause command records its request and THEN
            // reads the flag. Whichever ran first, the other one sees it — so a pause can neither be outrun
            // into a fired parent token (it sees the flag and cascades) nor be started around (we see the
            // request and park here without dispatching anything at all).
            if (_steering?.TryConsumePauseRequest(run.Id) == true)
            {
                _logger.LogInformation(
                    "Run {RunId} was paused as its fan-out began; nothing dispatched", run.Id);
                return new FanOutResult(
                    AnyParked: false, AnyFailed: false, Cancelled: false, null, PauseRequested: true);
            }

            return await FanOutCoreAsync(_childLauncher, run, group, siblings, ctx, profile, cts)
                .ConfigureAwait(false);
        }
        finally
        {
            _steering?.EndFanOut(run.Id);
        }
    }

    /// <summary>
    /// The fan-out proper: supersede the previous generation, dispatch every sibling as a child run, await them
    /// all and settle each sibling step from the child's persisted row. Split out of
    /// <see cref="TryFanOutAsync"/> only so the F2 fan-out mark can bracket it in a <c>finally</c>
    /// without re-indenting the body; the four decline tests and that bracket are the caller's whole job.
    /// </summary>
    private async Task<FanOutResult> FanOutCoreAsync(
        IHeadlessRunLauncher childLauncher, AgentRun run, int group, List<AgentStep> siblings,
        RunContext ctx, RunProfile profile, CancellationTokenSource cts)
    {
        // D13's park leaves a child at WaitingForInput with its step still Pending, so a RESUMED parent arrives
        // here again with the same pending group. Nothing links a child to a STEP (only ParentRunId → parent),
        // so the parent cannot tell this group already has a parked generation behind it — and a parked run is
        // never swept, so it would sit there forever owning a visible stub chat. Cancel the old generation first.
        await SafeCancelStaleChildrenAsync(childLauncher, run.Id, cts.Token).ConfigureAwait(false);

        var childProfile = ChildProfile(profile);
        var dispatched = new List<(AgentStep Step, HeadlessRunHandle Handle)>();
        var anyFailed = false;
        var anyParked = false;
        string? error = null;

        _logger.LogInformation(
            "Run {RunId} delegating parallel group {Group} to {ChildCount} child run(s)", run.Id, group, siblings.Count);

        foreach (var sibling in siblings)
        {
            try
            {
                var handle = await childLauncher.LaunchChildAsync(
                    new HeadlessRunRequest(
                        BuildChildGoal(sibling),
                        run.TriggerKind,
                        // A child is not a scheduled job of its own: TriggerRef stays null so nothing treats it
                        // as a second run of the parent's job. GrantedWrites likewise — the child's grants come
                        // from the parent's envelope, narrowed, and a request-level set would widen them.
                        TriggerRef: null,
                        OwnerDeviceId: run.OwnerDeviceId,
                        ProviderId: null,
                        GrantedWrites: null,
                        Budget: childProfile),
                    parentRunId: run.Id,
                    parentPolicyJson: run.PolicyJson,
                    // 06 G1's RunContext member: the child runs INSIDE the parent's workspace and provisions
                    // nothing. Null ⇒ the parent runs unisolated and so does the child.
                    parentWorkspaceRoot: ctx.WorkspaceRoot,
                    // The specialist the PLAN chose for this step becomes the child's run persona —
                    // its system prompt, its provider and its reasoning effort. Dropping it here (a hard
                    // ProviderId: null and nothing else) made a fan-out behave exactly as if no roster were
                    // configured, while G7's panel still drew that specialist's avatar and accent ring on the
                    // step. The launcher treats it as a request and degrades to the per-mode persona.
                    personaId: sibling.AssignedPersonaId,
                    cts.Token).ConfigureAwait(false);

                dispatched.Add((sibling, handle));
                await SafeSetStepStatus(sibling.Id, AgentStepStatus.Running, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Not the critical path: one sibling that could not be dispatched is a failed step, and the
                // siblings that WERE dispatched must still be awaited below.
                _logger.LogWarning(ex, "Fan-out: dispatching a child run of {RunId} failed", run.Id);
                anyFailed = true;
                error ??= "a delegated run could not be started";
                await SettleSiblingAsync(sibling, ctx, FanOutStepResult(false, error), cts.Token).ConfigureAwait(false);
            }
        }

        // 07 G8/D9. Park the parent in the PERSISTED WaitingForChildren state for the whole wait, which is what
        // makes a restart mid-fan-out recoverable: the state sits ABOVE the startup sweep's `State < 3`
        // threshold, so FailInterruptedRunsAsync cancels the children and RE-PARKS the parent as
        // WaitingForInput instead of cancelling it out from under their finished work. Parking also closes the
        // ledger work segment — the parent is not working, its children are, and each bills its own time.
        //
        // Only when something was actually dispatched: with dispatched.Count == 0 every sibling failed to
        // launch, there is nothing to wait for, and parking would leave a state no CAS below could clear.
        // `parked` (not `dispatched.Count > 0`) gates the un-park, so a swallowed park fault cannot make the
        // CAS below read as "someone else took this run".
        var parked = dispatched.Count > 0
            && await SafeBeginChildWait(run.Id, dispatched.Count, cts.Token).ConfigureAwait(false);

        var registration = default(CancellationTokenRegistration);
        try
        {
            // D16, the no-orphans guarantee. Cancellation is delivered TO the children and the parent then keeps
            // waiting for their dispatch tasks to unwind. Deliberately NO `.WaitAsync(cts.Token)` on the
            // WhenAll: that returns to the caller while the children keep running, so a settled parent would
            // have live children still writing its workspace — after 06, a concurrent writer into the run root.
            registration = cts.Token.Register(() =>
            {
                foreach (var d in dispatched)
                {
                    // Revocation 5: the parent's token fired for a TERMINAL reason (a chat
                    // delete, app shutdown, Stop), so a pause request standing against a child must not turn
                    // this cascade into a park. Revoke BEFORE cancelling, always in that order — the child's
                    // loop reads the request when its step unwinds, so a cancel delivered first could be
                    // consumed as a pause on the way past.
                    _steering?.RevokePauseRequest(d.Handle.RunId);
                    childLauncher.CancelAsync(d.Handle.RunId).SafeFireAndForget(_logger);
                }
            });

            await Task.WhenAll(dispatched.Select(d => d.Handle.Completion)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Defence in depth: every dispatch task self-catches and settles its own run, so a faulted
            // completion must not skip the per-child settle below — which reads the persisted ROWS, not tasks.
            _logger.LogWarning(ex, "Fan-out: awaiting the child runs of {RunId} faulted", run.Id);
        }
        finally
        {
            registration.Dispose();
        }

        foreach (var (sibling, handle) in dispatched)
        {
            var child = await SafeGetRunAsync(handle.RunId, cts.Token).ConfigureAwait(false);
            switch (child?.State)
            {
                case AgentRunState.Completed:
                    await SettleSiblingAsync(sibling, ctx,
                        FanOutStepResult(true, await SafeChildAnswerAsync(handle.ChatId, cts.Token).ConfigureAwait(false)),
                        cts.Token).ConfigureAwait(false);
                    await RollUpChildUsageAsync(run.Id, child, cts.Token).ConfigureAwait(false);
                    break;

                case AgentRunState.Failed:
                case AgentRunState.Cancelled:
                    anyFailed = true;
                    error ??= child.State == AgentRunState.Cancelled
                        ? "a delegated run was cancelled"
                        : "a delegated run failed";
                    await SettleSiblingAsync(sibling, ctx, FanOutStepResult(false, error), cts.Token).ConfigureAwait(false);
                    await RollUpChildUsageAsync(run.Id, child, cts.Token).ConfigureAwait(false);
                    break;

                // D6 widened this arm to an EXPLICIT two-member set, never a range and never a
                // null-tolerant pattern: a null child (a SafeGetRunAsync fault) must stay in `default:`, where a
                // run whose state cannot be read is charged as a failure rather than silently treated as parked.
                // Paused(4) is a CASCADE-paused child — the parent's own pause reached it — and it is parked in
                // exactly the sense this arm means. Without it every cascade-paused child is recorded a FAILED
                // sibling (SafeRecordStep AND ctx.RecordStep, so the critic and any replan see failed work, with
                // "child run did not settle" as the recorded outcome) and its tokens are dropped as well: the
                // user presses Pause and the parent replans around durable, resumable work.
                //
                // Same two-member set as AgentRunStates.IsParked — kept as a literal case pattern here because
                // a switch case label cannot invoke a method.
                case AgentRunState.WaitingForInput or AgentRunState.Paused:
                    // /D13: HeadlessRunHandle.Completion settles on a budget PAUSE too, which is not
                    // terminality. Roll up NOTHING — the child will resume and its tokens are pushed once, from
                    // a terminal branch, which is what stops a resumed child being billed to its parent twice.
                    //
                    // And put the step back to PENDING. It was set Running at dispatch (the panel highlights the
                    // delegated steps while their children work), and the resume drain is
                    // NextPendingStepAsync — a step left Running is invisible to it, so this generation's work
                    // would be silently dropped from the resumed run with the plan still showing it as active.
                    await SafeSetStepStatus(sibling.Id, AgentStepStatus.Pending, cts.Token).ConfigureAwait(false);
                    anyParked = true;
                    break;

                default:
                    anyFailed = true;
                    error ??= "child run did not settle";
                    await SettleSiblingAsync(sibling, ctx, FanOutStepResult(false, error), cts.Token).ConfigureAwait(false);
                    break;
            }
        }

        // Checked BEFORE the un-park CAS: if the parent's own token fired while it waited (a chat delete, app
        // shutdown, or ChatSession.Cancel), this loop still owns the run and must settle it Cancelled
        // itself — exactly like an in-process step that came back Cancelled. Ending the wait first would
        // briefly advertise the run as Running on its way to Cancelled for no gain.
        if (cts.IsCancellationRequested)
            return new FanOutResult(AnyParked: false, AnyFailed: false, Cancelled: true, "cancelled while awaiting delegated runs");

        // 07 G8/D9. Leave WaitingForChildren through a CAS, not a blind write. Every arm the caller can take
        // from here writes this run's row — the parked arm calls PauseAsync, the failed arm may Fail it, the
        // clean arm sets Running per step — so this is the single place to establish that the row is still
        // OURS. A false means it is not: cascade-cancelled to Cancelled, or re-parked WaitingForInput by
        // another process's startup reconcile. Continuing would resurrect a run somebody else settled.
        if (parked && !await SafeTryEndChildWait(run.Id, cts.Token).ConfigureAwait(false))
            return new FanOutResult(AnyParked: false, AnyFailed: false, Cancelled: false, null, Abandoned: true);

        return new FanOutResult(anyParked, anyFailed, Cancelled: false, error);
    }

    /// <summary>
    /// The step result a settled sibling contributes. The message ids are deliberately <see cref="Guid.Empty"/>:
    /// a child's transcript lives in the child's OWN chat, so folding its ids into the parent's run-level
    /// message range would pin a slice of a transcript the parent never wrote.
    /// </summary>
    private static StepTurnResult FanOutStepResult(bool succeeded, string text) =>
        new(succeeded, Cancelled: false, succeeded ? null : text, succeeded ? text : string.Empty,
            Usage: null, Guid.Empty, Guid.Empty);

    /// <summary>
    /// Persist a settled sibling's outcome and fold it into the run context. <c>ctx.RecordStep</c> is what makes
    /// the sibling visible to a later replan and to the critic — skip it and a replan re-plans work that already
    /// ran. It also increments <c>StepsExecuted</c> once per sibling, which is correct: they ARE the run's steps.
    /// The children's own internal steps count against their OWN budgets; nesting the enforced budget
    /// would make a fan-out unpredictably fatal to the parent. That is the half of D15 that does NOT nest — the
    /// ephemeral per-dispatch budget, as against the persisted ledger, which does (see
    /// <see cref="RollUpChildUsageAsync"/>) — and T-FAN-16 pins it from both sides: one extra unit per child
    /// parks the parent at its cap, and dropping this call stops its own steps counting at all.
    /// </summary>
    private async Task SettleSiblingAsync(AgentStep sibling, RunContext ctx, StepTurnResult result, CancellationToken ct)
    {
        await SafeRecordStep(sibling.Id, result, ct).ConfigureAwait(false);
        ctx.RecordStep(sibling, result);
    }

    /// <summary>
    /// A child run's GOAL, from the sibling step the parent is delegating. Mirrors
    /// <c>HeadlessTurnExecutor.BuildInstruction</c>'s shape minus its "Execute step N" framing — the child is
    /// planning its own decomposition of this work, not executing one step of the parent's plan.
    /// SENSITIVE (user/model content): it is never logged, only handed to the launcher.
    /// </summary>
    private static string BuildChildGoal(AgentStep step)
    {
        var goal = string.IsNullOrWhiteSpace(step.Intent) ? step.Title : step.Intent!;
        return string.IsNullOrEmpty(step.ExpectedArtifact) ? goal : $"{goal} Expected: {step.ExpectedArtifact}";
    }

    /// <summary>
    /// A child's budget envelope: the parent's own, with the WALL CLOCK HALVED (clamped at
    /// <see cref="RunProfile.MinWallClockMinutes"/>). R15 is the reason — a fan-out occupies one of
    /// <c>HeadlessRunLauncher</c>'s two run slots for the parent's wall clock PLUS every descendant's, so with
    /// two of them alive a third scheduled agent run waits. Halving keeps a fan-out roughly inside the envelope
    /// one scheduled job already occupies.
    /// The stronger version of this argument is gone : the scheduler used to hold a single run lock
    /// from before the launch across <c>await handle.Completion</c>, so while a fan-out ran NO scheduled job of
    /// either kind could dispatch at all. It now dispatches without awaiting the run, which makes the launcher's
    /// slots the real bound — and leaves the halving justified by the slot it still holds.
    /// Derived from the PARENT's profile rather than re-read from settings (which suggested): the parent's
    /// profile already IS <c>RunProfile.FromBudget(settings.Scheduled*)</c> on the launch path, so this yields
    /// the same numbers without giving the orchestrator a settings dependency — and it also honours an explicit
    /// per-request budget, which a settings read would silently discard.
    /// </summary>
    private static RunProfile ChildProfile(RunProfile parent) => parent with
    {
        WallClock = TimeSpan.FromMinutes(
            Math.Max(RunProfile.MinWallClockMinutes, parent.WallClock.TotalMinutes / 2)),
    };

    /// <summary>
    /// The <c>{"parallelGroup":N}</c> marker the planner writes into <c>AgentStep.ExtraJson</c> when a plan
    /// declares steps independent AND a persona roster is configured. Swallowing by design: ANY parse
    /// failure, a missing member or a non-integer value means <c>null</c> ⇒ sequential, i.e. today's behaviour.
    /// Precedent: <c>RunProgressViewModel.ReadTruncation</c>. <c>internal</c> so the reader's degrade rows can
    /// be pinned directly rather than only through a whole run.
    /// </summary>
    internal static int? ParallelGroupOf(AgentStep step)
    {
        if (string.IsNullOrWhiteSpace(step.ExtraJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(step.ExtraJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("parallelGroup", out var value)
                   && value.TryGetInt32(out var group)
                ? group
                : null;
        }
        catch (Exception)
        {
            return null; // an unreadable marker is not a fan-out, and never an error
        }
    }

    /// <summary>
    /// The PENDING steps of this run that share <paramref name="group"/>, in ordinal order — re-read from the
    /// persisted plan rather than taken from the in-memory run, because a replan may have rewritten it. A read
    /// fault yields an empty list, which the caller reads as "fewer than two members" ⇒ run the step in-process.
    /// </summary>
    private async Task<List<AgentStep>> SafeSiblingGroupAsync(Guid runId, int group, CancellationToken ct)
    {
        try
        {
            var persisted = await _runService.GetAsync(runId, ct).ConfigureAwait(false);
            return persisted?.Plan
                       .Where(s => s.Status == AgentStepStatus.Pending && ParallelGroupOf(s) == group)
                       .OrderBy(s => s.Ordinal)
                       .ToList()
                   ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fan-out: reading the sibling group of run {RunId} failed", runId);
            return [];
        }
    }

    /// <summary>
    /// Cancel every non-terminal child of a previous generation of this fan-out.
    /// <see cref="IHeadlessRunLauncher.CancelAsync"/> only reaches a run THIS PROCESS is dispatching, so a child
    /// parked in a previous process is settled directly instead — without that fallback the leak survives
    /// exactly the restart path it exists to handle. Failure-isolated: a stale generation that cannot be
    /// cancelled must not stop the new one from dispatching.
    /// </summary>
    private async Task SafeCancelStaleChildrenAsync(IHeadlessRunLauncher launcher, Guid parentRunId, CancellationToken ct)
    {
        try
        {
            var children = await _runService.GetChildRunsAsync(parentRunId, ct).ConfigureAwait(false);
            var superseded = 0;
            foreach (var old in children)
            {
                if (old.State is AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Cancelled)
                    continue;

                // Revocation 4: a superseded generation settles TERMINAL. Revoke before
                // cancelling, so a pause the user asked for on a child of the PREVIOUS generation cannot make
                // that child park instead — it would then be neither superseded nor re-dispatched.
                _steering?.RevokePauseRequest(old.Id);

                await launcher.CancelAsync(old.Id).ConfigureAwait(false);

                // A parked child is the one shape the cancel above cannot reach across a restart: states at or
                // above WaitingForInput are never swept, on purpose (a parked run must survive a restart), so
                // this settle is the only thing that stops it lingering with its own stub chat forever.
                //
                // Paused(4) joins the set, EXPLICITLY and never as a range. A cascade-paused child
                // presents the identical shape — its dispatch has already returned, so it is not in _inflight
                // and the CancelAsync above is a no-op against it — and it is reached the same way a restart
                // reaches a budget-parked one. Without it every cascade-paused child of a re-dispatched fan-out
                // leaks forever with its own visible stub chat, which is precisely what this settle exists to
                // prevent.
                if (AgentRunStates.IsParked(old.State))
                    await _runService.FailAsync(old.Id, SupersededFailureReason, cancelled: true,
                        CancellationToken.None).ConfigureAwait(false);

                superseded++;
            }

            if (superseded > 0)
                _logger.LogInformation(
                    "Fan-out: superseded {Count} child run(s) of a previous generation of run {RunId}", superseded, parentRunId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fan-out: cancelling the previous child generation of run {RunId} failed", parentRunId);
        }
    }

    /// <summary>
    /// The child's answer, as the parent's replan and verify prompts need to see it: the LAST non-empty
    /// assistant message of the child's own chat, capped. Without it a completed sibling carries empty visible
    /// text and the critic judges the goal on nothing. Failure-isolated, and the text is NEVER logged — it is
    /// model output about user content.
    /// </summary>
    private async Task<string> SafeChildAnswerAsync(Guid chatId, CancellationToken ct)
    {
        if (_chats is null)
            return DelegatedAnswerUnavailable;

        try
        {
            var chat = await _chats.GetAsync(chatId, ct).ConfigureAwait(false);
            var answer = chat?.Messages
                .LastOrDefault(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                                    && !string.IsNullOrWhiteSpace(m.Content))
                ?.Content;

            if (string.IsNullOrWhiteSpace(answer))
                return DelegatedAnswerUnavailable;

            return answer!.Length > MaxChildAnswerChars
                ? answer[..MaxChildAnswerChars] + "\n… (truncated)"
                : answer;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fan-out: reading a delegated run's answer failed");
            return DelegatedAnswerUnavailable;
        }
    }

    /// <summary>
    /// D15 — the roll-up. The PERSISTED ledger nests: a settled child's token totals are pushed into its
    /// parent's ledger ONCE, through the existing run-level accrual seam (<c>stepId: null</c>, the same one the
    /// plan/replan/verify turns use), from a terminal branch only. Deliberately NOT a fourth
    /// <c>IAgentRunService</c> member: <c>AddUsageAsync</c> already refreshes the clock and raises
    /// <c>RunChanged</c>, and one more method on a 17-member interface would buy no new capability.
    /// TOKENS ONLY, never time. The parent's <c>WallClockMs</c> stays its own worked time and the children's is
    /// visible on the children, in the drill-down. And <c>stepId</c> stays null rather than the sibling's id
    /// because the parent ran NO turn for that step — a per-step entry would claim it spent tokens it never did.
    /// Idempotence is the CALLER's: a parent awaits each child exactly once and pushes only from a terminal
    /// branch. Stated loss: a crash between a child's settle and this push loses that roll-up. The child's own
    /// ledger still holds the truth — the parent's number is an aggregate convenience, not an accounting record.
    /// </summary>
    private async Task RollUpChildUsageAsync(Guid parentRunId, AgentRun child, CancellationToken ct)
    {
        UsageDetails? usage = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(child.LedgerJson)
                && JsonSerializer.Deserialize<ChildLedger>(child.LedgerJson, LedgerJsonOptions) is { } ledger
                && (ledger.InputTokens > 0 || ledger.OutputTokens > 0))
            {
                usage = new UsageDetails
                {
                    InputTokenCount = ledger.InputTokens,
                    OutputTokenCount = ledger.OutputTokens,
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fan-out: reading the ledger of child run {RunId} failed", child.Id);
        }

        if (usage is null)
            return;

        await SafeAddUsage(parentRunId, usage, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Run {RunId} rolled up child run {ChildRunId} (in={In}, out={Out})",
            parentRunId, child.Id, usage.InputTokenCount ?? 0, usage.OutputTokenCount ?? 0);
    }

    /// <summary>
    /// Mirrors <c>AgentRunService</c>'s PRIVATE <c>Ledger</c> DTO (camelCase JSON), tokens only — the same
    /// mirror <c>RunProgressViewModel</c> already carries, for the same reason: the DTO is private to the
    /// service and the interface exposes no reader. Two mirrors is the accepted cost; do not add a third.
    /// </summary>
    private sealed class ChildLedger
    {
        public long InputTokens { get; set; }

        public long OutputTokens { get; set; }
    }

    private async Task<AgentRun?> SafeGetRunAsync(Guid runId, CancellationToken ct)
    {
        try { return await _runService.GetAsync(runId, ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Fan-out: reading child run {RunId} failed", runId); return null; }
    }

    // ---- Failure-isolated bookkeeping: never fail the run ----

    private async Task SafeSetState(Guid runId, AgentRunState state, CancellationToken ct)
    {
        try { await _runService.SetStateAsync(runId, state, ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Run bookkeeping (state {State}) failed for {RunId}", state, runId); }
    }

    private async Task SafeSetStepStatus(Guid stepId, AgentStepStatus status, CancellationToken ct)
    {
        try { await _runService.SetStepStatusAsync(stepId, status, ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Run bookkeeping (step status {Status}) failed for {StepId}", status, stepId); }
    }

    private async Task SafeRecordStep(Guid stepId, StepTurnResult r, CancellationToken ct)
    {
        try
        {
            await _runService.RecordStepResultAsync(stepId,
                r.Succeeded ? AgentStepStatus.Done : AgentStepStatus.Failed,
                r.FirstMessageId, r.LastMessageId, r.Usage, ct, r.Outcome?.ArtifactRef).ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Run bookkeeping (record step) failed for {StepId}", stepId); }
    }

    private async Task SafeReplaceSteps(Guid runId, IReadOnlyList<AgentStep> steps, CancellationToken ct)
    {
        try { await _runService.ReplaceStepsAsync(runId, steps, ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Run bookkeeping (replace steps) failed for {RunId}", runId); }
    }

    private async Task SafeRange(Guid runId, Guid first, Guid last, CancellationToken ct)
    {
        try { await _runService.SetRunMessageRangeAsync(runId, first, last, ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Run bookkeeping (range) failed for {RunId}", runId); }
    }

    private async Task SafeComplete(Guid runId, CancellationToken ct, bool truncated = false, string? reason = null)
    {
        try { await _runService.CompleteAsync(runId, truncated, reason, ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Run bookkeeping (complete) failed for {RunId}", runId); }
    }

    // Budget pause: park the run WaitingForInput (non-terminal). Failure-isolated — a
    // pause bookkeeping error must never corrupt or wedge the run.
    private async Task SafePause(Guid runId, CancellationToken ct, string reason)
    {
        try { await _runService.PauseAsync(runId, reason, ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Run bookkeeping (pause) failed for {RunId}", runId); }
    }

    /// <summary>Mints one message id and passes it to both the durable post and the live mirror below (in that
    /// order), since a live session's pull keys on the id to decide which stored rows it's missing — two ids for
    /// one question would render it twice.</summary>
    private async Task PostAndMirrorClarificationQuestionAsync(
        IAgentTurnExecutor executor, AgentRun run, RunContext ctx, Persona persona, string? question)
    {
        var questionId = Guid.NewGuid();
        await SafePostClarificationQuestionAsync(run, persona, questionId, question).ConfigureAwait(false);
        await SafeMirrorClarificationQuestion(executor, run, ctx, persona, questionId, question).ConfigureAwait(false);
    }

    /// <summary>Posts the clarification question into the run's own chat as an assistant message, using
    /// <see cref="IAssistantChatService.SaveMergedAsync"/> rather than a blind replace so a concurrent writer's
    /// rows are never dropped. A blank <paramref name="question"/> is a no-op, not a fabricated placeholder.</summary>
    /// <param name="messageId">Must match the id the caller's live mirror uses for the same question.</param>
    private async Task SafePostClarificationQuestionAsync(
        AgentRun run, Persona persona, Guid messageId, string? question)
    {
        if (_chats is null || string.IsNullOrWhiteSpace(question))
            return; // no chat service wired, or a decline that worded no question

        try
        {
            var chat = await _chats.GetAsync(run.ChatId, CancellationToken.None).ConfigureAwait(false);
            if (chat is null)
                return; // the stub row is gone (e.g. its chat was deleted underneath the run) — nothing to post into

            var now = DateTime.UtcNow;
            var snapshot = new SyncAssistantChat
            {
                Id = chat.Id,
                SchemaVersion = chat.SchemaVersion,
                Title = chat.Title,
                CreatedAt = chat.CreatedAt,
                UpdatedAt = now,
                LastAccessedAt = now,
                WindowMode = chat.WindowMode,
                ProviderId = chat.ProviderId,
                WorkingDirectory = chat.WorkingDirectory,
                Messages =
                [
                    new SyncAssistantChatMessage
                    {
                        Id = messageId,
                        Role = "assistant",
                        Content = question,
                        Timestamp = now,
                        Persona = new SyncMessagePersona { Id = persona.Id, Name = persona.Name, Emoji = persona.Emoji },
                    },
                ],
            };
            await _chats.SaveMergedAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation(
                "Run {RunId}: posted the clarification question into chat {ChatId}", run.Id, run.ChatId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run {RunId}: posting the clarification question into chat {ChatId} failed",
                run.Id, run.ChatId);
        }
    }

    /// <summary>
    /// Park the run WaitingForInput for a HUMAN TOOL DECISION, naming the tool in the envelope.
    /// The same non-terminal park the budget cap uses (same state, same Continue card, same resume claim) —
    /// only the reason token and the extra <c>tool</c> member differ, which is the whole point of reusing it.
    /// <c>CancellationToken.None</c>, unlike <see cref="SafePause"/>'s budget call: the step that parked may
    /// well have left <c>cts.Token</c> cancelled behind it, and a park that does not reach the row leaves the
    /// run dangling <c>Running</c> — unresumable, with the human's question never asked.
    /// </summary>
    private async Task SafeRequestApproval(Guid runId, string toolName)
    {
        try
        {
            await _runService.PauseAsync(runId, ToolApprovalReason, CancellationToken.None, approvalTool: toolName)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run bookkeeping (approval park) failed for {RunId}", runId);
        }
    }

    /// <summary>
    /// park the run at <see cref="AgentRunState.Paused"/> for a USER pause, through the CAS and
    /// never a blind write — a blind write would resurrect a run another writer already settled, which is
    /// the one way a pause could turn a Cancelled run back into a live one.
    /// <c>CancellationToken.None</c>: this runs on an already-cancelled token by construction (the pause fired
    /// it), and a pause that does not reach the row leaves the run dangling <c>Running</c> — unresumable.
    /// Failure-isolated like its neighbours, and it REPORTS the CAS result rather than swallowing it: a lost CAS
    /// is a normal outcome worth a log line, not a silent success.
    /// </summary>
    /// <summary>
    /// give every UNREPAIRED <c>Failed</c> step back to the plan when a user pause parks the run.
    /// A <c>Failed</c> row is only ever a transient: <c>TryReplanAfterFailureAsync</c> either writes a revised
    /// plan (and <c>KeepDoneAsync</c> drops the failed row on the way through) or exhausts its budget and
    /// settles the run terminally. A pause that lands DURING that replan — <c>_planner.ReplanAsync</c> is
    /// awaited on <c>cts.Token</c> with no local catch, so the OCE leaves through the outer arm — parks the
    /// run correctly and resumably, but leaves the failed step behind with nothing owed to it:
    /// <c>replans</c> is a <c>RunAsync</c> local so the resumed dispatch has no memory a replan was due,
    /// <c>NextPendingStepAsync</c> filters <c>Pending</c> so the step never re-runs, and
    /// <c>SafeSeedResumeContext</c> filters <c>== Done</c> so the critic is never told it failed. The resumed
    /// run drains the remainder and reports <b>Completed</b> over work that failed and was never repaired.
    /// Pre-Batch-08 that interleaving settled <c>Cancelled</c>, so the silent-success shape is new.
    /// Restoring the row to <c>Pending</c> is the cheap half of the review's fix and it is the half that
    /// changes the outcome: the resumed run RE-ATTEMPTS the step instead of reporting success over it, and if
    /// it fails again the replan budget of the new dispatch repairs it exactly as it would have. The expensive
    /// half — persisting the owed replan in the pause envelope and re-seeding <c>replans</c>/<c>ctx</c> from it
    /// buys a more faithful budget accounting and is not attempted here.
    /// Called only after the CAS has WON, so a run another writer owns is never touched, and it covers all
    /// three park sites at once (the in-loop consume, the fan-out boundary, the throwing-abort arm) — the last
    /// of which is also where a fan-out generation's <c>Failed</c> sibling steps would otherwise be stranded
    /// unretried. Failure-isolated per step, like every other bookkeeping write on this path.
    /// </summary>
    private async Task RestoreUnrepairedFailedStepsAsync(Guid runId)
    {
        AgentRun? current;
        try
        {
            current = await _runService.GetAsync(runId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run bookkeeping (re-reading the plan of paused run {RunId}) failed", runId);
            return;
        }

        if (current is null)
            return;

        var restored = 0;
        foreach (var step in current.Plan.Where(s => s.Status == AgentStepStatus.Failed))
        {
            await SafeSetStepStatus(step.Id, AgentStepStatus.Pending, CancellationToken.None).ConfigureAwait(false);
            restored++;
        }

        if (restored > 0)
            _logger.LogInformation(
                "Run {RunId} paused holding {Count} unrepaired failed step(s); they were returned to Pending so the resume re-attempts them",
                runId, restored);
    }

    /// <summary>the titles of this run's <c>Skipped</c> steps, or an empty list on any fault —
    /// the replan prompt's "do not re-add these" block is an improvement to the prompt, never a reason to
    /// fail a run.</summary>
    private async Task<IReadOnlyList<string>> SafeSkippedTitlesAsync(Guid runId, CancellationToken ct)
    {
        try
        {
            var current = await _runService.GetAsync(runId, ct).ConfigureAwait(false);
            return current is null
                ? []
                : current.Plan.Where(s => s.Status == AgentStepStatus.Skipped)
                    .OrderBy(s => s.Ordinal).Select(s => s.Title).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run bookkeeping (reading the skipped steps of {RunId}) failed", runId);
            return [];
        }
    }

    private async Task<bool> SafePauseUser(Guid runId)
    {
        try
        {
            if (await _runService.TryPauseUserAsync(runId, CancellationToken.None).ConfigureAwait(false))
            {
                await RestoreUnrepairedFailedStepsAsync(runId).ConfigureAwait(false); // Batch 08 F9
                return true;
            }

            _logger.LogInformation(
                "Run {RunId} user pause was not applied — another writer owns this run; releasing the executor only", runId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run bookkeeping (user pause) failed for {RunId}", runId);
            return false;
        }
    }

    /// <summary>
    /// Park the parent at <see cref="AgentRunState.WaitingForChildren"/> for the span of a fan-out.
    /// Returns whether the park actually happened: unlike the other <c>Safe*</c> wrappers this one reports its
    /// own failure, because the un-park CAS is only meaningful if the park landed — a swallowed fault here
    /// followed by a CAS would read as "another writer owns this run" and abandon a perfectly healthy run.
    /// </summary>
    private async Task<bool> SafeBeginChildWait(Guid runId, int childCount, CancellationToken ct)
    {
        try
        {
            await _runService.BeginChildWaitAsync(runId, childCount, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            // Degrade, never fail: the wait still happens in-process on the awaits below, exactly as it did
            // before this state existed. Only the RESTART guarantee is lost for this one run.
            _logger.LogWarning(ex, "Run bookkeeping (child-wait park) failed for {RunId}", runId);
            return false;
        }
    }

    /// <summary>
    /// End the child wait via the CAS. A fault is reported as <c>true</c> — "assume the run is still
    /// ours" — deliberately: the caller's false arm ABANDONS the run without settling it, and doing that on a
    /// transient read error would leave a live run permanently un-terminated with no loop and no user
    /// affordance. A genuine lost CAS returns false through the normal path, not through this catch.
    /// </summary>
    private async Task<bool> SafeTryEndChildWait(Guid runId, CancellationToken ct)
    {
        try { return await _runService.TryEndChildWaitAsync(runId, ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run bookkeeping (child-wait CAS) failed for {RunId}", runId);
            return true;
        }
    }

    private async Task SafeFail(Guid runId, string? error, bool cancelled)
    {
        // Terminal fail writes run un-cancelled so a cancel does not swallow the Failed/Cancelled record.
        try { await _runService.FailAsync(runId, error, cancelled, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Run bookkeeping (fail) failed for {RunId}", runId); }
    }

    /// <summary>
    /// Promote the run's isolated workspace into its destination, then tear the workspace down.
    /// Only a CLEANLY drained run promotes automatically (plan D3, "Completed auto, else offer to publish"):
    /// this is only ever called from the two success arms, so a cancelled or failed run keeps its workspace
    /// and the panel offers to publish it. No-op when no workspace service was injected or the run has no
    /// workspace root — that is the pre-Batch-06 shape, and every existing orchestrator test hits it.
    /// Executor-agnostic on purpose: it reads <c>ctx.WorkspaceRoot</c>, which BOTH executors assign, so a
    /// promotion that only fired for headless runs would be a defect rather than a scoping choice.
    /// Failure-isolated: a fault logs and returns, and the files stay in the workspace.
    /// </summary>
    private async Task SafePromote(AgentRun run, RunContext ctx, CancellationToken ct)
    {
        if (_workspaces is null || string.IsNullOrEmpty(ctx.WorkspaceRoot))
            return;

        // 06 B7/: promotion is TERMINAL-ONLY and ONCE PER WORKSPACE, decided by one provisionedAtUtc in the
        // workspace metadata. A child run SHARES its parent's workspace and must never consume that promotion —
        // the parent's own terminal settle promotes everything the whole fan-out wrote. Worse than a double
        // promotion: SafePromote TEARS THE WORKSPACE DOWN after a successful promote, so the FIRST sibling to
        // finish would delete the directory its still-running siblings are writing into (in worktree mode, a
        // `git worktree remove`). Explicit, not left to the metadata lookup missing at the child's run id: that
        // would work by accident and log a warning per child.
        //
        // Deliberately ONE early return INSIDE this method rather than a guard at each of the two call sites:
        // this is the single funnel for both PromoteAsync and TearDownAsync, and the second call site (the
        // PlanResult.Fallback degrade arm) returns early and settles in the opposite order, so a two-site guard
        // could be missed at one of them — and every launcher-harness test drives exactly that arm.
        if (run.ParentRunId is not null)
            return;

        try
        {
            var result = await _workspaces.PromoteAsync(run.Id, ct).ConfigureAwait(false);
            if (result is null)
            {
                // The service already logged why. The workspace is deliberately NOT torn down: it holds the
                // only copy of the run's work, and the publish affordance can still offer it.
                return;
            }

            // Counts, ids and enum values only — a path never reaches Information (there is no
            // SensitiveError, so the service logs any path through SensitiveWarning instead). In worktree mode
            // "promoted" is the number of changes COMMITTED to the run branch, not files copied anywhere.
            _logger.LogInformation(
                "Run {RunId} promoted in {Mode} mode: {PromotedCount} file(s), {SkippedCount} skipped, {ConflictCount} conflict(s)",
                run.Id, result.Mode, result.Promoted, result.Skipped, result.Conflicts);

            if (result.RetainWorkspace)
            {
                // The workspace still holds work this promotion did not move — a copy-mode conflict whose
                // resolution kept the USER's newer file, or a worktree the run-branch commit could not fully
                // take. Tearing it down here would delete the only remaining copy, silently and irreversibly,
                // on a run that reports success. Retained instead: the publish affordance can still offer it
                // and the launcher's terminal retention rule ages it out.
                _logger.LogInformation(
                    "Run {RunId} workspace retained after promotion: it still holds work the promotion did not move", run.Id);
                return;
            }

            await _workspaces.TearDownAsync(run.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run bookkeeping (promote) failed for {RunId}", run.Id);
        }
    }

    private async Task SafeEndRun(IAgentTurnExecutor executor, AgentRun run, RunContext ctx, bool cancelled, bool failed)
    {
        // Executor cleanup is not allowed to flip an already-terminal run — swallow + log.
        try { await executor.EndRunAsync(run, ctx, cancelled, failed, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Executor EndRun failed for run {RunId}", run.Id); }
    }

    private async Task SafeOnPaused(IAgentTurnExecutor executor, AgentRun run, RunContext ctx)
    {
        // Non-terminal executor release on a budget pause. Failure-isolated: a release
        // error must never wedge or corrupt a parked run. Uses CancellationToken.None so a cancelled
        // token does not skip settling the live session back to Idle.
        try { await executor.OnPausedAsync(run, ctx, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Executor OnPaused failed for run {RunId}", run.Id); }
    }

    /// <summary>Mirrors the clarification question into any live transcript the executor holds, so a live
    /// session sees it without waiting on a later pull of the durable row.</summary>
    /// <param name="messageId">Must be the same id <see cref="SafePostClarificationQuestionAsync"/> wrote this
    /// question under, so the live copy is that row rather than a look-alike.</param>
    private async Task SafeMirrorClarificationQuestion(
        IAgentTurnExecutor executor, AgentRun run, RunContext ctx, Persona persona, Guid messageId, string? question)
    {
        if (string.IsNullOrWhiteSpace(question))
            return;

        try
        {
            await executor
                .MirrorClarificationQuestionAsync(run, ctx, persona, messageId, question, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Executor MirrorClarificationQuestion failed for run {RunId}", run.Id);
        }
    }
}
