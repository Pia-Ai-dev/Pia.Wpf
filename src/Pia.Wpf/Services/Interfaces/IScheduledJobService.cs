using Pia.Models;

namespace Pia.Services.Interfaces;

public interface IScheduledJobService
{
    Task<ScheduledJob> CreateAsync(string name, string query, RecurrenceType recurrence, TimeOnly timeOfDay,
        DayOfWeek? dayOfWeek = null, int? dayOfMonth = null, int? month = null, DateTime? specificDate = null,
        Guid? providerId = null, IReadOnlyCollection<string>? grantedTools = null,
        ScheduledJobKind kind = ScheduledJobKind.Research,
        // T2-18 quiet mode. It belongs on CREATE and not only on UPDATE because the jobs editor is ONE panel
        // with one checkbox for both: without this, ticking "Quiet" while creating a job produced a job that
        // notifies, with no error and no hint that the choice was dropped.
        bool quietOnSuccess = false,
        // Guid.Empty is normalised to "no pin" here rather than stored, because the editor's default row sends
        // Empty and a create has no earlier value to clear.
        Guid? personaId = null, ReasoningEffort? reasoningEffort = null);

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

    /// <summary>The same rule for a job already in hand, so a list does not re-read every row it is holding.</summary>
    Task<bool> IsOwnedByThisDeviceAsync(ScheduledJob job);

    Task<ScheduledJob?> GetAsync(Guid id);
    Task<IReadOnlyList<ScheduledJob>> GetDueJobsAsync();
    Task<IReadOnlyList<ScheduledJob>> GetModifiedSinceAsync(DateTime since);

    /// <summary>
    /// Applies the supplied field edits (null = leave unchanged) and recomputes <c>NextFireAt</c>.
    /// <para>
    /// Two exceptions to "null = leave unchanged", because a nullable parameter cannot express "clear it".
    /// <paramref name="providerId"/> and <paramref name="personaId"/> take <see cref="Guid.Empty"/> as CLEAR.
    /// <paramref name="reasoningEffort"/> cannot: <c>ReasoningEffort.None</c> means "no reasoning", a real thing
    /// to pin, so clearing it needs <paramref name="clearReasoningEffort"/>.
    /// </para>
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
        DateTime? specificDate = null, ScheduledJobKind? kind = null,
        // T2-18 quiet mode. Trailing, defaulted and NULLABLE like every other member here: null means "leave it
        // as it is", so a caller that does not know about the flag cannot clear it.
        bool? quietOnSuccess = null,
        Guid? personaId = null, ReasoningEffort? reasoningEffort = null, bool clearReasoningEffort = false);

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
    /// Books the OUTCOME of a firing that already happened, without touching the schedule. The mirror image of
    /// <see cref="MarkOccurrenceDispatchedAsync"/>: that one moves the schedule and carries no health signal,
    /// this one carries only the health signal and moves nothing. Success writes <c>LastFiredAt</c>,
    /// <c>LastResultEntryId</c> (only when <paramref name="resultEntryId"/> is non-null — a COALESCE, so a
    /// booking with no chat to point at never erases an earlier one) and clears <c>ConsecutiveFailures</c>;
    /// failure writes <c>LastFiredAt</c> and increments the counter.
    /// <para>
    /// Writes NEITHER <c>Status</c>, NOR <c>NextFireAt</c>, NOR <c>UpdatedAt</c>. All four columns it does write
    /// are device-local execution state absent from <c>SyncScheduledJob</c>, so there is nothing to push and
    /// bumping <c>UpdatedAt</c> would only make a local booking outrank a genuine remote edit on the next pull.
    /// </para>
    /// <para>
    /// <b>The <c>ConsecutiveFailures</c> bump is a RECORD, not an action.</b> Because this member never writes
    /// <c>Status</c>, no amount of booking through it can retire a job: the 5-strike valve lives in
    /// <see cref="MarkRunFailedAsync"/> and only that method can reach
    /// <see cref="ScheduledJobStatus.Failed"/>. The counter here is the health signal a support log and the
    /// settings list read, and clearing it on a success is what stops an old crash from shadowing a job that
    /// now works.
    /// </para>
    /// <para>
    /// <paramref name="firedAt"/> is a PARAMETER, unlike the three sibling writes which each derive their own
    /// <c>DateTime.Now</c>, because the callers book a firing that settled in the PAST — a startup reconcile of
    /// a run the process died in the middle of, and a resumed run whose settle nobody was awaiting. Stamping
    /// "now" would be a lie, and a lie that is accidentally self-idempotent: the reconcile decides whether a
    /// firing is already booked by comparing this column against the run's settle time, so a startup-time stamp
    /// would look newer than every past run and quietly stop booking anything. Expected in the column's own
    /// convention — LOCAL time, like every other writer of <c>LastFiredAt</c>.
    /// </para>
    /// <para>
    /// Deliberately NOT served by reusing <see cref="MarkRunFailedAsync"/>/<see cref="MarkRunCompleteAsync"/>.
    /// On a one-off those would overwrite the <c>Status='Completed'</c> that DISPATCH wrote with
    /// <c>'Failed'</c> and burn a strike on a job that will never fire again; on a recurring job
    /// <see cref="MarkRunCompleteAsync"/> recomputes <c>NextFireAt</c> from <c>DateTime.Now</c>, which for a
    /// run that outlived its own next occurrence SKIPS that occurrence. (Note the second problem exists on the
    /// live bookkeeping path too and is out of scope here.)
    /// </para>
    /// </summary>
    Task MarkFiringOutcomeAsync(Guid id, DateTime firedAt, Guid? resultEntryId, bool succeeded);

    /// <summary>
    /// Pins the day a recurring job fires on, for rows that never had one. Returns the number pinned.
    /// <para>
    /// Until the editor gained day pickers it never sent <c>DayOfWeek</c>/<c>DayOfMonth</c>/<c>Month</c>, and
    /// <see cref="Scheduling.RecurrenceCalculator"/> substitutes the CURRENT day when the field is null. Since
    /// every recompute passes <c>DateTime.Now</c>, one late or skipped run permanently relocated the job: a
    /// Monday briefing that ran on a Wednesday next fired the following Wednesday. The stored
    /// <c>NextFireAt</c> is the only record of the day such a job currently fires on, so it is the source here.
    /// </para>
    /// <para>
    /// Owner device only, the same predicate <c>GetDueJobsAsync</c> uses — <c>NextFireAt</c> is device-local
    /// and never synced, so a peer's copy carries no firing history and two devices would pin different days.
    /// Idempotent by its WHERE clause rather than by a version flag: it only touches columns that are null.
    /// </para>
    /// </summary>
    Task<int> BackfillRecurrenceDaysAsync();

    /// <summary>
    /// Inserts a new job (no execution state) or updates the synced config of an existing one.
    /// Leaves NextFireAt/LastFiredAt/LastResultEntryId/ConsecutiveFailures untouched on update,
    /// since those are device-local execution state.
    /// </summary>
    Task UpsertFromSyncAsync(ScheduledJob job);
}
