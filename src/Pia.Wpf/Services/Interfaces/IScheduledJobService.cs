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

    /// <summary>
    /// May THIS device fire <paramref name="id"/>? The same rule <c>GetDueJobsAsync</c> applies in SQL
    /// (<c>OwnerDeviceId IS NULL OR OwnerDeviceId = @LocalDevice</c>), exposed for the UI's manual "run now"
    /// so the two cannot drift: a null owner is a legacy device-local row and stays runnable here, and a row
    /// owned elsewhere is not. False for an id that does not exist — a job nobody can find is one this device
    /// certainly may not fire.
    /// </summary>
    Task<bool> IsOwnedByThisDeviceAsync(Guid id);
    Task<ScheduledJob?> GetAsync(Guid id);
    Task<IReadOnlyList<ScheduledJob>> GetDueJobsAsync();
    Task<IReadOnlyList<ScheduledJob>> GetModifiedSinceAsync(DateTime since);

    /// <summary>
    /// Applies the supplied field edits (null = leave unchanged) and recomputes <c>NextFireAt</c>.
    /// <para>
    /// <b><paramref name="specificDate"/> is what makes the re-arm below reachable (Batch 09).</b> Until it
    /// existed, a settled one-off whose date had passed could not be moved by ANY surface: the re-arm rule
    /// requires a future <c>NextFireAt</c>, and the only edit that could produce one for a <c>Once</c> row was
    /// the one this method could not express. <paramref name="kind"/> is here for the sibling reason — a job
    /// authored as <c>Research</c> could otherwise only become an <c>AgentTask</c> by delete-and-recreate,
    /// which throws away its history and its id.
    /// </para>
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
        Guid? providerId = null, IReadOnlyCollection<string>? grantedTools = null,
        DateTime? specificDate = null, ScheduledJobKind? kind = null);

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
    /// This occurrence has been handed to a runner: move the schedule off it NOW, before the run settles, so
    /// the next 30-second tick cannot see the same occurrence still due and launch a SECOND run of the same
    /// goal. The identical write to <see cref="AdvanceMissedRunAsync"/>, under the name that says why.
    /// <para>
    /// Carries NO job-health signal — not the failure counter, not <see cref="ScheduledJobStatus.Failed"/>.
    /// "The schedule moved on" and "the run succeeded or failed" are two different facts written at two
    /// different times: this one when the run starts, <see cref="MarkRunCompleteAsync"/> /
    /// <see cref="MarkRunFailedAsync"/> when it settles. A <see cref="RecurrenceType.Once"/> job is the one
    /// exception, and only because <c>Status</c> is the sole column that can take it out of the due window: its
    /// firing settles as <see cref="ScheduledJobStatus.Completed"/> here and the outcome may still flip it to
    /// <see cref="ScheduledJobStatus.Failed"/> afterwards.
    /// </para>
    /// </summary>
    Task MarkOccurrenceDispatchedAsync(Guid id);

    /// <summary>
    /// Inserts a new job (no execution state) or updates the synced config of an existing one.
    /// Leaves NextFireAt/LastFiredAt/LastResultEntryId/ConsecutiveFailures untouched on update,
    /// since those are device-local execution state.
    /// </summary>
    Task UpsertFromSyncAsync(ScheduledJob job);
}
