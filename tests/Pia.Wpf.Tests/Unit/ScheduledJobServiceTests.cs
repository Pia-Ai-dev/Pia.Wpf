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
    // Execution is deferred: net10.0-windows cannot run on macOS, so these are written, not run.
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

    [Fact]
    public async Task MarkRunFailedAsync_OnceJob_FailsOnFirstFailure()
    {
        // A one-off has no future occurrence to retry INTO, so it does not wait for the 5-strike valve.
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
        // The narrowing: UpdateAsync has no specificDate parameter, so a settled one-off keeps its PAST
        // instant. Re-arming that would fire the job on the very next tick — for an AgentTask, an unattended
        // run nobody asked for. It stays settled until the caller actually re-schedules it forward.
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
