namespace Pia.Services.Interfaces;

/// <summary>
/// What a manual "run now" did. Distinguished rather than collapsed into a bool because the UI must be able
/// to say WHY nothing happened — a job owned by another device is a correct refusal, not a failure.
/// </summary>
public enum ScheduledJobRunNowResult
{
    /// <summary>The job was dispatched through the normal execution path and has settled.</summary>
    Dispatched,

    /// <summary>No such job — deleted underneath the list, or a stale id.</summary>
    NotFound,

    /// <summary>Another device owns this job's schedule; only the owner may advance it.</summary>
    NotOwner,
}

/// <summary>
/// Fires a scheduled job on demand, outside its schedule. Narrow on purpose: the implementation is
/// <c>ScheduledJobBackgroundService</c> (already a DI singleton), and this interface exists so the UI can
/// depend on the one operation it needs rather than on a <c>BackgroundService</c>.
/// </summary>
public interface IScheduledJobRunner
{
    /// <summary>
    /// Runs <paramref name="jobId"/> now, through the same dispatch a scheduled tick uses — including its
    /// <c>_runLock</c>, which is NOT an implementation detail to be optimised away: that lock is what bounds a
    /// delegating agent job to one scheduled slot at a time (roadmap R15). It deliberately bypasses only the
    /// missed-run grace prompt, which exists to ask whether a LATE job should still run and has no meaning
    /// for a run the user just asked for.
    /// <para>
    /// Awaits the run to completion, like the tick does. A caller on the UI thread must not block on it.
    /// </para>
    /// </summary>
    Task<ScheduledJobRunNowResult> RunNowAsync(Guid jobId, CancellationToken ct = default);
}
