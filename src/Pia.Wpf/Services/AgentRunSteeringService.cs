using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Default <see cref="IAgentRunSteeringService"/>. Reads the run, checks the pausable set, records the intent
/// in <see cref="IRunSteeringStore"/> and fires the dispatch's cancel sink — in that order, and nothing else.
/// <para>
/// IT WRITES NO ROW, deliberately. The row moves to <see cref="AgentRunState.Paused"/> from inside the run's
/// own loop, through <c>TryPauseUserAsync</c>, AFTER the aborted step has been given back to the plan. A
/// service that wrote the state itself would advertise a <c>Paused</c> run whose current step is still
/// <c>Running</c> — invisible to <c>NextPendingStepAsync</c>, so the resumed run would silently drop it — and
/// would race the loop's own terminal settle.
/// </para>
/// <para>
/// Failure-isolated like every other bookkeeping seam on this path: a read fault is a refused pause
/// (<c>false</c>), never an exception into a command handler.
/// </para>
/// </summary>
public sealed class AgentRunSteeringService : IAgentRunSteeringService
{
    private readonly IAgentRunService _runService;
    private readonly IRunSteeringStore _steering;
    private readonly ILogger<AgentRunSteeringService> _logger;

    public AgentRunSteeringService(
        IAgentRunService runService, IRunSteeringStore steering, ILogger<AgentRunSteeringService> logger)
    {
        _runService = runService;
        _steering = steering;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> PauseAsync(Guid runId, CancellationToken ct = default)
    {
        AgentRun? run;
        try
        {
            run = await _runService.GetAsync(runId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pause: reading run {RunId} failed", runId);
            return false;
        }

        if (run is null)
        {
            _logger.LogInformation("Pause: run {RunId} not found", runId);
            return false;
        }

        // EXPLICIT set, never a range (D7): WaitingForChildren = 8 sits ABOVE the terminal band, so any
        // `State < x` predicate lies about it. The same three states the CAS accepts as its source set, and
        // the pre-check exists so a refusal is a quiet log rather than a fired cancel whose request nobody
        // consumes. Planning is deliberately absent: a resume skips planning, so a run paused mid-plan would
        // come back with no plan at all.
        if (run.State is not (AgentRunState.Running or AgentRunState.Verifying or AgentRunState.WaitingForChildren))
        {
            _logger.LogInformation(
                "Pause: run {RunId} is not pausable in state {State}", runId, run.State);
            return false;
        }

        // Registration-scoped: false ⇒ nothing in this process is dispatching this run, so there is no loop
        // to interrupt and no cancel worth firing. Refused rather than silently dropped.
        if (!_steering.RecordPauseRequest(runId))
        {
            _logger.LogInformation(
                "Pause: run {RunId} is not dispatched in this process; nothing to interrupt", runId);
            return false;
        }

        if (run.State == AgentRunState.WaitingForChildren)
        {
            // D6's CASCADE, and the one branch that does NOT fire the run's own cancel.
            await CascadeToChildrenAsync(runId, ct).ConfigureAwait(false);
            _logger.LogInformation("Pause requested for delegating run {RunId} (cascaded to its children)", runId);
            return true;
        }

        // Record THEN fire, always in that order: the loop reads the request when it comes back from the
        // aborted step, so a cancel that arrived first could unwind past the read and settle Cancelled.
        _steering.FireCancel(runId);
        _logger.LogInformation("Pause requested for run {RunId} (was {State})", runId, run.State);
        return true;
    }

    /// <summary>
    /// D6: pause a fan-out PARENT by pausing what is actually working — its children.
    /// <para>
    /// The parent's own token is <b>deliberately never fired</b>. <c>AgentRunOrchestrator</c>'s fan-out checks
    /// <c>cts.IsCancellationRequested</c> before the un-park CAS and, if it is set, returns
    /// <c>Cancelled: true</c> — which the caller turns into <c>SafeFail(cancelled: true)</c>, a TERMINAL settle
    /// with <c>CompletedAt</c> stamped. That is the single easiest way to turn this whole feature into a
    /// cancel. The parent needs no signal at all: its <c>Task.WhenAll</c> completes naturally once every child
    /// dispatch task has returned, and it deliberately carries no <c>WaitAsync(cts.Token)</c> (07 D16).
    /// </para>
    /// <para>
    /// Per child, <c>RecordPauseRequest</c> THEN <c>FireCancel</c> — two operations, never one combined call.
    /// That split is why <see cref="IRunSteeringStore"/> has both: the parent above records without firing, and
    /// a combined API would make the rule invisible. A child whose dispatch is not registered here (settled
    /// between the read and the fire, or parked by a previous process) simply refuses the request and its
    /// cancel is a no-op — the read is taken once and not re-taken, because re-reading buys nothing a
    /// registration check does not already give.
    /// </para>
    /// </summary>
    private async Task CascadeToChildrenAsync(Guid parentRunId, CancellationToken ct)
    {
        IReadOnlyList<AgentRun> children;
        try
        {
            children = await _runService.GetChildRunsAsync(parentRunId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The parent's own request is already recorded and STAYS recorded: its loop honours it at the next
            // boundary it reaches (the parked-children arm, or the next in-process step). Revoking here would
            // be the unrecoverable direction — a pause the user asked for that silently never happens.
            _logger.LogWarning(ex, "Pause: reading the children of run {RunId} failed; the parent's request stands", parentRunId);
            return;
        }

        var cascaded = 0;
        foreach (var child in children)
        {
            if (child.State is AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Cancelled)
                continue;

            if (!_steering.RecordPauseRequest(child.Id))
                continue;

            _steering.FireCancel(child.Id);
            cascaded++;
        }

        _logger.LogInformation(
            "Pause: cascaded to {Count} of {Total} child run(s) of {RunId}", cascaded, children.Count, parentRunId);
    }
}
