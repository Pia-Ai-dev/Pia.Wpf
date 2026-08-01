using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.Scheduling;

namespace Pia.Services;

public class ScheduledJobService : IScheduledJobService
{
    private const int MaxConsecutiveFailures = 5;

    /// <summary>
    /// Attempts a <see cref="RecurrenceType.Once"/> job gets when its failure was PRE-MODEL. Two, not the
    /// recurring five: the point is to survive a momentary blip — a pinned provider row missing for the
    /// seconds a sync pull takes to re-import it — not to grind. A one-off that cannot resolve a provider
    /// twice, ten minutes apart, is broken in a way a third attempt will not fix.
    /// </summary>
    private const int MaxOncePreModelAttempts = 2;

    /// <summary>
    /// How far forward a re-armed one-off is pushed. The magnitude has exactly ONE job: comfortably clear the
    /// background service's 30 s poll, so the row genuinely leaves the due window instead of re-firing on the
    /// very next tick.
    /// <para>
    /// It is NOT what keeps the retry quiet, and raising it would NOT make the retry noisy: the missed-run
    /// grace is measured as <c>DateTime.Now - job.NextFireAt</c>, and the re-arm MOVES NextFireAt, so the
    /// retry comes due barely late and fires silently at any delay. The one case that still prompts is the
    /// process being closed across the retry instant and restarted more than the grace period later — then the
    /// user is asked about a run whose first attempt they never saw fail.
    /// </para>
    /// </summary>
    private static readonly TimeSpan _oncePreModelRetryDelay = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The one <c>reason</c> value <see cref="MarkRunFailedAsync"/> treats as pre-model: the pinned provider
    /// could not be resolved, so nothing ran — no AgentRuns row was created, no tokens were spent, nothing
    /// was written. Both dispatch legs of <see cref="ScheduledJobBackgroundService"/> pass this exact
    /// constant rather than their own literal, so producer and consumer cannot drift apart.
    /// </summary>
    public const string NoProviderFailureReason = "NoProvider";

    private readonly SqliteContext _context;
    private readonly IRecurrenceCalculator _calculator;
    private readonly ISettingsService _settingsService;
    private readonly SyncDeleteTrackerService _deleteTracker;
    private readonly ILogger<ScheduledJobService> _logger;

    public ScheduledJobService(
        SqliteContext context,
        IRecurrenceCalculator calculator,
        ISettingsService settingsService,
        SyncDeleteTrackerService deleteTracker,
        ILogger<ScheduledJobService> logger)
    {
        _context = context;
        _calculator = calculator;
        _settingsService = settingsService;
        _deleteTracker = deleteTracker;
        _logger = logger;
    }

    public async Task<ScheduledJob> CreateAsync(string name, string query, RecurrenceType recurrence, TimeOnly timeOfDay,
        DayOfWeek? dayOfWeek = null, int? dayOfMonth = null, int? month = null, DateTime? specificDate = null,
        Guid? providerId = null, IReadOnlyCollection<string>? grantedTools = null,
        ScheduledJobKind kind = ScheduledJobKind.Research)
    {
        var now = DateTime.Now;
        var job = new ScheduledJob
        {
            Name = name,
            Query = query,
            Kind = kind,
            Recurrence = recurrence,
            TimeOfDay = timeOfDay,
            DayOfWeek = dayOfWeek,
            DayOfMonth = dayOfMonth,
            Month = month,
            SpecificDate = specificDate,
            GrantedTools = grantedTools?.ToList() ?? [],
            ProviderId = providerId,
            CreatedAt = now,
            UpdatedAt = now,
            OwnerDeviceId = await ResolveLocalDeviceIdAsync()
        };

        job.NextFireAt = ComputeNextFireAt(job, now);

        await InsertAsync(job);

        _logger.LogInformation("Created scheduled job {Id} ({Kind}, {Recurrence})", job.Id, kind, recurrence);
        _logger.SensitiveDebug("Created scheduled job {Id} name: {Name} query: {Query}", job.Id, name, query);
        return job;
    }

    public async Task<IReadOnlyList<ScheduledJob>> GetAllAsync() =>
        await ReadAsync("ORDER BY NextFireAt ASC", _ => { });

    public async Task<IReadOnlyList<ScheduledJob>> GetActiveAsync() =>
        await ReadAsync("WHERE Status = 'Active' ORDER BY NextFireAt ASC", _ => { });

    public async Task<ScheduledJob?> GetAsync(Guid id)
    {
        var list = await ReadAsync("WHERE Id = @Id", cmd => cmd.Parameters.AddWithValue("@Id", id.ToString()));
        return list.FirstOrDefault();
    }

