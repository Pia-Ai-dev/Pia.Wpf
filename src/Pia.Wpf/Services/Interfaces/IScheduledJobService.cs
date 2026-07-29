using Pia.Models;

namespace Pia.Services.Interfaces;

public interface IScheduledJobService
{
    Task<ScheduledJob> CreateAsync(string name, string query, RecurrenceType recurrence, TimeOnly timeOfDay,
        DayOfWeek? dayOfWeek = null, int? dayOfMonth = null, int? month = null, DateTime? specificDate = null,
        Guid? providerId = null, IReadOnlyCollection<string>? grantedTools = null,
        ScheduledJobKind kind = ScheduledJobKind.Research);

    Task<IReadOnlyList<ScheduledJob>> GetAllAsync();
    Task<IReadOnlyList<ScheduledJob>> GetActiveAsync();
    Task<ScheduledJob?> GetAsync(Guid id);
    Task<IReadOnlyList<ScheduledJob>> GetDueJobsAsync();
    Task<IReadOnlyList<ScheduledJob>> GetModifiedSinceAsync(DateTime since);

    /// <summary>
    /// Applies the supplied field edits (null = leave unchanged) and recomputes <c>NextFireAt</c>.
    /// <para>
    /// Also RE-ARMS a job that had settled: a <see cref="ScheduledJobStatus.Completed"/> row whose recomputed
    /// fire time lands in the future goes back to <see cref="ScheduledJobStatus.Active"/>, because otherwise a
    /// fired one-off can never be re-scheduled — no caller exposes <c>EnableAsync</c>. Deliberately does NOT
    /// touch <see cref="ScheduledJobStatus.Disabled"/> (the user's off switch) or
    /// <see cref="ScheduledJobStatus.Failed"/> (a retirement whose failure count only <c>EnableAsync</c>
    /// clears — and by the time <see cref="MarkRunFailedAsync"/> puts a one-off THERE it has already used up
    /// its pre-model retries, so a row that reached Failed really is done), and does not re-arm a settled row
    /// whose fire time is still in the past.
    /// </para>
    /// </summary>
    Task UpdateAsync(Guid id, string? name = null, string? query = null,
        RecurrenceType? recurrence = null, TimeOnly? timeOfDay = null,
        DayOfWeek? dayOfWeek = null, int? dayOfMonth = null, int? month = null,
        Guid? providerId = null, IReadOnlyCollection<string>? grantedTools = null);

    Task DeleteAsync(Guid id);

    Task DisableAsync(Guid id);
    Task EnableAsync(Guid id);

    Task MarkRunCompleteAsync(Guid id, Guid resultEntryId);

    /// <summary>
    /// Records a failed firing. A recurring job re-arms into its next occurrence and only retires as
    /// <see cref="ScheduledJobStatus.Failed"/> once it has burned its 5-strike budget.
    /// <para>
    /// A <see cref="RecurrenceType.Once"/> job has no next occurrence, so it retires on the FIRST failure —
    /// with one exception. A PRE-MODEL failure (the pinned provider could not be resolved, so no run row
    /// exists, no tokens were spent and nothing was written) re-arms the row a few minutes out for ONE more
    /// attempt and retires on the second. Anything that failed once the run was EXECUTING retires
    /// immediately, on purpose: retrying a partially-executed run is not idempotent, since the first attempt
    /// may already have written to the vault. The discriminator is <c>reason</c>; the implementation names
    /// the exact value it accepts and why the boundary sits there.
    /// </para>
    /// </summary>
    Task MarkRunFailedAsync(Guid id, string reason);

    /// <summary>
    /// Advances <c>NextFireAt</c> for a job whose missed-run prompt was answered "Skip"
    /// without touching the failure counter. Skipping a missed run is a user choice,
    /// not a job-health signal.
    /// </summary>
    Task AdvanceMissedRunAsync(Guid id);

    /// <summary>
    /// Inserts a new job (no execution state) or updates the synced config of an existing one.
    /// Leaves NextFireAt/LastFiredAt/LastResultEntryId/ConsecutiveFailures untouched on update,
    /// since those are device-local execution state.
    /// </summary>
    Task UpsertFromSyncAsync(ScheduledJob job);
}
