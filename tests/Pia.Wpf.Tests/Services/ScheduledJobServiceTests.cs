using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Exceptions;
using Pia.Services.Interfaces;
using Pia.Services.Scheduling;
using Xunit;

namespace Pia.Tests.Services;

public class ScheduledJobServiceTests : IDisposable
{
    private readonly SqliteContext _ctx;
    private readonly ScheduledJobService _service;
    private readonly string _tmpDir;

    public ScheduledJobServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
        var settings = new TestSettingsService();
        var deleteTracker = new SyncDeleteTrackerService(_tmpDir, NullLogger<SyncDeleteTrackerService>.Instance);
        _service = new ScheduledJobService(_ctx, new RecurrenceCalculator(), settings, deleteTracker, NullLogger<ScheduledJobService>.Instance);
    }

    private sealed class TestSettingsService : ISettingsService
    {
#pragma warning disable CS0067
        public event EventHandler<AppSettings>? SettingsChanged;
#pragma warning restore CS0067
        public Task<AppSettings> GetSettingsAsync() => Task.FromResult(new AppSettings());
        public Task SaveSettingsAsync(AppSettings settings) => Task.CompletedTask;
        public Task SaveDraftAsync(string? draftText) => Task.CompletedTask;
        public Task<string?> GetDraftAsync() => Task.FromResult<string?>(null);
    }

    [Fact]
    public async Task CreateAsync_PersistsAndComputesNextFireAt()
    {
        var job = await _service.CreateAsync("TEST_TeslaBriefing", "Latest Tesla news", RecurrenceType.Daily, new TimeOnly(8, 0));
        Assert.NotEqual(Guid.Empty, job.Id);
        Assert.True(job.NextFireAt > DateTime.Now);

        var fetched = await _service.GetAsync(job.Id);
        Assert.NotNull(fetched);
        Assert.Equal("TEST_TeslaBriefing", fetched!.Name);
    }

    [Fact]
    public async Task GetDueJobsAsync_ReturnsOnlyOverdueAndActive()
    {
        var due = await _service.CreateAsync("TEST_Due", "q", RecurrenceType.Daily, new TimeOnly(0, 0));
        await ForceNextFireAtAsync(due.Id, DateTime.Now.AddMinutes(-5));

        var disabled = await _service.CreateAsync("TEST_Disabled", "q", RecurrenceType.Daily, new TimeOnly(0, 0));
        await _service.DisableAsync(disabled.Id);
        await ForceNextFireAtAsync(disabled.Id, DateTime.Now.AddMinutes(-5));

        var dueList = await _service.GetDueJobsAsync();
        Assert.Contains(dueList, j => j.Id == due.Id);
        Assert.DoesNotContain(dueList, j => j.Id == disabled.Id);
    }

    [Fact]
    public async Task MarkRunFailedAsync_FifthFailure_DisablesJob()
    {
        var job = await _service.CreateAsync("TEST_FlakeJob", "q", RecurrenceType.Daily, new TimeOnly(0, 0));
        for (var i = 0; i < 5; i++)
            await _service.MarkRunFailedAsync(job.Id, "test");

        var fetched = await _service.GetAsync(job.Id);
        Assert.Equal(ScheduledJobStatus.Failed, fetched!.Status);
        Assert.Equal(5, fetched.ConsecutiveFailures);
    }

    [Fact]
    public async Task MarkRunCompleteAsync_ResetsFailureCount()
    {
        var job = await _service.CreateAsync("TEST_Recovers", "q", RecurrenceType.Daily, new TimeOnly(0, 0));
        await _service.MarkRunFailedAsync(job.Id, "a");
        await _service.MarkRunFailedAsync(job.Id, "b");
        await _service.MarkRunCompleteAsync(job.Id, Guid.NewGuid());

        var fetched = await _service.GetAsync(job.Id);
        Assert.Equal(0, fetched!.ConsecutiveFailures);
        Assert.NotNull(fetched.LastFiredAt);
        Assert.NotNull(fetched.LastResultEntryId);
    }

    [Fact]
    public async Task GetDueJobsAsync_ExcludesJobsOwnedByOtherDevice()
    {
        // Create a job locally with no settings (OwnerDeviceId == null) — i.e. legacy/local-only.
        // Then re-stamp its OwnerDeviceId to a different device.
        var job = await _service.CreateAsync("TEST_OtherDevice", "q", RecurrenceType.Daily, new TimeOnly(0, 0));
        await ForceNextFireAtAsync(job.Id, DateTime.Now.AddMinutes(-5));
        await SetOwnerDeviceIdAsync(job.Id, Guid.NewGuid());

        var due = await _service.GetDueJobsAsync();
        Assert.DoesNotContain(due, j => j.Id == job.Id);
    }

    [Fact]
    public async Task GetDueJobsAsync_IncludesJobsWithNullOwner()
    {
        var job = await _service.CreateAsync("TEST_LegacyOwner", "q", RecurrenceType.Daily, new TimeOnly(0, 0));
        await ForceNextFireAtAsync(job.Id, DateTime.Now.AddMinutes(-5));
        await SetOwnerDeviceIdAsync(job.Id, null); // legacy row from before sync

        var due = await _service.GetDueJobsAsync();
        Assert.Contains(due, j => j.Id == job.Id);
    }

    // A fired one-off must settle, not re-arm. Asserted against the real service and calculator rather than
    // the hand-written fakes in ScheduledJobBackgroundServiceTests, where such a test passes on unfixed code.

    [Fact]
    public async Task MarkRunCompleteAsync_OnceJob_SettlesAndLeavesNextFireAtAlone()
    {
        var job = await _service.CreateAsync("TEST_OnceComplete", "q", RecurrenceType.Once, new TimeOnly(9, 0),
            specificDate: DateTime.Now.Date.AddDays(-1));
        var plantedFire = DateTime.Now.AddMinutes(-5);
        var plantedUpdate = DateTime.Now.AddHours(-3);
        await ForceNextFireAtAsync(job.Id, plantedFire);
        await ForceUpdatedAtAsync(job.Id, plantedUpdate);

        await _service.MarkRunCompleteAsync(job.Id, Guid.NewGuid());

        var after = await _service.GetAsync(job.Id);
        Assert.NotNull(after);
        Assert.Equal(ScheduledJobStatus.Completed, after!.Status);
        // NextFireAt is deliberately NOT rewritten: Status is what removes the row from the due query,
        // and the past instant stays as an honest record of when the job was meant to fire.
        Assert.Equal(plantedFire, after.NextFireAt, TimeSpan.FromSeconds(1));
        // A Status flip that does not bump UpdatedAt is reverted by the next sync pull.
        Assert.True(after.UpdatedAt > plantedUpdate, "terminal Status flip must bump UpdatedAt");
        Assert.NotNull(after.LastFiredAt);
        Assert.NotNull(after.LastResultEntryId);

        Assert.DoesNotContain(await _service.GetDueJobsAsync(), j => j.Id == job.Id);
    }

    [Fact]
    public async Task AdvanceMissedRunAsync_OnceJob_SettlesAndLeavesNextFireAtAlone()
    {
        // Covers BOTH callers: the user-Skip door of the missed-run prompt and the parked-at-budget door.
        var job = await _service.CreateAsync("TEST_OnceAdvance", "q", RecurrenceType.Once, new TimeOnly(9, 0),
            specificDate: DateTime.Now.Date.AddDays(-1));
        var plantedFire = DateTime.Now.AddMinutes(-20);
        var plantedUpdate = DateTime.Now.AddHours(-3);
        await ForceNextFireAtAsync(job.Id, plantedFire);
        await ForceUpdatedAtAsync(job.Id, plantedUpdate);

        await _service.AdvanceMissedRunAsync(job.Id);

        var after = await _service.GetAsync(job.Id);
        Assert.Equal(ScheduledJobStatus.Completed, after!.Status);
        Assert.Equal(plantedFire, after.NextFireAt, TimeSpan.FromSeconds(1));
        Assert.True(after.UpdatedAt > plantedUpdate, "terminal Status flip must bump UpdatedAt");
        // A park/Skip is not a job-health signal, so the failure counter stays untouched.
        Assert.Equal(0, after.ConsecutiveFailures);

        Assert.DoesNotContain(await _service.GetDueJobsAsync(), j => j.Id == job.Id);
    }

    /// <summary>A tick dispatches without awaiting the run, so the schedule must leave the due window at dispatch
    /// — and for a one-off only <c>Status</c> can do that, since <c>NextFireAt</c> keeps its past instant.</summary>
    [Fact]
    public async Task MarkOccurrenceDispatchedAsync_OnceJob_LeavesTheDueWindowByStatus_NotByMovingNextFireAt()
    {
        var job = await _service.CreateAsync("TEST_OnceDispatched", "q", RecurrenceType.Once, new TimeOnly(9, 0),
            specificDate: DateTime.Now.Date.AddDays(-1));
        var plantedFire = DateTime.Now.AddMinutes(-2);
        var plantedUpdate = DateTime.Now.AddHours(-3);
        await ForceNextFireAtAsync(job.Id, plantedFire);
        await ForceUpdatedAtAsync(job.Id, plantedUpdate);
        Assert.Contains(await _service.GetDueJobsAsync(), j => j.Id == job.Id); // premise: it really was due

        await _service.MarkOccurrenceDispatchedAsync(job.Id);

        var after = await _service.GetAsync(job.Id);
        Assert.Equal(ScheduledJobStatus.Completed, after!.Status);
        Assert.Equal(plantedFire, after.NextFireAt, TimeSpan.FromSeconds(1));
        Assert.True(after.UpdatedAt > plantedUpdate, "the Status flip must bump UpdatedAt or a sync pull reverts it");
        // Dispatching is not a job-health signal: the outcome bookkeeping still owns the counter.
        Assert.Equal(0, after.ConsecutiveFailures);

        // The dispatch write deliberately leaves these null: the outcome writers fill them in, and stamping them
        // here would wrongly record a firing for the user-Skip door, which never fired.
        Assert.Null(after.LastFiredAt);
        Assert.Null(after.LastResultEntryId);

        Assert.DoesNotContain(await _service.GetDueJobsAsync(), j => j.Id == job.Id);
    }

    /// <summary><c>UpdatedAt</c> deliberately does not bump: <c>NextFireAt</c> is device-local execution state and
    /// bumping would force a pointless sync push.</summary>
    [Fact]
    public async Task MarkOccurrenceDispatchedAsync_RecurringJob_ReArmsAndTouchesNoHealthColumn()
    {
        var job = await _service.CreateAsync("TEST_DailyDispatched", "q", RecurrenceType.Daily, new TimeOnly(9, 0));
        var plantedUpdate = DateTime.Now.AddHours(-3);
        await ForceNextFireAtAsync(job.Id, DateTime.Now.AddMinutes(-2));
        await ForceUpdatedAtAsync(job.Id, plantedUpdate);
        Assert.Contains(await _service.GetDueJobsAsync(), j => j.Id == job.Id);

        await _service.MarkOccurrenceDispatchedAsync(job.Id);

        var after = await _service.GetAsync(job.Id);
        Assert.True(after!.NextFireAt > DateTime.Now, "a recurring job must re-arm into its next occurrence");
        Assert.Equal(ScheduledJobStatus.Active, after.Status);
        Assert.Equal(0, after.ConsecutiveFailures);
        Assert.Null(after.LastFiredAt);   // the outcome writers own this, not the dispatch
        Assert.Equal(plantedUpdate, after.UpdatedAt, TimeSpan.FromSeconds(1));

        Assert.DoesNotContain(await _service.GetDueJobsAsync(), j => j.Id == job.Id);
    }

    /// <summary>Booking a failure onto an already-settled one-off must record the firing and nothing else: every
    /// existing outcome writer would stamp <c>'Failed'</c> and burn a strike on a job that cannot fire again.</summary>
    [Fact]
    public async Task MarkFiringOutcomeAsync_BooksTheHealthColumns_AndTouchesNeitherStatusNorNextFireAt()
    {
        var job = await _service.CreateAsync("TEST_OnceReconciled", "q", RecurrenceType.Once, new TimeOnly(9, 0),
            specificDate: DateTime.Now.Date.AddDays(-1));
        var plantedFire = DateTime.Now.AddMinutes(-30);
        await ForceNextFireAtAsync(job.Id, plantedFire);
        await _service.MarkOccurrenceDispatchedAsync(job.Id);

        var dispatched = await _service.GetAsync(job.Id);
        Assert.Equal(ScheduledJobStatus.Completed, dispatched!.Status);   // premise: dispatch settled the one-off
        Assert.Null(dispatched.LastFiredAt);                             // premise: it never recorded a firing
        var updatedAtAfterDispatch = dispatched.UpdatedAt;

        // A past instant, not "now": an implementation that stamped DateTime.Now would be self-idempotent and
        // stop the reconcile booking anything at all.
        var settledAt = DateTime.Now.AddMinutes(-20);
        await _service.MarkFiringOutcomeAsync(job.Id, settledAt, resultEntryId: null, succeeded: false);

        var after = await _service.GetAsync(job.Id);
        Assert.Equal(settledAt, after!.LastFiredAt!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(1, after.ConsecutiveFailures);
        // The three columns this write must not touch. Status is the one that matters most: a failure booking
        // that flipped it to 'Failed' would retire a job through a path that has no 5-strike valve.
        Assert.Equal(ScheduledJobStatus.Completed, after.Status);
        Assert.Equal(plantedFire, after.NextFireAt, TimeSpan.FromSeconds(1));
        Assert.Equal(updatedAtAfterDispatch, after.UpdatedAt, TimeSpan.FromMilliseconds(1));
    }

    /// <summary>Run on a row that already carries a failure count, so clearing the counter is non-vacuous; the
    /// re-armed <c>NextFireAt</c> proves the booking did not recompute the schedule.</summary>
    [Fact]
    public async Task MarkFiringOutcomeAsync_Success_RecordsTheChat_AndClearsTheFailureCounter()
    {
        var job = await _service.CreateAsync("TEST_DailyReconciled", "q", RecurrenceType.Daily, new TimeOnly(9, 0));
        await ForceNextFireAtAsync(job.Id, DateTime.Now.AddMinutes(-2));
        await _service.MarkRunFailedAsync(job.Id, "an earlier occurrence failed");
        var armed = await _service.GetAsync(job.Id);
        Assert.Equal(1, armed!.ConsecutiveFailures);   // premise: there is a counter to clear
        var reArmedFire = armed.NextFireAt;

        var chatId = Guid.NewGuid();
        await _service.MarkFiringOutcomeAsync(job.Id, DateTime.Now.AddMinutes(-1), chatId, succeeded: true);

        var after = await _service.GetAsync(job.Id);
        Assert.Equal(chatId, after!.LastResultEntryId);
        Assert.Equal(0, after.ConsecutiveFailures);
        Assert.Equal(reArmedFire, after.NextFireAt, TimeSpan.FromSeconds(1));

        // COALESCE, not assignment: a later booking with no chat must not erase the one above. This is the leg
        // that reds if the SQL is simplified to `LastResultEntryId=@EntryId`.
        await _service.MarkFiringOutcomeAsync(job.Id, DateTime.Now, resultEntryId: null, succeeded: true);
        Assert.Equal(chatId, (await _service.GetAsync(job.Id))!.LastResultEntryId);
    }

    [Fact]
    public async Task MarkRunFailedAsync_OnceJob_PostModelFailure_FailsOnFirstFailure()
    {
        // A one-off has no future occurrence to retry into and a started run's retry is not idempotent, so any
        // failure but the pre-model reason retires the job on the first strike rather than the 5-strike valve.
        var job = await _service.CreateAsync("TEST_OnceFails", "q", RecurrenceType.Once, new TimeOnly(9, 0),
            specificDate: DateTime.Now.Date.AddDays(-1));
        var plantedFire = DateTime.Now.AddMinutes(-5);
        await ForceNextFireAtAsync(job.Id, plantedFire);

        await _service.MarkRunFailedAsync(job.Id, "provider blew up");

        var after = await _service.GetAsync(job.Id);
        Assert.Equal(ScheduledJobStatus.Failed, after!.Status);
        Assert.Equal(1, after.ConsecutiveFailures);
        Assert.Equal(plantedFire, after.NextFireAt, TimeSpan.FromSeconds(1));

        Assert.DoesNotContain(await _service.GetDueJobsAsync(), j => j.Id == job.Id);
    }

    [Fact]
    public async Task MarkRunFailedAsync_OnceJob_PreModelFailure_ReArmsInsteadOfRetiring()
    {
        // NoProvider costs nothing — no run row, no tokens, no writes — and is often momentary, so it must not
        // spend a one-off's only firing.
        var job = await _service.CreateAsync("TEST_OncePreModel", "q", RecurrenceType.Once, new TimeOnly(9, 0),
            specificDate: DateTime.Now.Date.AddDays(-1));
        await ForceNextFireAtAsync(job.Id, DateTime.Now.AddMinutes(-5));
        var plantedUpdate = DateTime.Now.AddHours(-3);
        await ForceUpdatedAtAsync(job.Id, plantedUpdate);

        await _service.MarkRunFailedAsync(job.Id, ScheduledJobService.NoProviderFailureReason);

        var after = await _service.GetAsync(job.Id);
        Assert.Equal(ScheduledJobStatus.Active, after!.Status);
        Assert.Equal(1, after.ConsecutiveFailures);
        Assert.True(after.NextFireAt > DateTime.Now, "a re-armed one-off must have a FUTURE fire time");
        // Far enough forward that the row genuinely leaves the 30 s due window rather than re-running at once.
        Assert.True(after.NextFireAt > DateTime.Now.AddMinutes(5), "the retry must not fire on the next tick");
        // NextFireAt/ConsecutiveFailures are device-local and unsynced, so the re-arm must not bump UpdatedAt —
        // that would let a local retry outrank a genuine remote edit in the merge.
        Assert.Equal(plantedUpdate, after.UpdatedAt, TimeSpan.FromSeconds(1));
        Assert.DoesNotContain(await _service.GetDueJobsAsync(), j => j.Id == job.Id);
        // Still Active, so the row is still listed and still owns a scheduled firing.
        Assert.Contains(await _service.GetActiveAsync(), j => j.Id == job.Id);
    }

    /// <summary>
    /// The gap IsPreModelFailure's own doc comment recorded: a launch that threw before the run existed
    /// arrived as a bare message and retired a one-off on the first strike. A descriptor its raiser vouched
    /// for now re-arms it, without the classifier ever matching on message text.
    /// </summary>
    [Fact]
    public async Task MarkRunFailedAsync_OnceJob_VouchedForPreModelDescriptor_ReArmsThoughTheReasonIsAMessage()
    {
        var job = await _service.CreateAsync("TEST_OnceVouched", "q", RecurrenceType.Once, new TimeOnly(9, 0),
            specificDate: DateTime.Now.Date.AddDays(-1));
        await ForceNextFireAtAsync(job.Id, DateTime.Now.AddMinutes(-5));

        await _service.MarkRunFailedAsync(
            job.Id,
            "No provider configured for a headless agent run.",
            FailureMapper.ForException(new PreModelLaunchException("no provider")));

        var after = await _service.GetAsync(job.Id);
        Assert.Equal(ScheduledJobStatus.Active, after!.Status);
        Assert.True(after.NextFireAt > DateTime.Now.AddMinutes(5), "the one-off must have been re-armed");
    }

    /// <summary>Widening, not loosening: an ordinary fault still settles terminally on the first strike.</summary>
    [Fact]
    public async Task MarkRunFailedAsync_OnceJob_MidRunFaultDescriptor_StillRetiresOnTheFirstStrike()
    {
        var job = await _service.CreateAsync("TEST_OnceMidRun", "q", RecurrenceType.Once, new TimeOnly(9, 0),
            specificDate: DateTime.Now.Date.AddDays(-1));
        await ForceNextFireAtAsync(job.Id, DateTime.Now.AddMinutes(-5));

        // A 503 is transient by hermes's meaning of "retryable" and emphatically not safe to re-dispatch:
        // the run may already have written to the vault.
        await _service.MarkRunFailedAsync(
            job.Id, "upstream 503", FailureMapper.ForException(new HttpRequestException("503")));

        var after = await _service.GetAsync(job.Id);
        Assert.Equal(ScheduledJobStatus.Failed, after!.Status);
    }

    [Fact]
    public async Task MarkRunFailedAsync_OnceJob_SecondPreModelFailure_SettlesFailed()
    {
        // The cap: retry once, then stop — a one-off that cannot resolve a provider twice will not on a third
        // unattended attempt either.
        var job = await _service.CreateAsync("TEST_OncePreModelTwice", "q", RecurrenceType.Once, new TimeOnly(9, 0),
            specificDate: DateTime.Now.Date.AddDays(-1));
        await ForceNextFireAtAsync(job.Id, DateTime.Now.AddMinutes(-5));

        await _service.MarkRunFailedAsync(job.Id, ScheduledJobService.NoProviderFailureReason);
        Assert.Equal(ScheduledJobStatus.Active, (await _service.GetAsync(job.Id))!.Status);

        await _service.MarkRunFailedAsync(job.Id, ScheduledJobService.NoProviderFailureReason);

        var after = await _service.GetAsync(job.Id);
        Assert.Equal(ScheduledJobStatus.Failed, after!.Status);
        Assert.Equal(2, after.ConsecutiveFailures);
        Assert.DoesNotContain(await _service.GetDueJobsAsync(), j => j.Id == job.Id);
        Assert.DoesNotContain(await _service.GetActiveAsync(), j => j.Id == job.Id);
    }

    [Fact]
    public async Task MarkRunFailedAsync_OnceJob_PreModelRetryThenSettle_SurvivesASyncPull()
    {
        // The pull merge applies the remote row when remote.UpdatedAt >= local.UpdatedAt and writes Status back
        // to 'Active', so a settle that does not bump looks green locally and is reverted by the first pull.
        var job = await _service.CreateAsync("TEST_OnceFailSync", "q", RecurrenceType.Once, new TimeOnly(9, 0),
            specificDate: DateTime.Now.Date.AddDays(-1));
        var remote = (await _service.GetAsync(job.Id))!;   // stands in for the row the server still holds
        var plantedUpdate = DateTime.Now.AddHours(-3);
        await ForceUpdatedAtAsync(job.Id, plantedUpdate);
        remote.UpdatedAt = plantedUpdate;

        // Attempt 1 re-arms: local execution state only, so UpdatedAt must not move.
        await _service.MarkRunFailedAsync(job.Id, ScheduledJobService.NoProviderFailureReason);
        var reArmed = (await _service.GetAsync(job.Id))!;
        Assert.Equal(plantedUpdate, reArmed.UpdatedAt, TimeSpan.FromSeconds(1));

        // Attempt 2 settles. The bump makes the local row strictly newer, so the merge predicate
        // `remote.UpdatedAt >= local.UpdatedAt` is false and the pull is skipped.
        await _service.MarkRunFailedAsync(job.Id, ScheduledJobService.NoProviderFailureReason);
        var settled = (await _service.GetAsync(job.Id))!;
        Assert.Equal(ScheduledJobStatus.Failed, settled.Status);
        Assert.False(remote.UpdatedAt.ToUniversalTime() >= settled.UpdatedAt.ToUniversalTime(),
            "a terminal settle must bump UpdatedAt or the next pull reverts it to Active");

        // What the revert would do if the predicate held. UpsertFromSyncAsync leaves ConsecutiveFailures alone
        // (absent from SyncScheduledJob), so a pull does not reset the attempt budget.
        remote.Status = ScheduledJobStatus.Active;
        await _service.UpsertFromSyncAsync(remote);
        var pulled = (await _service.GetAsync(job.Id))!;
        Assert.Equal(ScheduledJobStatus.Active, pulled.Status);
        Assert.Equal(2, pulled.ConsecutiveFailures);
    }

    [Fact]
    public async Task MarkRunFailedAsync_RecurringJob_PreModelFailure_StillUsesTheFiveStrikeBudget()
    {
        // A recurring job already has a next occurrence to retry into, so the pre-model reason must neither
        // shorten nor lengthen its 5-strike budget.
        var job = await _service.CreateAsync("TEST_DailyPreModel", "q", RecurrenceType.Daily, new TimeOnly(9, 0));
        await ForceNextFireAtAsync(job.Id, DateTime.Now.AddMinutes(-5));

        for (var i = 0; i < 4; i++)
            await _service.MarkRunFailedAsync(job.Id, ScheduledJobService.NoProviderFailureReason);

        var after = await _service.GetAsync(job.Id);
        Assert.Equal(ScheduledJobStatus.Active, after!.Status);
        Assert.Equal(4, after.ConsecutiveFailures);
        // Only that the row re-armed into the future — deliberately no magnitude assertion, because a Daily
        // 09:00 job's next occurrence is minutes away if the suite runs at 08:45 and ~24 h away at 09:05.
        Assert.True(after.NextFireAt > DateTime.Now, "a recurring job must still re-arm");

        await _service.MarkRunFailedAsync(job.Id, ScheduledJobService.NoProviderFailureReason);
        Assert.Equal(ScheduledJobStatus.Failed, (await _service.GetAsync(job.Id))!.Status);
    }

    [Fact]
    public async Task MarkRunCompleteAsync_OnceJobWithNullSpecificDate_StillSettles()
    {
        // Once with SpecificDate == null falls through to the Daily expression, which clamps forward — so the
        // settle predicate must be Recurrence and not "is the recomputed NextFireAt still past".
        var job = await _service.CreateAsync("TEST_OnceNoDate", "q", RecurrenceType.Once, new TimeOnly(9, 0),
            specificDate: null);
        var plantedFire = DateTime.Now.AddMinutes(-5);
        await ForceNextFireAtAsync(job.Id, plantedFire);

        await _service.MarkRunCompleteAsync(job.Id, Guid.NewGuid());

        var after = await _service.GetAsync(job.Id);
        Assert.Equal(ScheduledJobStatus.Completed, after!.Status);
        Assert.Equal(plantedFire, after.NextFireAt, TimeSpan.FromSeconds(1));
        Assert.DoesNotContain(await _service.GetDueJobsAsync(), j => j.Id == job.Id);
    }

    [Fact]
    public async Task MarkRunCompleteAsync_RecurringJob_StillAdvancesAndStillDoesNotBumpUpdatedAt()
    {
        var job = await _service.CreateAsync("TEST_DailyComplete", "q", RecurrenceType.Daily, new TimeOnly(9, 0));
        var plantedUpdate = DateTime.Now.AddHours(-3);
        await ForceNextFireAtAsync(job.Id, DateTime.Now.AddMinutes(-5));
        await ForceUpdatedAtAsync(job.Id, plantedUpdate);

        await _service.MarkRunCompleteAsync(job.Id, Guid.NewGuid());

        var after = await _service.GetAsync(job.Id);
        Assert.Equal(ScheduledJobStatus.Active, after!.Status);
        Assert.True(after.NextFireAt > DateTime.Now, "a recurring job must still re-arm");
        // The recurring branch's deliberate non-bump: NextFireAt/LastFiredAt are device-local execution
        // state, so bumping UpdatedAt here would force a wasteful re-sync on every firing.
        Assert.Equal(plantedUpdate, after.UpdatedAt, TimeSpan.FromSeconds(1));
        Assert.Contains(await _service.GetActiveAsync(), j => j.Id == job.Id);
    }

    [Fact]
    public async Task AdvanceMissedRunAsync_RecurringJob_StillAdvances()
    {
        var job = await _service.CreateAsync("TEST_DailyAdvance", "q", RecurrenceType.Daily, new TimeOnly(9, 0));
        await ForceNextFireAtAsync(job.Id, DateTime.Now.AddMinutes(-20));

        await _service.AdvanceMissedRunAsync(job.Id);

        var after = await _service.GetAsync(job.Id);
        Assert.Equal(ScheduledJobStatus.Active, after!.Status);
        Assert.True(after.NextFireAt > DateTime.Now, "a recurring job must still re-arm");
    }

    [Fact]
    public async Task MarkRunFailedAsync_RecurringJob_StaysActiveOnFirstFailure()
    {
        var job = await _service.CreateAsync("TEST_DailyFails", "q", RecurrenceType.Daily, new TimeOnly(9, 0));
        await ForceNextFireAtAsync(job.Id, DateTime.Now.AddMinutes(-5));

        await _service.MarkRunFailedAsync(job.Id, "transient");

        var after = await _service.GetAsync(job.Id);
        Assert.Equal(ScheduledJobStatus.Active, after!.Status);
        Assert.Equal(1, after.ConsecutiveFailures);
        Assert.True(after.NextFireAt > DateTime.Now, "a recurring job must still re-arm");
    }

    [Fact]
    public async Task CompletedJob_IsAbsentFromGetActive_ButPresentInGetAll()
    {
        // Settled rows are kept forever on purpose: a completed one-off's LastResultEntryId links to
        // the chat it produced, which is user-visible history.
        var job = await _service.CreateAsync("TEST_OnceListed", "q", RecurrenceType.Once, new TimeOnly(9, 0),
            specificDate: DateTime.Now.Date.AddDays(-1));
        await _service.MarkRunCompleteAsync(job.Id, Guid.NewGuid());

        Assert.DoesNotContain(await _service.GetActiveAsync(), j => j.Id == job.Id);
        Assert.Contains(await _service.GetAllAsync(), j => j.Id == job.Id);
    }

    [Theory]
    [InlineData(ScheduledJobKind.Research)]
    [InlineData(ScheduledJobKind.AgentTask)]
    public async Task OnceJob_SettlesIdenticallyForEitherKind(ScheduledJobKind kind)
    {
        // The branch lives in the service, so both dispatch legs get it; moving it into ExecuteAgentTaskAsync
        // reds the Research case.
        var job = await _service.CreateAsync("TEST_OnceKind_" + kind, "q", RecurrenceType.Once, new TimeOnly(9, 0),
            specificDate: DateTime.Now.Date.AddDays(-1), kind: kind);
        await ForceNextFireAtAsync(job.Id, DateTime.Now.AddMinutes(-5));

        await _service.MarkRunCompleteAsync(job.Id, Guid.NewGuid());

        var after = await _service.GetAsync(job.Id);
        Assert.Equal(kind, after!.Kind);
        Assert.Equal(ScheduledJobStatus.Completed, after.Status);
    }

    [Fact]
    public async Task UpdateAsync_ReArmsASettledOnceJob()
    {
        // EnableAsync is not exposed by ScheduledJobToolHandler and there is no scheduled-job view model, so
        // without this a settled one-off is permanently inert while the update tool still reports success.
        var job = await _service.CreateAsync("TEST_OnceReArm", "q", RecurrenceType.Once, new TimeOnly(9, 0),
            specificDate: DateTime.Now.Date.AddDays(-1));
        await _service.MarkRunCompleteAsync(job.Id, Guid.NewGuid());
        Assert.Equal(ScheduledJobStatus.Completed, (await _service.GetAsync(job.Id))!.Status);

        await _service.UpdateAsync(job.Id, timeOfDay: new TimeOnly(10, 0), recurrence: RecurrenceType.Daily);

        var after = await _service.GetAsync(job.Id);
        Assert.Equal(ScheduledJobStatus.Active, after!.Status);
        Assert.True(after.NextFireAt > DateTime.Now, "a re-scheduled job must have a future NextFireAt");
        Assert.Contains(await _service.GetActiveAsync(), j => j.Id == job.Id);
    }

    [Fact]
    public async Task UpdateAsync_DoesNotReArmASettledJobWhoseFireTimeIsStillPast()
    {
        // A settled one-off whose fire time is still past stays settled until the caller re-schedules it
        // forward: re-arming it would fire an unattended run on the very next tick.
        var job = await _service.CreateAsync("TEST_OnceStalePast", "q", RecurrenceType.Once, new TimeOnly(9, 0),
            specificDate: DateTime.Now.Date.AddDays(-2));
        await _service.MarkRunCompleteAsync(job.Id, Guid.NewGuid());

        await _service.UpdateAsync(job.Id, name: "TEST_OnceStalePast_renamed");

        var after = await _service.GetAsync(job.Id);
        Assert.Equal(ScheduledJobStatus.Completed, after!.Status);
        Assert.Equal("TEST_OnceStalePast_renamed", after.Name);
        Assert.DoesNotContain(await _service.GetDueJobsAsync(), j => j.Id == job.Id);
    }

    [Fact]
    public async Task UpdateAsync_MovingASettledOneOffToAFutureDate_ReArmsIt()
    {
        // The re-arm rule (Completed + a future NextFireAt ⇒ Active) is unreachable for a one-off whose date has
        // passed unless a caller can supply a new date.
        var job = await _service.CreateAsync("TEST_OnceMovedForward", "q", RecurrenceType.Once,
            new TimeOnly(9, 0), specificDate: DateTime.Now.Date.AddDays(-3));
        await _service.MarkRunCompleteAsync(job.Id, Guid.NewGuid());
        Assert.Equal(ScheduledJobStatus.Completed, (await _service.GetAsync(job.Id))!.Status);

        var target = DateTime.Now.Date.AddDays(3);
        await _service.UpdateAsync(job.Id, specificDate: target);

        var after = await _service.GetAsync(job.Id);
        Assert.Equal(ScheduledJobStatus.Active, after!.Status);
        Assert.Equal(target.Date, after.SpecificDate!.Value.Date);
        // The date has to be persisted, not merely used to recompute, or a re-armed job keeps the old past date
        // and settles again on its next fire.
        Assert.True(after.NextFireAt > DateTime.Now);
    }

    [Fact]
    public async Task UpdateAsync_MovingASettledOneOffToAnotherPastDate_LeavesItSettled()
    {
        // An explicitly past date must not re-arm, or the job fires on the next 30 s tick.
        var job = await _service.CreateAsync("TEST_OnceMovedBackward", "q", RecurrenceType.Once,
            new TimeOnly(9, 0), specificDate: DateTime.Now.Date.AddDays(-3));
        await _service.MarkRunCompleteAsync(job.Id, Guid.NewGuid());

        await _service.UpdateAsync(job.Id, specificDate: DateTime.Now.Date.AddDays(-1));

        var after = await _service.GetAsync(job.Id);
        Assert.Equal(ScheduledJobStatus.Completed, after!.Status);
        Assert.DoesNotContain(await _service.GetDueJobsAsync(), j => j.Id == job.Id);
    }

    [Fact]
    public async Task UpdateAsync_CanChangeAJobsKind_WithoutLosingItsIdentity()
    {
        // The only alternative is delete-and-recreate, which discards the row's id, its history and its
        // LastResultEntryId link.
        var job = await _service.CreateAsync("TEST_KindSwap", "q", RecurrenceType.Daily, new TimeOnly(9, 0),
            kind: ScheduledJobKind.Research);

        await _service.UpdateAsync(job.Id, kind: ScheduledJobKind.AgentTask);

        var after = await _service.GetAsync(job.Id);
        Assert.Equal(ScheduledJobKind.AgentTask, after!.Kind);
        Assert.Equal(job.Id, after.Id);
    }

    [Fact]
    public async Task UpdateAsync_DoesNotReviveADisabledJob()
    {
        // Disabled is the user's explicit off switch, owned by DisableAsync/EnableAsync. An unrelated field
        // edit must not silently switch a job back on.
        var job = await _service.CreateAsync("TEST_DisabledEdit", "q", RecurrenceType.Daily, new TimeOnly(9, 0));
        await _service.DisableAsync(job.Id);

        await _service.UpdateAsync(job.Id, name: "TEST_DisabledEdit_renamed");

        var after = await _service.GetAsync(job.Id);
        Assert.Equal(ScheduledJobStatus.Disabled, after!.Status);
        Assert.Equal("TEST_DisabledEdit_renamed", after.Name);
    }

    [Fact]
    public async Task UpdateAsync_DoesNotReviveAFailureRetiredJob()
    {
        // Failed carries a ConsecutiveFailures budget that only EnableAsync resets, so re-arming it here
        // would put a broken job straight back into the due query with its failure count intact.
        var job = await _service.CreateAsync("TEST_FailedEdit", "q", RecurrenceType.Once, new TimeOnly(9, 0),
            specificDate: DateTime.Now.Date.AddDays(-1));
        await _service.MarkRunFailedAsync(job.Id, "boom");
        Assert.Equal(ScheduledJobStatus.Failed, (await _service.GetAsync(job.Id))!.Status);

        await _service.UpdateAsync(job.Id, timeOfDay: new TimeOnly(10, 0));

        Assert.Equal(ScheduledJobStatus.Failed, (await _service.GetAsync(job.Id))!.Status);
    }

    private async Task ForceUpdatedAtAsync(Guid id, DateTime when)
    {
        var conn = _ctx.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE ScheduledJobs SET UpdatedAt = @t WHERE Id = @id";
        cmd.Parameters.AddWithValue("@t", when.ToString("O"));
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SetOwnerDeviceIdAsync(Guid id, Guid? ownerId)
    {
        var conn = _ctx.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE ScheduledJobs SET OwnerDeviceId = @owner WHERE Id = @id";
        cmd.Parameters.AddWithValue("@owner", ownerId.HasValue ? (object)ownerId.Value.ToString() : DBNull.Value);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task BackfillRecurrenceDays_PinsTheWeekdayAWeeklyJobCurrentlyFiresOn()
    {
        var job = await _service.CreateAsync("TEST_Weekly", "q", RecurrenceType.Weekly, new TimeOnly(7, 30));
        Assert.Null(job.DayOfWeek);

        // 31 days is deliberately not a multiple of 7, so this weekday cannot coincide with today's — which is
        // what a backfill reading DateTime.Now instead of NextFireAt would produce.
        var fired = DateTime.Now.Date.AddDays(-31).AddHours(7).AddMinutes(30);
        await ForceNextFireAtAsync(job.Id, fired);

        Assert.Equal(1, await _service.BackfillRecurrenceDaysAsync());

        var pinned = await _service.GetAsync(job.Id);
        Assert.Equal(fired.DayOfWeek, pinned!.DayOfWeek);
        Assert.NotEqual(DateTime.Now.DayOfWeek, pinned.DayOfWeek);
    }

    [Fact]
    public async Task BackfillRecurrenceDays_LeavesNextFireAtWhereItWas()
    {
        var job = await _service.CreateAsync("TEST_WeeklyKeepsFireAt", "q", RecurrenceType.Weekly, new TimeOnly(7, 30));
        var fired = DateTime.Now.Date.AddDays(-31).AddHours(7).AddMinutes(30);
        await ForceNextFireAtAsync(job.Id, fired);

        await _service.BackfillRecurrenceDaysAsync();

        // Routing the pin through UpdateAsync would recompute this off DateTime.Now — the drift being repaired.
        var pinned = await _service.GetAsync(job.Id);
        Assert.Equal(fired, pinned!.NextFireAt);
    }

    [Fact]
    public async Task BackfillRecurrenceDays_PinsBothMonthAndDayForAYearlyJob()
    {
        var job = await _service.CreateAsync("TEST_Yearly", "q", RecurrenceType.Yearly, new TimeOnly(9, 0));
        var fired = new DateTime(2026, 3, 9, 9, 0, 0);
        await ForceNextFireAtAsync(job.Id, fired);

        Assert.Equal(1, await _service.BackfillRecurrenceDaysAsync());

        var pinned = await _service.GetAsync(job.Id);
        Assert.Equal(3, pinned!.Month);
        Assert.Equal(9, pinned.DayOfMonth);
    }

    [Fact]
    public async Task BackfillRecurrenceDays_IsANoOpOnASecondRun()
    {
        var job = await _service.CreateAsync("TEST_Twice", "q", RecurrenceType.Monthly, new TimeOnly(9, 0));
        await ForceNextFireAtAsync(job.Id, new DateTime(2026, 3, 9, 9, 0, 0));

        Assert.Equal(1, await _service.BackfillRecurrenceDaysAsync());
        Assert.Equal(0, await _service.BackfillRecurrenceDaysAsync());

        var pinned = await _service.GetAsync(job.Id);
        Assert.Equal(9, pinned!.DayOfMonth);
    }

    [Fact]
    public async Task BackfillRecurrenceDays_LeavesADayTheUserAlreadyChoseAlone()
    {
        var job = await _service.CreateAsync(
            "TEST_AlreadyPinned", "q", RecurrenceType.Weekly, new TimeOnly(9, 0), dayOfWeek: DayOfWeek.Tuesday);
        await ForceNextFireAtAsync(job.Id, DateTime.Now.Date.AddDays(-31));

        Assert.Equal(0, await _service.BackfillRecurrenceDaysAsync());
        Assert.Equal(DayOfWeek.Tuesday, (await _service.GetAsync(job.Id))!.DayOfWeek);
    }

    [Fact]
    public async Task BackfillRecurrenceDays_IgnoresRecurrencesWithNoDayToPin()
    {
        await _service.CreateAsync("TEST_Daily", "q", RecurrenceType.Daily, new TimeOnly(9, 0));
        await _service.CreateAsync("TEST_Once", "q", RecurrenceType.Once, new TimeOnly(9, 0));

        Assert.Equal(0, await _service.BackfillRecurrenceDaysAsync());
    }

    [Fact]
    public async Task BackfillRecurrenceDays_LeavesAJobOwnedByAnotherDeviceAlone()
    {
        var job = await _service.CreateAsync("TEST_Foreign", "q", RecurrenceType.Weekly, new TimeOnly(9, 0));
        await ForceNextFireAtAsync(job.Id, DateTime.Now.Date.AddDays(-31));
        await ForceOwnerAsync(job.Id, Guid.NewGuid());

        // Only the owner has real firing history: NextFireAt is device-local and never synced, so a peer
        // pinning from its own copy would invent a day.
        Assert.Equal(0, await _service.BackfillRecurrenceDaysAsync());
        Assert.Null((await _service.GetAsync(job.Id))!.DayOfWeek);
    }

    [Fact]
    public async Task MarkRunComplete_KeepsAPinnedWeeklyJobOnItsOwnDay_EvenWhenItRanLate()
    {
        var job = await _service.CreateAsync(
            "TEST_LateWeekly", "q", RecurrenceType.Weekly, new TimeOnly(7, 30), dayOfWeek: DayOfWeek.Monday);
        await ForceNextFireAtAsync(job.Id, DateTime.Now.AddDays(-2));

        await _service.MarkRunCompleteAsync(job.Id, Guid.NewGuid());

        // The regression the day pickers exist for: without a pinned day this lands on today's weekday and the
        // job relocates there permanently.
        var rescheduled = await _service.GetAsync(job.Id);
        Assert.Equal(DayOfWeek.Monday, rescheduled!.NextFireAt.DayOfWeek);
        Assert.True(rescheduled.NextFireAt > DateTime.Now);
    }

    private async Task ForceOwnerAsync(Guid id, Guid owner)
    {
        var conn = _ctx.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE ScheduledJobs SET OwnerDeviceId = @o WHERE Id = @id";
        cmd.Parameters.AddWithValue("@o", owner.ToString());
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task ForceNextFireAtAsync(Guid id, DateTime when)
    {
        var conn = _ctx.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE ScheduledJobs SET NextFireAt = @t WHERE Id = @id";
        cmd.Parameters.AddWithValue("@t", when.ToString("O"));
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        var conn = _ctx.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM ScheduledJobs WHERE Name LIKE 'TEST_%'";
        cmd.ExecuteNonQuery();
        _ctx.Dispose();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best effort */ }
    }
}
