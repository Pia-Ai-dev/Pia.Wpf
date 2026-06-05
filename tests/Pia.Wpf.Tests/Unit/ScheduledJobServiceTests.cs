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
