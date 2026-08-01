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
        // `State < x` predicate lies about it. This set is the CAS's own source set minus WaitingForChildren
        // (below) — the pre-check exists so a refusal is a quiet log rather than a fired cancel whose request
        // nobody consumes.
        if (run.State is not (AgentRunState.Running or AgentRunState.Verifying))
        {
            // WaitingForChildren is D6's CASCADE and it arrives in G5: the parent's request is recorded and
            // every CHILD's cancel is fired, while the parent's own token is deliberately NEVER fired —
            // AgentRunOrchestrator's fan-out checks cts.IsCancellationRequested before the un-park CAS and
            // returns Cancelled, which settles the parent terminally. Refusing until that arm exists is the
            // safe half: recording an intent nothing can consume would strand it in the store until the
            // dispatch released it, and a parent that parked for its children's budget in the meantime would
            // carry a "children-parked" reason with a live "user" request behind it.
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

        // Record THEN fire, always in that order: the loop reads the request when it comes back from the
        // aborted step, so a cancel that arrived first could unwind past the read and settle Cancelled.
        _steering.FireCancel(runId);
        _logger.LogInformation("Pause requested for run {RunId} (was {State})", runId, run.State);
        return true;
    }
}
