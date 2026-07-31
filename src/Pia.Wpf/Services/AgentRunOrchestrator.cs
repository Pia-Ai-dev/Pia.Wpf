using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// The plan → act → failure-only-replan → complete loop (§13.2). UI-agnostic — never captures a
/// <see cref="SynchronizationContext"/> and uses <c>ConfigureAwait(false)</c> throughout; each
/// executor owns its own threading. Owns the run's linked <see cref="CancellationTokenSource"/>
/// (R13), the ledger accrual, and a <see cref="RunContext"/>. Run state/ledger writes are
/// failure-isolated (Safe* wrappers, §12.5/§13.10); planner-cannot-plan and executor crashes are
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

    /// <summary>
    /// Cap on a delegated run's answer text as it is folded into the parent's context. Same number as
    /// <c>AgentPlanner</c>'s own analysis cap, for the same reason: this text lands in the replan and verify
    /// prompts, and one verbose child must not crowd out its siblings.
    /// </summary>
    private const int MaxChildAnswerChars = 4000;

    /// <summary>What a settled child's step reports when its answer could not be read. Says the work ran
    /// elsewhere rather than implying the step produced nothing (the failure mode
    /// <c>CompletedStepSummary.FromEarlierSegment</c> exists for).</summary>
    private const string DelegatedAnswerUnavailable = "(this step ran as a delegated run; its result text is not available here)";

    private static readonly JsonSerializerOptions LedgerJsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <param name="workspaces">Batch 06 G4. TRAILING and DEFAULTED, like every dependency this batch adds:
    /// this type is hand-constructed positionally in a dozen test sites, and a required parameter would break
    /// all of them at once. Null ⇒ no promotion, which is the pre-Batch-06 loop exactly.</param>
    /// <param name="childLauncher">Batch 07 G10, trailing and defaulted for the same reason. Null ⇒ <b>no
    /// delegation, ever</b>: a plan's parallel groups are recorded and ignored, and every step runs in-process
    /// exactly as before. No DI cycle — the launcher is a singleton that resolves this type lazily from a
    /// per-run scope, so nothing is constructed twice.</param>
    /// <param name="chats">Batch 07 G10, trailing and defaulted. Only used to read a settled child's answer back
    /// into the parent's context; null ⇒ the fan-out still works and the parent's replan/verify prompts see
    /// <see cref="DelegatedAnswerUnavailable"/> instead of the child's text.</param>
    public AgentRunOrchestrator(
        IAgentRunService runService,
        IAgentPlanner planner,
        IAgentVerifier verifier,
        ILogger<AgentRunOrchestrator> logger,
        IRunWorkspaceService? workspaces = null,
        IHeadlessRunLauncher? childLauncher = null,
        IAssistantChatService? chats = null)
    {
        _runService = runService;
        _planner = planner;
        _verifier = verifier;
        _logger = logger;
        _workspaces = workspaces;
        _childLauncher = childLauncher;
        _chats = chats;
    }

    public async Task RunAsync(
        AgentRun run,
        IAgentTurnExecutor executor,
        Persona persona,
        AiProvider provider,
        RunProfile profile,
        CancellationToken externalToken,
        bool resume = false)
    {
        // R13: link the run CTS from the caller's token. Interactive passes session.Cts.Token, so
        // ChatSession.Cancel() (which cancels session.Cts) propagates to the run + in-flight step.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var ctx = new RunContext(run.Goal ?? string.Empty, profile);
        var cancelled = false;
        var failed = false;
        Guid? runFirst = null;
        var runLast = Guid.Empty;

        // R3: on resume, seed the range from the persisted pre-pause slice so the terminal PinRange
        // EXTENDS the run's transcript range rather than shrinking it to only the post-resume portion.
        // runFirst's ??= below then keeps this original first message; runLast advances to the latest.
        if (resume)
        {
            runFirst = run.FirstMessageId;
            runLast = run.LastMessageId ?? Guid.Empty;
        }

        // R3: pin the run-level transcript slice off the STABLE step message Ids accrued so far.
        // Shared by every terminal path (success, truncation, cancel, fail) so a run that executed
        // steps never keeps a null range — symmetric with the clean-success path.
        async Task PinRange()
        {
            if (runFirst is { } first)
                await SafeRange(run.Id, first, runLast, cts.Token).ConfigureAwait(false);
        }

        try
        {
            await executor.BeginRunAsync(run, ctx, cts.Token).ConfigureAwait(false);

            // E2: RunContext is built fresh per RunAsync, so without this a resumed run's critic (and any
            // replan) would judge the goal on ONLY the post-resume steps — the pre-pause work, and with H1
            // its declared artifacts, would be invisible. Seed the persisted Done steps before anything
            // reads ctx.CompletedSteps.
            if (resume)
                await SafeSeedResumeContext(run.Id, ctx, cts.Token).ConfigureAwait(false);

            // D1: a resume must NOT re-plan. ReplaceStepsAsync writes the plan verbatim and does not
            // preserve Done steps, so re-planning here would wipe the persisted Done+Pending steps and
            // re-run the whole goal from scratch. On resume we skip Planning/PlanAsync/ReplaceSteps and
            // drop straight into the outer verify/drain loop, which re-queries the persisted Pending
            // remainder (R2) and runs only the steps that had not completed before the pause.
            if (!resume)
            {
                await SafeSetState(run.Id, AgentRunState.Planning, cts.Token).ConfigureAwait(false);

                var plan = await _planner.PlanAsync(ctx.Goal, ctx, persona, provider, cts.Token).ConfigureAwait(false);
                // I1: the plan turn's rounds (≥2, doubled by the firm retry) are real spend — accrue
                // them run-level BEFORE branching, so the degrade path below cannot drop them.
                await SafeAddUsage(run.Id, plan.Usage, cts.Token).ConfigureAwait(false);
                if (plan.FallBackToSingleTurn) // R10
                {
                    var fr = await executor.RunSingleTurnFallbackAsync(run, ctx, cts.Token).ConfigureAwait(false);
                    // The fallback turn owns no step row, so its usage has no SafeRecordStep to ride on —
                    // accrue it run-level here or the whole degrade path bills as zero tokens (I1). Before
                    // the branch: a cancelled/failed fallback turn spent its tokens just the same.
                    await SafeAddUsage(run.Id, fr.Usage, cts.Token).ConfigureAwait(false);
                    if (fr.Cancelled)
                    {
                        cancelled = true;
                        await SafeFail(run.Id, fr.Error, cancelled: true).ConfigureAwait(false);
                    }
                    else if (!fr.Succeeded)
                    {
                        // R5/R10: a failed fallback turn is never presented as a clean Completed run.
                        failed = true;
                        await SafeFail(run.Id, fr.Error, cancelled: false).ConfigureAwait(false);
                    }
                    else
                    {
                        if (fr.FirstMessageId != Guid.Empty)
                            await SafeRange(run.Id, fr.FirstMessageId, fr.LastMessageId, cts.Token).ConfigureAwait(false);
                        // Batch 06 B8, the SECOND terminal path: this arm returns at the `return` below and
                        // never reaches the terminal-settle block, and it settles Complete BEFORE EndRun —
                        // the opposite order to the main path. Promotion still goes before CompleteAsync.
                        // There is no verify on this arm at all (the planner degraded), so "promote what the
                        // turn wrote" is the whole contract.
                        await SafePromote(run, ctx, cts.Token).ConfigureAwait(false);
                        await SafeComplete(run.Id, cts.Token).ConfigureAwait(false); // clean; zero steps recorded
                    }
                    await SafeEndRun(executor, run, ctx, cancelled, failed).ConfigureAwait(false);
                    return;
                }

                await SafeReplaceSteps(run.Id, plan.Steps, cts.Token).ConfigureAwait(false);
            }
            // resume: TryBeginResumeAsync already CAS'd State→Running; the drain loop re-sets Running per
            // step. The persisted Pending remainder drives the loop — no re-plan, no step wipe (D1).

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
                    // replan itself degraded to Fallback → the same terminal fail as an exhausted budget
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
                // R2: re-query the persisted Pending list each iteration — a foreach over a snapshot
                // would never run replanned steps.
                while (await _runService.NextPendingStepAsync(run.Id, cts.Token).ConfigureAwait(false) is { } step)
                {
                    if (ctx.StepBudgetExceeded || ctx.WallClockExceeded) // R5: both checks, never silent
                    {
                        await PinRange().ConfigureAwait(false); // R3: keep the executed-so-far slice
                        await SafePause(run.Id, cts.Token,
                            reason: ctx.WallClockExceeded ? "wall-clock" : "step-cap").ConfigureAwait(false);
                        // Pause is NOT terminal: deliberately NO SafeEndRun — Live must not settle
                        // ChatState.Completed or raise TurnCompleted (guardrail 5), and Headless must not
                        // persist-and-finalize here. But the executor still needs a NON-terminal release
                        // hook: for a Live run, only EndRunAsync clears the session's IsStreaming, so
                        // without this the foreground chat would be wedged Running forever (spinner +
                        // disabled Send). OnPausedAsync settles the live session to Idle (no TurnCompleted,
                        // no Completed/Error); Headless no-ops. The run sits WaitingForInput until
                        // TryBeginResumeAsync claims it. Release the loop.
                        await SafeOnPaused(executor, run, ctx).ConfigureAwait(false);
                        return;
                    }

                    // Batch 07 D7/D11: a step the plan put in a PARALLEL GROUP is not executed in-process —
                    // the whole group is dispatched as sibling CHILD runs and awaited here. Null covers every
                    // ordinary step, which is every step of every plan a build with no persona roster produces
                    // (the planner only ever writes a parallelGroup when a roster is configured).
                    var fanOut = await TryFanOutAsync(run, step, ctx, profile, cts).ConfigureAwait(false);
                    if (fanOut is { } children)
                    {
                        if (children.Abandoned)
                        {
                            // 07 G8: the un-park CAS lost — this run's row now belongs to another writer
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

                        if (children.AnyParked)
                        {
                            // D13: a PARKED child is not a finished child. Its work is durable and resumable,
                            // so failing the parent would throw it away and burn a replan. Re-park the parent
                            // through the existing budget-pause shape — its fan-out steps are still Pending, so
                            // one Continue on the parent re-dispatches the group (and cancels this generation
                            // first). Deliberately NO SafeEndRun and no promotion: a park is not terminal.
                            await PinRange().ConfigureAwait(false);
                            await SafePause(run.Id, cts.Token, reason: "children-parked").ConfigureAwait(false);
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

                    var r = await executor.ExecuteStepAsync(run, step, ctx, cts.Token).ConfigureAwait(false); // critical path
                    await SafeRecordStep(step.Id, r, cts.Token).ConfigureAwait(false); // R16 ledger + R3 slice
                    ctx.RecordStep(step, r);
                    // Track only valid (non-empty) message Ids so a step that produced no transcript
                    // (e.g. a cancelled step) never poisons the run-level range with Guid.Empty.
                    if (r.FirstMessageId != Guid.Empty) runFirst ??= r.FirstMessageId;
                    if (r.LastMessageId != Guid.Empty) runLast = r.LastMessageId;

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

                // Verify FAIL → feed the SHARED replan budget (guardrail 3).
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
                    // replan itself degraded to Fallback → settle unverified, NOT Failed (steps genuinely ran)
                }

                unverifiedTruncated = true; // replans exhausted OR replan degraded
                break;
            }

            // ---- single terminal settle (SafeEndRun → SafeComplete, exactly once, every path) ----
            if (!cancelled && !failed)
            {
                await PinRange().ConfigureAwait(false);
                // §13.2 order: END the run (Live: settle terminal state; Headless: persist the chat) BEFORE
                // marking it Completed — so no crash / RunChanged consumer observes a Completed run whose chat
                // is not yet persisted (headless persists only in EndRunAsync). A verify-unverified run
                // settles Completed+truncated reason "unverified".
                await SafeEndRun(executor, run, ctx, cancelled, failed).ConfigureAwait(false);
                // Batch 06 B8: promote BEFORE CompleteAsync. Verify has already run against the RUN ROOT
                // (B3), so the artifacts it confirmed are the files being promoted; and no RunChanged
                // consumer can observe a Completed run whose deliverables are still only in a workspace the
                // sweep may delete (plan R4/R5) — which is what dissolves the "Completed but not yet
                // promoted" window without a promotion-aware sweep. Failure-isolated: a promotion fault
                // leaves the files in the workspace for the publish affordance to offer, and never fails an
                // otherwise-successful run.
                await SafePromote(run, ctx, cts.Token).ConfigureAwait(false);
                await SafeComplete(run.Id, cts.Token,
                    truncated: unverifiedTruncated,
                    reason: unverifiedTruncated ? "unverified" : null).ConfigureAwait(false);
            }
            else
            {
                await SafeEndRun(executor, run, ctx, cancelled, failed).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // R3: a cancel can now surface here from the in-flight verify turn (SafeVerify rethrows a
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
    /// The Done steps of the run, re-ordinaled 0..k-1 with their ORIGINAL Ids preserved so
    /// <see cref="IAgentRunService.ReplaceStepsAsync"/> re-inserts them (it writes ordinals verbatim
    /// and does not itself preserve Done — §F/§13.2 <c>KeepDone</c>).
    /// </summary>
    private async Task<List<AgentStep>> KeepDoneAsync(Guid runId, CancellationToken ct)
    {
        AgentRun? run = null;
        try { run = await _runService.GetAsync(runId, ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "KeepDone: failed to read run {RunId}", runId); }

        var done = run?.Plan.Where(s => s.Status == AgentStepStatus.Done).OrderBy(s => s.Ordinal).ToList()
                   ?? new List<AgentStep>();
        for (var i = 0; i < done.Count; i++)
            done[i].Ordinal = i;
        return done;
    }

    // Verify is failure-isolated: a crash/timeout degrades to ACCEPT (guardrail 1) so it never wedges
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
    /// say so instead of implying those steps never ran. Failure-isolated (guardrail 1): a read fault
    /// leaves the run with the old partial picture rather than failing the resume.
    /// </summary>
    private async Task SafeSeedResumeContext(Guid runId, RunContext ctx, CancellationToken ct)
    {
        try
        {
            var persisted = await _runService.GetAsync(runId, ct).ConfigureAwait(false);
            var done = persisted?.Plan
                .Where(s => s.Status == AgentStepStatus.Done)
                .OrderBy(s => s.Ordinal)
                .Select(s => new CompletedStepSummary(
                    s.Ordinal, s.Title, s.Intent ?? string.Empty, Succeeded: true, VisibleText: string.Empty,
                    s.ExpectedArtifact, FromEarlierSegment: true))
                .ToList();
            if (done is null || done.Count == 0)
                return;

            ctx.SeedCompletedSteps(done);
            _logger.LogInformation("Resume seeded {Count} pre-pause step(s) into the context of run {RunId}", done.Count, runId);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Run bookkeeping (resume context seed) failed for {RunId}", runId); }
    }

    // Run-level usage accrual (stepId: null) for the loop's non-step turns — plan, replan and verify
    // (I1) — updates ledger totals + wall-clock and raises RunChanged. Step usage goes through
    // SafeRecordStep instead (per-step entry). Skips a null usage so a fake/no-usage provider path adds
    // no spurious ledger write.
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

    // ---- Batch 07 D7: the fan-out (delegate a step group to sibling child runs and await them) ----

    /// <summary>How a fan-out settled, as the drain loop needs to see it. Never both parked and cancelled —
    /// the caller checks <paramref name="Cancelled"/> first, because a cascade cancel outranks a park.
    /// <para>
    /// <paramref name="Abandoned"/> outranks everything: it means <c>TryEndChildWaitAsync</c> lost its CAS, so
    /// this run's row is owned by another writer and this loop must stop WITHOUT writing to it (07 G8/D9).
    /// </para></summary>
    private sealed record FanOutResult(bool AnyParked, bool AnyFailed, bool Cancelled, string? Error, bool Abandoned = false);

    /// <summary>
    /// Dispatch <paramref name="step"/>'s whole parallel group as sibling CHILD runs, await every one of them,
    /// and settle each sibling step from the child's persisted row. Returns <c>null</c> when this step is NOT a
    /// fan-out, which is the overwhelmingly common case and means "run it in-process, exactly as before".
    /// <para>
    /// Four independent reasons to decline, each one a degrade rather than a failure: no launcher was injected
    /// (⇒ delegation is off for the whole build), the step declares no <c>parallelGroup</c> (D11 — absence means
    /// sequential), this run is ITSELF a child (§7.5's depth guard), or the group has fewer than two pending
    /// members (D11 — a group of one is not a fan-out; running it as a child would cost all of delegation and
    /// buy none of the parallelism).
    /// </para>
    /// <para>
    /// Every child interaction is failure-isolated. A launcher fault while dispatching sibling 3 of 4 marks
    /// sibling 3 failed and still awaits siblings 1–2: a dispatched child is never left unawaited, because that
    /// is precisely the orphan D16 exists to rule out.
    /// </para>
    /// </summary>
    private async Task<FanOutResult?> TryFanOutAsync(
        AgentRun run, AgentStep step, RunContext ctx, RunProfile profile, CancellationTokenSource cts)
    {
        if (_childLauncher is null)
            return null;

        if (ParallelGroupOf(step) is not { } group)
            return null;

        // Depth guard (§7.5), one line and structural: a run that is itself a child NEVER delegates. It bounds
        // the wall clock, the child-pool pressure and the scheduled-job _runLock hold to a single level — a
        // plan shaped like a tree would otherwise multiply R15 by its depth.
        if (run.ParentRunId is not null)
            return null;

        var siblings = await SafeSiblingGroupAsync(run.Id, group, cts.Token).ConfigureAwait(false);
        if (siblings.Count < 2)
            return null;

        // D13's park leaves a child at WaitingForInput with its step still Pending, so a RESUMED parent arrives
        // here again with the same pending group. Nothing links a child to a STEP (only ParentRunId → parent),
        // so the parent cannot tell this group already has a parked generation behind it — and a parked run is
        // never swept, so it would sit there forever owning a visible stub chat. Cancel the old generation first.
        await SafeCancelStaleChildrenAsync(_childLauncher, run.Id, cts.Token).ConfigureAwait(false);

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
                var handle = await _childLauncher.LaunchChildAsync(
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
                    // nothing (§7.6). Null ⇒ the parent runs unisolated and so does the child.
                    parentWorkspaceRoot: ctx.WorkspaceRoot,
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
                    _childLauncher.CancelAsync(d.Handle.RunId).SafeFireAndForget(_logger);
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

                case AgentRunState.WaitingForInput:
                    // §0.9/D13: HeadlessRunHandle.Completion settles on a budget PAUSE too, which is not
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
        // shutdown, or ChatSession.Cancel()), this loop still owns the run and must settle it Cancelled
        // itself — exactly like an in-process step that came back Cancelled. Ending the wait first would
        // briefly advertise the run as Running on its way to Cancelled for no gain.
        if (cts.IsCancellationRequested)
            return new FanOutResult(AnyParked: false, AnyFailed: false, Cancelled: true, "cancelled while awaiting delegated runs");

        // 07 G8/D9. Leave WaitingForChildren through a CAS, not a blind write. Every arm the caller can take
        // from here writes this run's row — the parked arm calls PauseAsync, the failed arm may Fail it, the
        // clean arm sets Running per step — so this is the single place to establish that the row is still
        // OURS. A false means it is not: cascade-cancelled to Cancelled, or re-parked WaitingForInput by
        // another process's startup reconcile. Continuing would resurrect a run somebody else settled (R11).
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
    /// The children's own internal steps count against their OWN budgets (D15); nesting the enforced budget
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
    /// <see cref="RunProfile.MinWallClockMinutes"/>). Phase 3 R15 is the reason —
    /// <c>ScheduledJobBackgroundService</c> holds its <c>_runLock</c> from before the launch across
    /// <c>await handle.Completion</c>, so while a fan-out runs NO scheduled job of either kind can dispatch, for
    /// the parent's wall clock PLUS every descendant's. Halving keeps a fan-out roughly inside the envelope one
    /// scheduled job already occupies.
    /// <para>
    /// Derived from the PARENT's profile rather than re-read from settings (which §7.5 suggested): the parent's
    /// profile already IS <c>RunProfile.FromBudget(settings.Scheduled*)</c> on the launch path, so this yields
    /// the same numbers without giving the orchestrator a settings dependency — and it also honours an explicit
    /// per-request budget, which a settings read would silently discard.
    /// </para>
    /// </summary>
    private static RunProfile ChildProfile(RunProfile parent) => parent with
    {
        WallClock = TimeSpan.FromMinutes(
            Math.Max(RunProfile.MinWallClockMinutes, parent.WallClock.TotalMinutes / 2)),
    };

    /// <summary>
    /// The <c>{"parallelGroup":N}</c> marker the planner writes into <c>AgentStep.ExtraJson</c> when a plan
    /// declares steps independent AND a persona roster is configured (D11). Swallowing by design: ANY parse
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

                await launcher.CancelAsync(old.Id).ConfigureAwait(false);

                // A parked child is the one shape the cancel above cannot reach across a restart: states at or
                // above WaitingForInput are never swept, on purpose (a parked run must survive a restart), so
                // this settle is the only thing that stops it lingering with its own stub chat forever.
                if (old.State is AgentRunState.WaitingForInput)
                    await _runService.FailAsync(old.Id, "superseded by a re-dispatched fan-out", cancelled: true,
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
    /// <para>
    /// TOKENS ONLY, never time. The parent's <c>WallClockMs</c> stays its own worked time and the children's is
    /// visible on the children, in the drill-down. And <c>stepId</c> stays null rather than the sibling's id
    /// because the parent ran NO turn for that step — a per-step entry would claim it spent tokens it never did.
    /// </para>
    /// <para>
    /// Idempotence is the CALLER's: a parent awaits each child exactly once and pushes only from a terminal
    /// branch. Stated loss: a crash between a child's settle and this push loses that roll-up. The child's own
    /// ledger still holds the truth — the parent's number is an aggregate convenience, not an accounting record.
    /// </para>
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

    // ---- Failure-isolated bookkeeping (§12.5/§13.10): never fail the run ----

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
                r.FirstMessageId, r.LastMessageId, r.Usage, ct).ConfigureAwait(false);
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

    // Budget pause: park the run WaitingForInput (non-terminal). Failure-isolated (guardrail 1) — a
    // pause bookkeeping error must never corrupt or wedge the run.
    private async Task SafePause(Guid runId, CancellationToken ct, string reason)
    {
        try { await _runService.PauseAsync(runId, reason, ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Run bookkeeping (pause) failed for {RunId}", runId); }
    }

    /// <summary>
    /// Park the parent at <see cref="AgentRunState.WaitingForChildren"/> for the span of a fan-out (07 D9).
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
    /// End the child wait via the CAS (07 D9). A fault is reported as <c>true</c> — "assume the run is still
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
    /// Promote the run's isolated workspace into its destination, then tear the workspace down (Batch 06 B8).
    /// Only a CLEANLY drained run promotes automatically (plan D3, "Completed auto, else offer to publish"):
    /// this is only ever called from the two success arms, so a cancelled or failed run keeps its workspace
    /// and the panel offers to publish it. No-op when no workspace service was injected or the run has no
    /// workspace root — that is the pre-Batch-06 shape, and every existing orchestrator test hits it.
    /// <para>
    /// Executor-agnostic on purpose: it reads <c>ctx.WorkspaceRoot</c>, which BOTH executors assign, so a
    /// promotion that only fired for headless runs would be a defect rather than a scoping choice.
    /// Failure-isolated (guardrail 1): a fault logs and returns, and the files stay in the workspace.
    /// </para>
    /// </summary>
    private async Task SafePromote(AgentRun run, RunContext ctx, CancellationToken ct)
    {
        if (_workspaces is null || string.IsNullOrEmpty(ctx.WorkspaceRoot))
            return;

        // 06 B7/§13.4: promotion is TERMINAL-ONLY and ONCE PER WORKSPACE, decided by one provisionedAtUtc in the
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
        // Non-terminal executor release on a budget pause (guardrail 1/5). Failure-isolated: a release
        // error must never wedge or corrupt a parked run. Uses CancellationToken.None so a cancelled
        // token does not skip settling the live session back to Idle.
        try { await executor.OnPausedAsync(run, ctx, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Executor OnPaused failed for run {RunId}", run.Id); }
    }
}
