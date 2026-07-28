using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
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

    public AgentRunOrchestrator(
        IAgentRunService runService,
        IAgentPlanner planner,
        IAgentVerifier verifier,
        ILogger<AgentRunOrchestrator> logger)
    {
        _runService = runService;
        _planner = planner;
        _verifier = verifier;
        _logger = logger;
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
                        if (replans++ < profile.MaxReplans)
                        {
                            var revised = await _planner.ReplanAsync(ctx, r.Error, persona, provider, cts.Token).ConfigureAwait(false);
                            await SafeAddUsage(run.Id, revised.Usage, cts.Token).ConfigureAwait(false); // I1
                            if (revised.FallBackToSingleTurn)
                            {
                                failed = true;
                                await PinRange().ConfigureAwait(false); // R3: keep the executed-so-far slice
                                await SafeFail(run.Id, r.Error, cancelled: false).ConfigureAwait(false);
                                break;
                            }
                            // Keep the Done steps (immutable, original Ids preserved), append the revised
                            // steps continuing the ordinal sequence; ReplaceSteps writes ordinals verbatim.
                            var doneSteps = await KeepDoneAsync(run.Id, cts.Token).ConfigureAwait(false);
                            var offset = doneSteps.Count;
                            var revisedSteps = revised.Steps.Select((s, i) => { s.Ordinal = offset + i; return s; });
                            await SafeReplaceSteps(run.Id, doneSteps.Concat(revisedSteps).ToList(), cts.Token).ConfigureAwait(false);
                            continue; // re-query picks up the revised steps (R2)
                        }

                        failed = true;
                        await PinRange().ConfigureAwait(false); // R3: keep the executed-so-far slice
                        await SafeFail(run.Id, r.Error, cancelled: false).ConfigureAwait(false);
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

    private async Task SafeFail(Guid runId, string? error, bool cancelled)
    {
        // Terminal fail writes run un-cancelled so a cancel does not swallow the Failed/Cancelled record.
        try { await _runService.FailAsync(runId, error, cancelled, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Run bookkeeping (fail) failed for {RunId}", runId); }
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
