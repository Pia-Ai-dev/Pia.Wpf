namespace Pia.Services.Interfaces;

/// <summary>
/// Startup reconcile for scheduled firings whose outcome nobody was alive to book (T0-1). A dispatched job's
/// schedule advances immediately, but its job-HEALTH columns are written only when the run settles — from a
/// continuation inside this process. Kill the process mid-run and that continuation never runs: the row keeps
/// <c>LastFiredAt</c> null while a one-off already reads <c>Status='Completed'</c>, so the job produced nothing,
/// records nothing, and (being a one-off) never fires again.
/// <para>
/// Runs ONCE at startup, and its position is load-bearing twice over — see
/// <see cref="ReconcileAsync"/>.
/// </para>
/// </summary>
public interface IScheduledFiringReconciler
{
    /// <summary>
    /// Books every settled firing the job row has no record of, and nothing else: no schedule is advanced, no
    /// job is retired or re-armed, no toast is raised (a startup toast storm for runs the user has already
    /// forgotten is not a notification, it is noise).
    /// <para>
    /// Must run AFTER <c>IAgentRunService.FailInterruptedRunsAsync</c> — a run the process died inside is
    /// non-terminal until that sweep settles it, and this reconcile only sees SETTLED runs, so running first
    /// would find exactly the crashed firings it exists for still invisible. Must run BEFORE the scheduler
    /// starts, so no tick can be writing the same job rows concurrently (which is also why this path needs no
    /// share of the scheduler's bookkeeping lock).
    /// </para>
    /// </summary>
    /// <returns>How many firings were booked. 0 is the healthy steady state.</returns>
    Task<int> ReconcileAsync(CancellationToken ct);
}
