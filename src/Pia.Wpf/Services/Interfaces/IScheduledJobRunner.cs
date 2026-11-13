namespace Pia.Services.Interfaces;

/// <summary>Not a bool, so the UI can say why nothing ran: a refusal is not a failure.</summary>
public enum ScheduledJobRunNowResult
{
    /// <summary>Handed to its runner; the run is still executing when this returns.</summary>
    Dispatched,

    /// <summary>No such job: deleted underneath the list, or a stale id.</summary>
    NotFound,

    /// <summary>Another device owns this job's schedule; only the owner may advance it.</summary>
    NotOwner,

    /// <summary>A run is executing (a parked one does not count); the schedule is left untouched.</summary>
    AlreadyRunning,

    /// <summary>A local-data clear is pending, so nothing may run until Pia restarts.</summary>
    ResetPending,
}

/// <summary>
/// Fires a scheduled job on demand, outside its schedule. Narrow on purpose: the implementation is
/// <c>ScheduledJobBackgroundService</c> (already a DI singleton), and this interface exists so the UI can
/// depend on the one operation it needs rather than on a <c>BackgroundService</c>.
/// </summary>
public interface IScheduledJobRunner
{
    /// <summary>Dispatches through the tick's own path and duplicate-run guard, skipping only the missed-run
    /// prompt; it does not await the run.</summary>
    Task<ScheduledJobRunNowResult> RunNowAsync(Guid jobId, CancellationToken ct = default);
}
