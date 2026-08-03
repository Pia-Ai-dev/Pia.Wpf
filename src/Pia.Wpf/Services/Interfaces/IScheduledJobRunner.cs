namespace Pia.Services.Interfaces;

/// <summary>
/// What a manual "run now" did. Distinguished rather than collapsed into a bool because the UI must be able
/// to say WHY nothing happened — a job owned by another device is a correct refusal, not a failure.
/// </summary>
public enum ScheduledJobRunNowResult
{
    /// <summary>
    /// The job was handed to its runner through the normal execution path. NOT "has settled" — the run is
    /// executing in the background when this comes back, exactly as a tick's dispatch is.
    /// </summary>
    Dispatched,

    /// <summary>No such job — deleted underneath the list, or a stale id.</summary>
    NotFound,

    /// <summary>Another device owns this job's schedule; only the owner may advance it.</summary>
    NotOwner,

    /// <summary>
    /// A run of this job is executing right now, so a second one was refused (Batch 08 §19 Q4). The schedule is
    /// untouched: the refusal costs the manual fire, never the job's next occurrence. A run that is merely
    /// PARKED does not refuse — see <c>AgentRunStates.IsExecuting</c>.
    /// </summary>
    AlreadyRunning,
}

/// <summary>
/// Fires a scheduled job on demand, outside its schedule. Narrow on purpose: the implementation is
/// <c>ScheduledJobBackgroundService</c> (already a DI singleton), and this interface exists so the UI can
/// depend on the one operation it needs rather than on a <c>BackgroundService</c>.
/// </summary>
public interface IScheduledJobRunner
{
    /// <summary>
    /// Runs <paramref name="jobId"/> now, through the same dispatch a scheduled tick uses — including the
    /// duplicate-run guard, which is what stops a manual fire from doubling a scheduled run already in flight.
    /// It deliberately bypasses only the missed-run grace prompt, which exists to ask whether a LATE job should
    /// still run and has no meaning for a run the user just asked for.
    /// <para>
    /// DISPATCHES; it does not await the run, exactly like the tick (hermes review #2 — one long run must not
    /// hold up the device's other scheduled jobs, and it must not hold up the settings UI either). So
    /// <see cref="ScheduledJobRunNowResult.Dispatched"/> means "started", and the run's result appears in the
    /// chat it creates when it gets there.
    /// </para>
    /// </summary>
    Task<ScheduledJobRunNowResult> RunNowAsync(Guid jobId, CancellationToken ct = default);
}
