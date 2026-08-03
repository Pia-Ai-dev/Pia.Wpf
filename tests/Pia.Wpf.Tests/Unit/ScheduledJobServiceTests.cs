using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Scheduling;
using Xunit;

namespace Pia.Wpf.Tests.Unit;

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

    // ---------------------------------------------------------------------------------------------
    // W3: a fired RecurrenceType.Once job must SETTLE, not re-arm.
    //
    // These run against the REAL ScheduledJobService and the REAL RecurrenceCalculator on a temp-file
    // SqliteContext, deliberately NOT against the hand-written fakes in ScheduledJobBackgroundServiceTests
    // (whose AdvanceMissedRunAsync hardcodes +1d and whose GetDueJobsAsync has no Status clause) — a test
    // written there passes on unfixed code and is worthless.
    //
    // (B) extends the section: a PRE-MODEL failure (ScheduledJobService.NoProviderFailureReason) no longer
    // retires a one-off outright, it re-arms once — so the old "fails on first failure" test is split into
    // its post-model and pre-model halves below rather than deleted.
    // ---------------------------------------------------------------------------------------------

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

    /// <summary>
    /// hermes #2 layer (a) for the <see cref="RecurrenceType.Once"/> class, which is the half the scheduler's
    /// own tests cannot see. Now that a tick dispatches without awaiting the run, the schedule must leave the due
    /// window at DISPATCH — and for a one-off the only column that can do that is <c>Status</c>, because
    /// <c>NextFireAt</c> is deliberately left at its past instant on every settle path.
    /// <para>
    /// The "NextFireAt unchanged" leg is what makes "no longer due" non-vacuous: give the Once branch the
    /// recurring branch's <c>SET NextFireAt=@NextFireAt</c> and the row also leaves the window, so the not-due leg
    /// alone would stay green while the honest record was silently rewritten.
    /// </para>
    /// </summary>
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

        // The DISPATCH write deliberately leaves these two null, and that is still the right pin — it is what
        // keeps "the schedule moved on" and "the run fired and produced something" two separate facts written at
        // two separate times. Status reads 'Completed' here while the row holds no record of having fired; that
        // window is closed from the OTHER side, never by widening this write:
        //   * live: MarkRunComplete / MarkRunFailed fill them in when the run settles;
        //   * crash: ScheduledFiringReconciler books them at the next startup, from the settled AgentRuns row
        //     (which FailInterruptedRunsAsync has just swept to Cancelled), via MarkFiringOutcomeAsync —
        //     health columns only, so the reconcile cannot re-arm, retire or re-schedule anything;
        //   * park-then-resume: IHeadlessRunLauncher.ResumedRunSettled books the same way, for the run whose
        //     settle no continuation was awaiting because a resume hands out no handle.
        // Widening the dispatch write instead would need a parameter through MoveOffCurrentOccurrenceAsync
        // (which the W3 note above forbids) and would wrongly stamp LastFiredAt for the user-Skip door, which
        // did NOT fire. Pinned below so a future "just set LastFiredAt at dispatch" reds here.
        Assert.Null(after.LastFiredAt);
        Assert.Null(after.LastResultEntryId);

        Assert.DoesNotContain(await _service.GetDueJobsAsync(), j => j.Id == job.Id);
    }

    /// <summary>
    /// The recurring half of the same layer, against the real calculator: the occurrence is spent, so the row
    /// re-arms into the NEXT one and nothing else moves. <c>UpdatedAt</c> deliberately does not bump —
    /// <c>NextFireAt</c> is device-local execution state and bumping would force a pointless sync push.
    /// </summary>
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

    /// <summary>
    /// T0-1. The health-only booking, over the exact row shape the crash leaves behind: a ONE-OFF that dispatch
    /// already settled as <c>Completed</c> with <c>LastFiredAt</c> null. Booking a FAILURE onto it must record
    /// the firing and NOTHING else — the four schedule/sync columns are the interesting half of this fact, since
    /// every existing outcome writer moves at least one of them and reusing one of those here is exactly the
    /// mistake the plan warns about (it would stamp <c>'Failed'</c> over dispatch's <c>'Completed'</c> and burn a
    /// strike on a job that can never fire again).
    /// </summary>
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

        // A PAST instant, not "now": the whole reason firedAt is a parameter. Asserting it comes back means a
        // future implementation that stamps DateTime.Now (which would be self-idempotent and therefore stop the
        // reconcile booking anything at all) reds here.
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

    /// <summary>
    /// The success half, on a RECURRING row that already carries a failure count — so "clears the counter" and
    /// "records the chat" are both non-vacuous, and the re-armed <c>NextFireAt</c> proves the booking did not
    /// recompute the schedule the way <c>MarkRunCompleteAsync</c> would.
    /// </summary>
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
        // Half one of the split, and it PASSES BEFORE AND AFTER this change — a guard, not new coverage.
        // A one-off has no future occurrence to retry INTO, and once the run has STARTED a retry is not
        // idempotent (the first attempt may already have written to the vault), so a failure carrying
        // anything other than the pre-model reason retires the job on the first strike and does not wait for
        // the 5-strike valve. This is the non-idempotency guard: a raw provider or exception string must NEVER
        // buy a second unattended run.
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
        // Half two, and this one FAILS on the pre-change tree (the row settles Failed immediately today).
        // NoProvider costs nothing — no AgentRuns row, no tokens, no writes — and is often momentary (a
        // pinned provider row missing for the seconds a sync pull takes to re-import it), yet it used to
        // spend the job's only firing.
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
        // NextFireAt/ConsecutiveFailures are device-local execution state and are not synced at all, so the
        // re-arm must NOT bump UpdatedAt — that would force a pointless push and let a local retry outrank a
        // genuine remote edit in SyncClientService's merge.
        Assert.Equal(plantedUpdate, after.UpdatedAt, TimeSpan.FromSeconds(1));
        Assert.DoesNotContain(await _service.GetDueJobsAsync(), j => j.Id == job.Id);
        // Still Active, so the row is still listed and still owns a scheduled firing.
        Assert.Contains(await _service.GetActiveAsync(), j => j.Id == job.Id);
    }

    [Fact]
    public async Task MarkRunFailedAsync_OnceJob_SecondPreModelFailure_SettlesFailed()
    {
        // The cap: retry once, then stop. A one-off that cannot resolve a provider twice, ten minutes apart,
        // is broken in a way a third unattended attempt will not fix. FAILS on the pre-change tree at the
        // intermediate Active assertion.
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
        // The UpdatedAt trap, both directions. SyncClientService's pull merge applies the REMOTE row when
        // remote.UpdatedAt >= local.UpdatedAt, and UpsertFromSyncAsync then writes Status back to 'Active' —
        // so a SETTLE that does not bump looks green locally and is reverted by the first pull. The RE-ARM is
        // the mirror case: it changes only device-local execution state, so it must not bump. FAILS on the
        // pre-change tree, where attempt 1 already settles and bumps.
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

        // And this is what the revert WOULD do if the predicate held — which is why the bump matters. It also
        // pins the storage premise behind the whole retry: UpsertFromSyncAsync leaves ConsecutiveFailures
        // alone (it is absent from SyncScheduledJob), so the attempt budget is not reset by a pull.
        remote.Status = ScheduledJobStatus.Active;
        await _service.UpsertFromSyncAsync(remote);
        var pulled = (await _service.GetAsync(job.Id))!;
        Assert.Equal(ScheduledJobStatus.Active, pulled.Status);
        Assert.Equal(2, pulled.ConsecutiveFailures);
    }

    [Fact]
    public async Task MarkRunFailedAsync_RecurringJob_PreModelFailure_StillUsesTheFiveStrikeBudget()
    {
        // PASSES BEFORE AND AFTER this change (the recurring SQL is untouched) — a scoping guard against a
        // future refactor that merges the two branches, not a regression test. The retry is for one-offs: a
        // recurring job already has a next occurrence to retry into, so the pre-model reason must neither
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
        // The quiet second face of W3: Once with SpecificDate == null falls through to the Daily
        // expression in RecurrenceCalculator, which DOES clamp forward — so such a job never "looks
        // past" and used to repeat every day forever. This test is what proves the predicate is
        // Recurrence and not "is the recomputed NextFireAt still past".
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
        // Executor parity, cheaply: the branch lives in the service, so both dispatch legs of
        // ScheduledJobBackgroundService get the fix. If someone moves it into ExecuteAgentTaskAsync,
        // the Research case goes red.
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
        // W3 left no re-arming surface: EnableAsync is not exposed by ScheduledJobToolHandler (list/create/
        // update/delete only) and there is no scheduled-job view model, so a settled one-off was permanently
        // inert while the update tool still reported success — "move that job to Friday at 10:00" did
        // nothing, and list_scheduled_jobs (GetActiveAsync) no longer showed the row to say so.
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
        // The narrowing: a settled one-off whose fire time is still in the past stays settled, because
        // re-arming it would fire the job on the very next tick — for an AgentTask, an unattended run nobody
        // asked for. It stays settled until the caller actually re-schedules it FORWARD.
        //
        // CORRECTED by Batch 09. This comment used to justify the rule with "UpdateAsync has no specificDate
        // parameter, so a settled one-off keeps its past instant". That parameter now exists, so the premise
        // is gone and the rule is load-bearing on its own — which is why the two facts below it were added:
        // one proves a FUTURE date re-arms, the other that a PAST date still does not. This fact keeps its
        // original shape (an edit that supplies no date at all) because that is a third distinct case.
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
        // Batch 09's re-arm surface, and the reason it needed a SERVICE change rather than only a UI: the
        // re-arm rule ("Completed + a future NextFireAt ⇒ Active") already existed and was UNREACHABLE for a
        // one-off whose date had passed, because no caller could supply a new date. The roadmap recorded that
        // as "a settled Once job has almost no re-arm surface" and handed it to this batch.
        var job = await _service.CreateAsync("TEST_OnceMovedForward", "q", RecurrenceType.Once,
            new TimeOnly(9, 0), specificDate: DateTime.Now.Date.AddDays(-3));
        await _service.MarkRunCompleteAsync(job.Id, Guid.NewGuid());
        Assert.Equal(ScheduledJobStatus.Completed, (await _service.GetAsync(job.Id))!.Status);

        var target = DateTime.Now.Date.AddDays(3);
        await _service.UpdateAsync(job.Id, specificDate: target);

        var after = await _service.GetAsync(job.Id);
        Assert.Equal(ScheduledJobStatus.Active, after!.Status);
        Assert.Equal(target.Date, after.SpecificDate!.Value.Date);
        // The date has to be PERSISTED, not merely used to recompute: the UPDATE statement did not carry
        // SpecificDate at all before this batch, so a re-armed job would have been written with the old past
        // date and settled again on its next fire.
        Assert.True(after.NextFireAt > DateTime.Now);
    }

    [Fact]
    public async Task UpdateAsync_MovingASettledOneOffToAnotherPastDate_LeavesItSettled()
    {
        // The other half, and the one that keeps the narrowing honest now that a date CAN be supplied: an
        // explicitly past date must not re-arm, or the job fires on the next 30 s tick.
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
        // A Research job cannot otherwise become an AgentTask except by delete-and-recreate, which discards
        // the row's id, its history and its LastResultEntryId link. Kind was also absent from the UPDATE
        // statement, so this is a persistence fact and not only a parameter one.
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
