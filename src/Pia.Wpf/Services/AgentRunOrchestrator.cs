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
    private readonly ILogger<AgentRunOrchestrator> _logger;

    public AgentRunOrchestrator(
        IAgentRunService runService,
        IAgentPlanner planner,
        ILogger<AgentRunOrchestrator> logger)
    {
        _runService = runService;
        _planner = planner;
        _logger = logger;
    }

    public async Task RunAsync(
        AgentRun run,
        IAgentTurnExecutor executor,
        Persona persona,
        AiProvider provider,
        RunProfile profile,
        CancellationToken externalToken)
    {
        // R13: link the run CTS from the caller's token. Interactive passes session.Cts.Token, so
        // ChatSession.Cancel() (which cancels session.Cts) propagates to the run + in-flight step.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var ctx = new RunContext(run.Goal ?? string.Empty, profile);
        var cancelled = false;
        var failed = false;
        Guid? runFirst = null;
        var runLast = Guid.Empty;

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
            await SafeSetState(run.Id, AgentRunState.Planning, cts.Token).ConfigureAwait(false);

            var plan = await _planner.PlanAsync(ctx.Goal, ctx, persona, provider, cts.Token).ConfigureAwait(false);
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
                await SafeEndRun(executor, run, ctx, cancelled).ConfigureAwait(false);
                return;
            }

            await SafeReplaceSteps(run.Id, plan.Steps, cts.Token).ConfigureAwait(false);

            var replans = 0;
            // R2: re-query the persisted Pending list each iteration — a foreach over a snapshot
            // would never run replanned steps.
            while (await _runService.NextPendingStepAsync(run.Id, cts.Token).ConfigureAwait(false) is { } step)
            {
                if (ctx.StepBudgetExceeded || ctx.WallClockExceeded) // R5: both checks, never silent
                {
                    await PinRange().ConfigureAwait(false); // R3: keep the executed-so-far slice on a truncated run
                    await SafeComplete(run.Id, cts.Token, truncated: true,
                        reason: ctx.WallClockExceeded ? "wall-clock" : "step-cap").ConfigureAwait(false);
                    await SafeEndRun(executor, run, ctx, cancelled).ConfigureAwait(false);
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

            if (!cancelled && !failed)
            {
                await PinRange().ConfigureAwait(false);
                await SafeSetState(run.Id, AgentRunState.Verifying, cts.Token).ConfigureAwait(false); // no-op pass-through (R12)
                await SafeComplete(run.Id, cts.Token).ConfigureAwait(false);
            }

            await SafeEndRun(executor, run, ctx, cancelled).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await SafeFail(run.Id, null, cancelled: true).ConfigureAwait(false);
            await SafeEndRun(executor, run, ctx, cancelled: true).ConfigureAwait(false);
        }
        catch (Exception ex) // planner-cannot-plan (threw) / executor crash — critical path, fail the run
        {
            _logger.LogError(ex, "Agent run {RunId} failed", run.Id);
            await SafeFail(run.Id, ex.Message, cancelled: false).ConfigureAwait(false);
            await SafeEndRun(executor, run, ctx, cancelled: false).ConfigureAwait(false);
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

    private async Task SafeFail(Guid runId, string? error, bool cancelled)
    {
        // Terminal fail writes run un-cancelled so a cancel does not swallow the Failed/Cancelled record.
        try { await _runService.FailAsync(runId, error, cancelled, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Run bookkeeping (fail) failed for {RunId}", runId); }
    }

    private async Task SafeEndRun(IAgentTurnExecutor executor, AgentRun run, RunContext ctx, bool cancelled)
    {
        // Executor cleanup is not allowed to flip an already-terminal run — swallow + log.
        try { await executor.EndRunAsync(run, ctx, cancelled, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Executor EndRun failed for run {RunId}", run.Id); }
    }
}