    public async Task<IReadOnlyList<ScheduledJob>> GetDueJobsAsync()
    {
        var localDeviceId = await ResolveLocalDeviceIdAsync();
        var localDeviceParam = localDeviceId.HasValue ? localDeviceId.Value.ToString() : (object)DBNull.Value;
        return await ReadAsync(
            "WHERE NextFireAt <= @Now AND Status = 'Active' AND (OwnerDeviceId IS NULL OR OwnerDeviceId = @LocalDevice) ORDER BY NextFireAt ASC",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Now", DateTime.Now.ToString("O"));
                cmd.Parameters.AddWithValue("@LocalDevice", localDeviceParam);
            });
    }

    public async Task<bool> IsOwnedByThisDeviceAsync(Guid id)
    {
        var job = await GetAsync(id);
        if (job is null) return false;

        // Deliberately the same rule as GetDueJobsAsync' SQL predicate, expressed once more in C# rather than
        // re-queried: a NULL owner is a legacy row that stays device-local to whichever machine made it, so
        // it is runnable here. Anything owned by another device is not, which is what stops a manual run from
        // doing what the scheduler on this device is forbidden to do.
        if (job.OwnerDeviceId is not { } owner) return true;
        return owner == await ResolveLocalDeviceIdAsync();
    }

    public async Task<IReadOnlyList<ScheduledJob>> GetModifiedSinceAsync(DateTime since) =>
        await ReadAsync(
            "WHERE UpdatedAt >= @Since",
            cmd => cmd.Parameters.AddWithValue("@Since", since.ToString("O")));

    public async Task UpdateAsync(Guid id, string? name = null, string? query = null,
        RecurrenceType? recurrence = null, TimeOnly? timeOfDay = null,
        DayOfWeek? dayOfWeek = null, int? dayOfMonth = null, int? month = null,
        Guid? providerId = null, IReadOnlyCollection<string>? grantedTools = null,
        DateTime? specificDate = null, ScheduledJobKind? kind = null)
    {
        var existing = await GetAsync(id) ?? throw new InvalidOperationException($"ScheduledJob {id} not found");

        if (name is not null) existing.Name = name;
        if (query is not null) existing.Query = query;
        if (recurrence is not null) existing.Recurrence = recurrence.Value;
        if (timeOfDay is not null) existing.TimeOfDay = timeOfDay.Value;
        if (dayOfWeek is not null) existing.DayOfWeek = dayOfWeek;
        if (dayOfMonth is not null) existing.DayOfMonth = dayOfMonth;
        if (month is not null) existing.Month = month;
        if (specificDate is not null) existing.SpecificDate = specificDate;
        if (kind is not null) existing.Kind = kind.Value;
        if (grantedTools is not null) existing.GrantedTools = grantedTools.ToList();
        if (providerId is not null) existing.ProviderId = providerId;

        existing.NextFireAt = ComputeNextFireAt(existing, DateTime.Now);
        existing.UpdatedAt = DateTime.Now;

        // W3 follow-up: re-scheduling a SETTLED job re-arms it. Since a fired Once job is left
        // Status='Completed' (deff7d9), an update that recomputed NextFireAt into the future was writing a
        // schedule the due query — `NextFireAt <= @Now AND Status = 'Active'` — can never pick up: the tool
        // reported success, list_scheduled_jobs (GetActiveAsync) no longer showed the row at all, and
        // "move that job to Friday at 10:00" silently did nothing. Before W3 the same edit worked, because a
        // fired Once job stayed Active.
        //
        // Two deliberate narrowings. ONLY Completed is re-armed: Disabled is the user's explicit off switch
        // (DisableAsync/EnableAsync own it) and Failed is a retirement whose ConsecutiveFailures budget only
        // EnableAsync resets, so neither may be flipped on by an unrelated field edit. And only when the
        // recomputed NextFireAt is in the FUTURE.
        //
        // AMENDED by Batch 09: this comment used to explain the future-only rule by saying "UpdateAsync
        // cannot move SpecificDate (there is no parameter for it), so a settled one-off keeps its past
        // instant". THAT CLAUSE IS NOW FALSE — the parameter exists, which is the whole point: a settled
        // one-off whose date is in the past was previously unreachable by any surface (the roadmap's
        // "a settled Once job has almost no re-arm surface"), because the one edit that would revive it was
        // the one edit this method could not express.
        //
        // The future-only rule survives that change UNALTERED, and now carries the weight on its own: moving
        // a one-off to another PAST date must not re-arm it, because the due query would fire it on the very
        // next 30 s tick — an unattended AgentTask run nobody asked for. Supplying a FUTURE date is what
        // re-arms, and that is a thing the user did on purpose.
        if (existing.Status == ScheduledJobStatus.Completed && existing.NextFireAt > DateTime.Now)
            existing.Status = ScheduledJobStatus.Active;

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ScheduledJobs
            SET Name=@Name, Query=@Query, Kind=@Kind, Recurrence=@Recurrence, TimeOfDay=@TimeOfDay,
                DayOfWeek=@DayOfWeek, DayOfMonth=@DayOfMonth, Month=@Month, SpecificDate=@SpecificDate,
                GrantedTools=@GrantedTools, ProviderId=@ProviderId, NextFireAt=@NextFireAt,
                Status=@Status, UpdatedAt=@UpdatedAt
            WHERE Id=@Id
            """;
        command.Parameters.AddWithValue("@Id", existing.Id.ToString());
        command.Parameters.AddWithValue("@Name", existing.Name);
        command.Parameters.AddWithValue("@Query", existing.Query);
        command.Parameters.AddWithValue("@Kind", existing.Kind.ToString());
        command.Parameters.AddWithValue("@SpecificDate",
            existing.SpecificDate.HasValue ? (object)existing.SpecificDate.Value.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("@Recurrence", existing.Recurrence.ToString());
        command.Parameters.AddWithValue("@TimeOfDay", existing.TimeOfDay.ToString("HH:mm"));
        command.Parameters.AddWithValue("@DayOfWeek", existing.DayOfWeek.HasValue ? (object)(int)existing.DayOfWeek.Value : DBNull.Value);
        command.Parameters.AddWithValue("@DayOfMonth", existing.DayOfMonth.HasValue ? (object)existing.DayOfMonth.Value : DBNull.Value);
        command.Parameters.AddWithValue("@Month", existing.Month.HasValue ? (object)existing.Month.Value : DBNull.Value);
        command.Parameters.AddWithValue("@GrantedTools", SerializeGrantedTools(existing.GrantedTools));
        command.Parameters.AddWithValue("@ProviderId", existing.ProviderId.HasValue ? (object)existing.ProviderId.Value.ToString() : DBNull.Value);
        command.Parameters.AddWithValue("@NextFireAt", existing.NextFireAt.ToString("O"));
        command.Parameters.AddWithValue("@Status", existing.Status.ToString());
        command.Parameters.AddWithValue("@UpdatedAt", existing.UpdatedAt.ToString("O"));

        await command.ExecuteNonQueryAsync();
        _logger.LogInformation("Updated scheduled job {Id} ({Status})", id, existing.Status);
    }

    public async Task DeleteAsync(Guid id)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ScheduledJobs WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());
        await command.ExecuteNonQueryAsync();
        _deleteTracker.TrackDeletion("scheduledJobs", id);
        _logger.LogInformation("Deleted scheduled job {Id}", id);
    }

    public async Task DisableAsync(Guid id)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ScheduledJobs SET Status = 'Disabled', UpdatedAt = @UpdatedAt WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());
        command.Parameters.AddWithValue("@UpdatedAt", DateTime.Now.ToString("O"));
        await command.ExecuteNonQueryAsync();
        _logger.LogInformation("Disabled scheduled job {Id}", id);
    }

    public async Task EnableAsync(Guid id)
    {
        var existing = await GetAsync(id) ?? throw new InvalidOperationException($"ScheduledJob {id} not found");
        existing.NextFireAt = ComputeNextFireAt(existing, DateTime.Now);

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ScheduledJobs
            SET Status = 'Active', NextFireAt = @NextFireAt, ConsecutiveFailures = 0, UpdatedAt = @UpdatedAt
            WHERE Id = @Id
            """;
        command.Parameters.AddWithValue("@Id", id.ToString());
        command.Parameters.AddWithValue("@NextFireAt", existing.NextFireAt.ToString("O"));
        command.Parameters.AddWithValue("@UpdatedAt", DateTime.Now.ToString("O"));
        await command.ExecuteNonQueryAsync();
        _logger.LogInformation("Enabled scheduled job {Id}", id);
    }

    public async Task MarkRunCompleteAsync(Guid id, Guid resultEntryId)
    {
        var existing = await GetAsync(id) ?? throw new InvalidOperationException($"ScheduledJob {id} not found");

        // W3: a one-off job has no next occurrence to re-arm into, so its single firing SETTLES the
        // schedule instead of recomputing NextFireAt. The predicate is Recurrence — never Kind (that
        // would retire every Daily AgentTask job after one run) and never "does the recomputed
        // NextFireAt still look past" (that would catch a hand-edited recurring row and MISS the quiet
        // face of the bug: Once with SpecificDate == null falls through to the Daily expression in
        // RecurrenceCalculator, which DOES clamp forward, so such a job never looks past and used to
        // repeat every day forever). Shape mirrors ReminderService.DismissAsync: the branch chooses
        // only the CommandText, shared parameters are bound after it, and it stays ONE statement and
        // ONE round-trip — this call site is not try/catch-wrapped, so extra work here could abort the
        // tick's remaining due jobs (guardrail 1).
        var settleOnce = existing.Recurrence == RecurrenceType.Once;
        DateTime? nextFire = null;

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();

        if (settleOnce)
        {
            // NextFireAt is deliberately NOT rewritten: the row keeps an honest record of when it was
            // meant to fire, and Status is what removes it from GetDueJobsAsync's
            // `NextFireAt <= @Now AND Status = 'Active'`. Clamping it forward instead would turn a
            // diagnosable loop into a Once job that quietly behaves like a Daily one.
            //
            // UpdatedAt IS bumped here, unlike the recurring branch below: this is a Status flip, not
            // device-local execution state. SyncClientService's pull merge keeps the REMOTE row when
            // remote.UpdatedAt >= local.UpdatedAt, and UpsertFromSyncAsync then writes Status back to
            // 'Active' while explicitly leaving NextFireAt (still the past instant) alone — so a
            // settle that does not bump UpdatedAt is reverted by the first pull. Same rationale as
            // MarkRunFailedAsync's existing bump.
            command.CommandText = """
                UPDATE ScheduledJobs
                SET LastFiredAt=@Now, LastResultEntryId=@EntryId, ConsecutiveFailures=0,
                    Status='Completed', UpdatedAt=@UpdatedAt
                WHERE Id=@Id
                """;
            command.Parameters.AddWithValue("@UpdatedAt", DateTime.Now.ToString("O"));
        }
        else
        {
            nextFire = ComputeNextFireAt(existing, DateTime.Now);
            // LastFiredAt / LastResultEntryId / NextFireAt / ConsecutiveFailures are device-local
            // execution state; don't bump UpdatedAt so this doesn't trigger a wasteful re-sync.
            command.CommandText = """
                UPDATE ScheduledJobs
                SET LastFiredAt=@Now, LastResultEntryId=@EntryId, ConsecutiveFailures=0, NextFireAt=@NextFireAt
                WHERE Id=@Id
                """;
            command.Parameters.AddWithValue("@NextFireAt", nextFire.Value.ToString("O"));
        }

        command.Parameters.AddWithValue("@Id", id.ToString());
        command.Parameters.AddWithValue("@Now", DateTime.Now.ToString("O"));
        command.Parameters.AddWithValue("@EntryId", resultEntryId.ToString());
        await command.ExecuteNonQueryAsync();

        // nextFire is non-null exactly when the recurring branch ran; pattern-match rather than
        // re-test settleOnce so the compiler can see it.
        if (nextFire is { } fire)
            _logger.LogInformation("Scheduled job {Id} run completed; next fire {NextFireAt:g}", id, fire);
        else
            _logger.LogInformation("Scheduled job {Id} run completed; one-off settled as Completed, will not fire again", id);
    }

    public async Task MarkRunFailedAsync(Guid id, string reason)
    {
        var existing = await GetAsync(id) ?? throw new InvalidOperationException($"ScheduledJob {id} not found");
        var settleOnce = existing.Recurrence == RecurrenceType.Once;
        var preModel = IsPreModelFailure(reason);
        DateTime? onceRetryAt = null;

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();

        if (settleOnce)
        {
            // W3: a one-off has no future occurrence to retry INTO, so a failure that happened while the run
            // was EXECUTING settles it terminally on the first strike rather than waiting for the 5-strike
            // valve. Leaving it Active would re-run the same past instant on every 30 s tick until the valve
            // trips — five unattended agent runs with real token spend inside 150 s, each serialized behind
            // the tick's run lock, which also starves the other due jobs. Retrying a partially-executed run
            // is not idempotent either: the first attempt may already have written to the vault, so "retry
            // the whole run" can silently duplicate work.
            //
            // (B): the ONE exception is a PRE-MODEL failure — see IsPreModelFailure. There the pinned
            // provider could not be resolved, so no AgentRuns row exists, no tokens were spent and nothing
            // was written; the job's single firing used to be retired for FREE, by a blip that is often gone
            // seconds later (a provider row missing while a sync pull re-imports it). Such a failure re-arms
            // the row _oncePreModelRetryDelay out and settles 'Failed' on attempt MaxOncePreModelAttempts.
            // NextFireAt still stays at its past instant on a SETTLE — the honest record of when the job was
            // meant to fire — so only a re-arm moves it.
            //
            // ONE statement, ONE round-trip, and all three conditional writes chosen by CASE off the SAME
            // atomic `ConsecutiveFailures + 1`: this call site is not try/catch-wrapped in
            // ScheduledJobBackgroundService, so a second statement here could abort the tick's remaining due
            // jobs (guardrail 1), and reading the counter in C# to pick a branch would lose a concurrent
            // increment.
            //
            // Status gains ELSE Status, so a re-arm no longer stamps 'Failed' over a row a direct caller had
            // Disabled (the unconditional 'Failed' this replaces did). The preservation arm is the RETRY arm,
            // mirroring the recurring branch's sub-threshold ELSE Status; a terminal settle writes 'Failed'
            // regardless of prior status, exactly as the recurring branch does at threshold.
            //
            // UpdatedAt is the trap. SyncClientService's pull merge applies the REMOTE row when
            // remote.UpdatedAt >= local.UpdatedAt, and UpsertFromSyncAsync then writes Status back to
            // 'Active' — so a SETTLE that does not bump is reverted by the first pull while testing green
            // locally. It therefore bumps, like the other two settles. The RE-ARM must NOT bump: it changes
            // only NextFireAt and ConsecutiveFailures, which are device-local execution state that is never
            // synced (both are absent from SyncScheduledJob), so bumping would force a pointless push and
            // let a local retry outrank a genuine remote edit.
            command.CommandText = """
                UPDATE ScheduledJobs
                SET LastFiredAt = @Now,
                    ConsecutiveFailures = ConsecutiveFailures + 1,
                    Status = CASE
                        WHEN @PreModel = 1 AND ConsecutiveFailures + 1 < @MaxAttempts THEN Status
                        ELSE 'Failed'
                    END,
                    NextFireAt = CASE
                        WHEN @PreModel = 1 AND ConsecutiveFailures + 1 < @MaxAttempts THEN @RetryAt
                        ELSE NextFireAt
                    END,
                    UpdatedAt = CASE
                        WHEN @PreModel = 1 AND ConsecutiveFailures + 1 < @MaxAttempts THEN UpdatedAt
                        ELSE @UpdatedAt
                    END
                WHERE Id = @Id
                """;
            var retryAt = DateTime.Now.Add(_oncePreModelRetryDelay);
            // Mirrors the CASE above for the log line only; the SQL's own predicate is the authority.
            if (preModel && existing.ConsecutiveFailures + 1 < MaxOncePreModelAttempts)
                onceRetryAt = retryAt;
            command.Parameters.AddWithValue("@PreModel", preModel ? 1 : 0);
            command.Parameters.AddWithValue("@MaxAttempts", MaxOncePreModelAttempts);
            command.Parameters.AddWithValue("@RetryAt", retryAt.ToString("O"));
        }
        else
        {
            var nextFire = ComputeNextFireAt(existing, DateTime.Now);
            // Atomic increment + threshold check, so concurrent callers cannot lose increments
            // or overwrite each other's Status flips. UpdatedAt is bumped unconditionally so a
            // Status flip to 'Failed' propagates to other devices on next sync; the redundant
            // bumps in non-flip cases are cheap.
            command.CommandText = """
                UPDATE ScheduledJobs
                SET LastFiredAt = @Now,
                    ConsecutiveFailures = ConsecutiveFailures + 1,
                    Status = CASE
                        WHEN ConsecutiveFailures + 1 >= @MaxFailures THEN 'Failed'
                        ELSE Status
                    END,
                    NextFireAt = @NextFireAt,
                    UpdatedAt = @UpdatedAt
                WHERE Id = @Id
                """;
            command.Parameters.AddWithValue("@MaxFailures", MaxConsecutiveFailures);
            command.Parameters.AddWithValue("@NextFireAt", nextFire.ToString("O"));
        }

        command.Parameters.AddWithValue("@Id", id.ToString());
        command.Parameters.AddWithValue("@Now", DateTime.Now.ToString("O"));
        command.Parameters.AddWithValue("@UpdatedAt", DateTime.Now.ToString("O"));
        await command.ExecuteNonQueryAsync();

        _logger.LogWarning("Scheduled job {Id} run failed", id);
        if (onceRetryAt is { } retry)
            _logger.LogInformation(
                "Scheduled job {Id} is one-off; pre-model failure {Attempt}/{MaxAttempts}, re-armed for {RetryAt:g}",
                id, existing.ConsecutiveFailures + 1, MaxOncePreModelAttempts, retry);
        else if (settleOnce)
            _logger.LogInformation("Scheduled job {Id} is one-off; retired as Failed, will not fire again", id);
        _logger.SensitiveDebug("Scheduled job {Id} run failed reason: {Reason}", id, reason);
    }

    /// <summary>
    /// True for the one failure <see cref="MarkRunFailedAsync"/> can PROVE happened before the run started
    /// spending anything, and which is therefore safe to retry: <see cref="NoProviderFailureReason"/>, raised
    /// by both dispatch legs when the pinned provider cannot be resolved. The test is exact ordinal equality,
    /// never a substring match, so an unrelated caller string can only be mistaken for pre-model by being
    /// byte-identical to the constant.
    /// <para>
    /// Deliberately narrow, and deliberately NOT extended to the other reasons the background service passes.
    /// A <c>run.State.ToString()</c> and a caught <c>ex.Message</c> both describe a run that already exists:
    /// an initial-planning HTTP failure, for instance, has spent tokens and left an AgentRuns row plus a stub
    /// chat, and its message is indistinguishable from a fault raised mid-act, where a step may already have
    /// written to the vault. Re-firing those is the duplicate-write risk this scoping exists to avoid, so they
    /// settle terminally on the first strike.
    /// </para>
    /// <para>
    /// KNOWN GAP, accepted: <c>IHeadlessRunLauncher.LaunchAsync</c> can also fail genuinely pre-model (its own
    /// provider resolve, the stub-chat save, workspace setup), and that arrives here as a bare message, so
    /// such a one-off still dies on the first strike. Widening needs a reason value the CALLER can vouch for —
    /// never a substring match on provider error text.
    /// </para>
    /// </summary>
    private static bool IsPreModelFailure(string reason) =>
        string.Equals(reason, NoProviderFailureReason, StringComparison.Ordinal);

    public async Task AdvanceMissedRunAsync(Guid id)
    {
        var existing = await GetAsync(id) ?? throw new InvalidOperationException($"ScheduledJob {id} not found");
        var settleOnce = existing.Recurrence == RecurrenceType.Once;
        DateTime? nextFire = null;

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();

        if (settleOnce)
        {
            // W3: this method has two callers and for a one-off BOTH of them are the job's settle —
            // the user-Skip door of the missed-run prompt and the parked-at-budget door. Deliberately
            // one status for both: a job's lifecycle question is "has this firing been settled", not
            // "did the run eventually succeed", so the PARK settle IS the job's settle and a resumed
            // run does not re-settle it (that would double-advance a recurring job, and a resume can
            // happen on a non-owner device). Distinguishing park from Skip would need a parameter here
            // and a matching edit in every hand-written fake, for a nuance no surface renders.
            //
            // NextFireAt stays at its past instant; UpdatedAt is bumped so the Status flip survives the
            // next sync pull (see MarkRunCompleteAsync for the full reason).
            command.CommandText = "UPDATE ScheduledJobs SET Status = 'Completed', UpdatedAt = @UpdatedAt WHERE Id = @Id";
            command.Parameters.AddWithValue("@UpdatedAt", DateTime.Now.ToString("O"));
        }
        else
        {
            nextFire = ComputeNextFireAt(existing, DateTime.Now);
            // NextFireAt is local execution state; don't bump UpdatedAt.
            command.CommandText = "UPDATE ScheduledJobs SET NextFireAt = @NextFireAt WHERE Id = @Id";
            command.Parameters.AddWithValue("@NextFireAt", nextFire.Value.ToString("O"));
        }

        command.Parameters.AddWithValue("@Id", id.ToString());
        await command.ExecuteNonQueryAsync();

        if (nextFire is { } fire)
            _logger.LogInformation("Scheduled job {Id} missed run advanced to {NextFireAt:g}", id, fire);
        else
            _logger.LogInformation("Scheduled job {Id} missed run settled; one-off marked Completed, will not fire again", id);
    }

    public async Task UpsertFromSyncAsync(ScheduledJob job)
    {
        var existing = await GetAsync(job.Id);
        if (existing is null)
        {
            // Imported job hasn't fired locally yet; compute initial NextFireAt from the synced config.
            job.NextFireAt = ComputeNextFireAt(job, DateTime.Now);
            await InsertAsync(job);
            _logger.LogInformation("Imported scheduled job {Id} from sync", job.Id);
            return;
        }

        // Update only the synced config fields; leave execution state (NextFireAt, LastFiredAt,
        // LastResultEntryId, ConsecutiveFailures) untouched — that is each device's own.
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ScheduledJobs
            SET Name=@Name, Query=@Query, Kind=@Kind, GrantedTools=@GrantedTools, ProviderId=@ProviderId,
                Recurrence=@Recurrence, TimeOfDay=@TimeOfDay, DayOfWeek=@DayOfWeek, DayOfMonth=@DayOfMonth,
                Month=@Month, SpecificDate=@SpecificDate, Status=@Status, UpdatedAt=@UpdatedAt,
                OwnerDeviceId=@OwnerDeviceId
            WHERE Id=@Id
            """;
        command.Parameters.AddWithValue("@Id", job.Id.ToString());
        command.Parameters.AddWithValue("@Name", job.Name);
        command.Parameters.AddWithValue("@Query", job.Query);
        command.Parameters.AddWithValue("@Kind", job.Kind.ToString());
        command.Parameters.AddWithValue("@GrantedTools", SerializeGrantedTools(job.GrantedTools));
        command.Parameters.AddWithValue("@ProviderId", job.ProviderId.HasValue ? (object)job.ProviderId.Value.ToString() : DBNull.Value);
        command.Parameters.AddWithValue("@Recurrence", job.Recurrence.ToString());
        command.Parameters.AddWithValue("@TimeOfDay", job.TimeOfDay.ToString("HH:mm"));
        command.Parameters.AddWithValue("@DayOfWeek", job.DayOfWeek.HasValue ? (object)(int)job.DayOfWeek.Value : DBNull.Value);
        command.Parameters.AddWithValue("@DayOfMonth", job.DayOfMonth.HasValue ? (object)job.DayOfMonth.Value : DBNull.Value);
        command.Parameters.AddWithValue("@Month", job.Month.HasValue ? (object)job.Month.Value : DBNull.Value);
        command.Parameters.AddWithValue("@SpecificDate", job.SpecificDate.HasValue ? (object)job.SpecificDate.Value.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("@Status", job.Status.ToString());
        command.Parameters.AddWithValue("@UpdatedAt", job.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("@OwnerDeviceId", job.OwnerDeviceId.HasValue ? (object)job.OwnerDeviceId.Value.ToString() : DBNull.Value);
        await command.ExecuteNonQueryAsync();
        _logger.LogInformation("Updated scheduled job {Id} from sync", job.Id);
    }

    private async Task InsertAsync(ScheduledJob job)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ScheduledJobs
            (Id, Name, Query, Kind, GrantedTools, ProviderId, Recurrence, TimeOfDay,
             DayOfWeek, DayOfMonth, Month, SpecificDate, NextFireAt, Status, CreatedAt, UpdatedAt,
             LastFiredAt, LastResultEntryId, ConsecutiveFailures, OwnerDeviceId)
            VALUES (@Id, @Name, @Query, @Kind, @GrantedTools, @ProviderId, @Recurrence, @TimeOfDay,
                    @DayOfWeek, @DayOfMonth, @Month, @SpecificDate, @NextFireAt, @Status, @CreatedAt, @UpdatedAt,
                    @LastFiredAt, @LastResultEntryId, @ConsecutiveFailures, @OwnerDeviceId)
            """;
        AddJobParameters(command, job);
        await command.ExecuteNonQueryAsync();
    }

    private DateTime ComputeNextFireAt(ScheduledJob job, DateTime from) =>
        _calculator.ComputeNextFireAt(
            job.Recurrence, job.TimeOfDay, job.SpecificDate,
            job.DayOfWeek, job.DayOfMonth, job.Month, from);

    private async Task<Guid?> ResolveLocalDeviceIdAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        if (string.IsNullOrWhiteSpace(settings.SyncDeviceId)) return null;
        return Guid.TryParse(settings.SyncDeviceId, out var id) ? id : null;
    }

    private async Task<IReadOnlyList<ScheduledJob>> ReadAsync(string whereOrOrder, Action<SqliteCommand> bind)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, Name, Query, Kind, GrantedTools, ProviderId, Recurrence, TimeOfDay,
                   DayOfWeek, DayOfMonth, Month, SpecificDate, NextFireAt, Status, CreatedAt, UpdatedAt,
                   LastFiredAt, LastResultEntryId, ConsecutiveFailures, OwnerDeviceId
            FROM ScheduledJobs
            {whereOrOrder}
            """;
        bind(command);

        var list = new List<ScheduledJob>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(MapJob(reader));

        return list.AsReadOnly();
    }

    private static void AddJobParameters(SqliteCommand command, ScheduledJob job)
    {
        command.Parameters.AddWithValue("@Id", job.Id.ToString());
        command.Parameters.AddWithValue("@Name", job.Name);
        command.Parameters.AddWithValue("@Query", job.Query);
        command.Parameters.AddWithValue("@Kind", job.Kind.ToString());
        command.Parameters.AddWithValue("@GrantedTools", SerializeGrantedTools(job.GrantedTools));
        command.Parameters.AddWithValue("@ProviderId", job.ProviderId.HasValue ? (object)job.ProviderId.Value.ToString() : DBNull.Value);
        command.Parameters.AddWithValue("@Recurrence", job.Recurrence.ToString());
        command.Parameters.AddWithValue("@TimeOfDay", job.TimeOfDay.ToString("HH:mm"));
        command.Parameters.AddWithValue("@DayOfWeek", job.DayOfWeek.HasValue ? (object)(int)job.DayOfWeek.Value : DBNull.Value);
        command.Parameters.AddWithValue("@DayOfMonth", job.DayOfMonth.HasValue ? (object)job.DayOfMonth.Value : DBNull.Value);
        command.Parameters.AddWithValue("@Month", job.Month.HasValue ? (object)job.Month.Value : DBNull.Value);
        command.Parameters.AddWithValue("@SpecificDate", job.SpecificDate.HasValue ? (object)job.SpecificDate.Value.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("@NextFireAt", job.NextFireAt.ToString("O"));
        command.Parameters.AddWithValue("@Status", job.Status.ToString());
        command.Parameters.AddWithValue("@CreatedAt", job.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("@UpdatedAt", job.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("@LastFiredAt", job.LastFiredAt.HasValue ? (object)job.LastFiredAt.Value.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("@LastResultEntryId", job.LastResultEntryId.HasValue ? (object)job.LastResultEntryId.Value.ToString() : DBNull.Value);
        command.Parameters.AddWithValue("@ConsecutiveFailures", job.ConsecutiveFailures);
        command.Parameters.AddWithValue("@OwnerDeviceId", job.OwnerDeviceId.HasValue ? (object)job.OwnerDeviceId.Value.ToString() : DBNull.Value);
    }

    private static ScheduledJob MapJob(SqliteDataReader r) => new()
    {
        Id = Guid.Parse(r.GetString(0)),
        Name = r.GetString(1),
        Query = r.GetString(2),
        Kind = Enum.Parse<ScheduledJobKind>(r.GetString(3)),
        GrantedTools = DeserializeGrantedTools(r.IsDBNull(4) ? null : r.GetString(4)),
        ProviderId = r.IsDBNull(5) ? null : Guid.Parse(r.GetString(5)),
        Recurrence = Enum.Parse<RecurrenceType>(r.GetString(6)),
        TimeOfDay = TimeOnly.Parse(r.GetString(7)),
        DayOfWeek = r.IsDBNull(8) ? null : (DayOfWeek)r.GetInt32(8),
        DayOfMonth = r.IsDBNull(9) ? null : r.GetInt32(9),
        Month = r.IsDBNull(10) ? null : r.GetInt32(10),
        SpecificDate = r.IsDBNull(11) ? null : DateTime.Parse(r.GetString(11)),
        NextFireAt = DateTime.Parse(r.GetString(12)),
        Status = Enum.Parse<ScheduledJobStatus>(r.GetString(13)),
        CreatedAt = DateTime.Parse(r.GetString(14)),
        UpdatedAt = DateTime.Parse(r.GetString(15)),
        LastFiredAt = r.IsDBNull(16) ? null : DateTime.Parse(r.GetString(16)),
        LastResultEntryId = r.IsDBNull(17) ? null : Guid.Parse(r.GetString(17)),
        ConsecutiveFailures = r.GetInt32(18),
        OwnerDeviceId = r.IsDBNull(19) ? null : Guid.Parse(r.GetString(19))
    };

    private static string SerializeGrantedTools(IReadOnlyCollection<string> grantedTools) =>
        JsonSerializer.Serialize(grantedTools);

    private static List<string> DeserializeGrantedTools(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
