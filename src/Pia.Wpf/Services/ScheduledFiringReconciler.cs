using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <inheritdoc cref="IScheduledFiringReconciler"/>
public sealed class ScheduledFiringReconciler : IScheduledFiringReconciler
{
    private readonly IScheduledJobService _jobs;
    private readonly IAgentRunService _runs;
    private readonly ILogger<ScheduledFiringReconciler> _logger;

    public ScheduledFiringReconciler(
        IScheduledJobService jobs, IAgentRunService runs, ILogger<ScheduledFiringReconciler> logger)
    {
        _jobs = jobs;
        _runs = runs;
        _logger = logger;
    }

    public async Task<int> ReconcileAsync(CancellationToken ct)
    {
        var firings = await _runs.GetLatestSettledFiringsAsync(ct);
        if (firings.Count == 0) return 0;

        // GetAllAsync, NOT GetActiveAsync: the severe case is precisely a one-off sitting at
        // Status='Completed' (dispatch settled it) with no record of ever having fired, and an Active-only read
        // would skip exactly that row. Also deliberately NOT filtered by OwnerDeviceId the way GetDueJobsAsync
        // is — the firings on the left of this join are AgentRuns rows THIS device created, and the four columns
        // being written are device-local execution state absent from SyncScheduledJob, so there is no other
        // device's state to step on.
        var jobs = await _jobs.GetAllAsync();
        var byId = jobs.ToDictionary(j => j.Id);

        var booked = 0;
        foreach (var firing in firings)
        {
            ct.ThrowIfCancellationRequested();

            if (!byId.TryGetValue(firing.JobId, out var job))
                continue;   // a TriggerRef whose job has since been deleted

            // THE idempotence guard, and it lives HERE rather than as a `WHERE LastFiredAt IS NULL` clause in
            // the write, because the write has a second caller (a resumed run's booking) for which "already has
            // a LastFiredAt" is the normal case — a recurring job books a firing on every occurrence.
            //
            // TIMEZONE. job.LastFiredAt comes back from DateTime.Parse of a LOCAL "O" string (kind Local);
            // SettledAtUtc is UTC. DateTime's comparison operators ignore Kind entirely, so comparing them raw
            // is off by the host's offset in whichever direction the sign points: east of Greenwich every
            // healthy job looks freshly booked and nothing is ever reconciled, west of it every healthy job
            // looks stale and gets re-booked on every single startup. Normalize both, in C#, never in SQL.
            if (job.LastFiredAt is { } last && last.ToUniversalTime() >= firing.SettledAtUtc)
                continue;

            // STRICT less-than above is what makes a second pass a no-op: the booking below stores exactly
            // SettledAtUtc.ToLocalTime(), which normalizes back to the same instant, so the next pass sees
            // `>=` and skips.
            //
            // ToLocalTime, because LastFiredAt's convention is local — every other writer of that column
            // stamps DateTime.Now. Storing the UTC instant instead would render two hours early in the UI and
            // would make the guard above compare a local-parsed value against a UTC one all over again.
            await _jobs.MarkFiringOutcomeAsync(
                job.Id, firing.SettledAtUtc.ToLocalTime(), firing.ChatId,
                succeeded: firing.State == AgentRunState.Completed);
            booked++;

            _logger.LogInformation(
                "Reconciled scheduled job {JobId}: run {RunId} settled {State} at {SettledAt:u} was never booked",
                job.Id, firing.RunId, firing.State, firing.SettledAtUtc);
        }

        if (booked > 0)
            _logger.LogInformation("Booked {Count} unrecorded scheduled firing(s) at startup", booked);

        return booked;
    }
}
