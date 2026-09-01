using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Scheduling;
using Pia.Shared.Models;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>Over real SQLite, not fakes: idempotence and the timezone fact both live in the persisted <c>"O"</c> strings, which a
/// fake holding a <see cref="DateTime"/> would preserve for free.</summary>
public sealed class ScheduledFiringReconcilerTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly SqliteContext _ctx;
    private readonly ScheduledJobService _jobs;
    private readonly AgentRunService _runs;
    private readonly AssistantChatService _chats;
    private readonly ScheduledFiringReconciler _reconciler;

    public ScheduledFiringReconcilerTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
        _runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_ctx, _runs);
        _jobs = new ScheduledJobService(
            _ctx, new RecurrenceCalculator(), new TestSettingsService(),
            new SyncDeleteTrackerService(_tmpDir, NullLogger<SyncDeleteTrackerService>.Instance),
            NullLogger<ScheduledJobService>.Instance);
        _reconciler = new ScheduledFiringReconciler(_jobs, _runs, NullLogger<ScheduledFiringReconciler>.Instance);
    }

    /// <summary>The reconcile must book the outcome only: re-arming would re-run an unattended goal, advancing would rewrite
    /// the record of when it was meant to fire.</summary>
    [Fact]
    public async Task ACrashedOneOffFiring_GetsItsHealthColumnsBooked_WithoutReArmingOrAdvancing()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await DispatchedOnceJobAsync();
        var plantedFire = job.NextFireAt;

        var before = await _jobs.GetAsync(job.Id);
        Assert.Equal(ScheduledJobStatus.Completed, before!.Status);
        Assert.Null(before.LastFiredAt);

        var run = await CrashedRunAsync(job.Id);
        Assert.Equal(AgentRunState.Planning, (await _runs.GetAsync(run.Id, ct))!.State);
        await _runs.FailInterruptedRunsAsync(ct);   // the sweep this reconcile must run AFTER

        Assert.Equal(1, await _reconciler.ReconcileAsync(ct));

        var settledRun = await _runs.GetAsync(run.Id, ct);
        var after = await _jobs.GetAsync(job.Id);
        Assert.NotNull(settledRun!.CompletedAt);
        Assert.Equal(settledRun.CompletedAt!.Value.ToLocalTime(), after!.LastFiredAt!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(1, after.ConsecutiveFailures);

        // A booking that flipped Status to Failed would retire the job through a path with no 5-strike valve.
        Assert.Equal(ScheduledJobStatus.Completed, after.Status);
        Assert.Equal(plantedFire, after.NextFireAt, TimeSpan.FromSeconds(1));
        Assert.DoesNotContain(await _jobs.GetDueJobsAsync(), j => j.Id == job.Id);
    }

    /// <summary>Startup runs this every launch, so a non-idempotent pass would climb a crashed job's failure counter forever.</summary>
    [Fact]
    public async Task ReconcileAsync_IsIdempotent_ASecondPassBooksNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await DispatchedOnceJobAsync();
        var run = await CrashedRunAsync(job.Id);
        await _runs.FailInterruptedRunsAsync(ct);

        Assert.Equal(1, await _reconciler.ReconcileAsync(ct));
        var firstPass = await _jobs.GetAsync(job.Id);

        Assert.Equal(0, await _reconciler.ReconcileAsync(ct));
        Assert.Equal(0, await _reconciler.ReconcileAsync(ct));

        var after = await _jobs.GetAsync(job.Id);
        Assert.Equal(1, after!.ConsecutiveFailures);
        Assert.Equal(firstPass!.LastFiredAt!.Value, after.LastFiredAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.NotEqual(Guid.Empty, run.Id);
    }

    /// <summary>Re-booking an already-booked firing would stamp a failure over a success, on every launch.</summary>
    [Fact]
    public async Task ReconcileAsync_SkipsAFiringTheJobAlreadyBooked()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await DispatchedOnceJobAsync();
        var chatId = await MakeChatAsync();
        var run = await _runs.CreateAsync(new AgentRunCreateRequest(
            chatId, RunShape.Planned, AgentRunTrigger.Schedule, job.Id, null, "goal"), ct);
        await _runs.CompleteAsync(run.Id, ct: ct);
        // The live continuation's write, which happens just AFTER the run settled.
        await _jobs.MarkRunCompleteAsync(job.Id, chatId);

        var booked = await _jobs.GetAsync(job.Id);
        Assert.NotNull(booked!.LastFiredAt);

        Assert.Equal(0, await _reconciler.ReconcileAsync(ct));

        var after = await _jobs.GetAsync(job.Id);
        Assert.Equal(0, after!.ConsecutiveFailures);
        Assert.Equal(chatId, after.LastResultEntryId);
        Assert.Equal(booked.LastFiredAt!.Value, after.LastFiredAt!.Value, TimeSpan.FromMilliseconds(1));
    }

    /// <summary>Comparison operators ignore <see cref="DateTime.Kind"/>, so the Local <c>LastFiredAt</c> against the UTC
    /// <c>CompletedAt</c> is off by the host offset; the two jobs straddle the settle so either sign reds.</summary>
    [Fact]
    public async Task ReconcileAsync_ComparesInUtc_NotLocalStringOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var settledAtUtc = DateTime.UtcNow.AddHours(-3);

        var stale = await _jobs.CreateAsync("TEST_TzStale", "q", RecurrenceType.Daily, new TimeOnly(9, 0));
        var stalePlanted = settledAtUtc.AddHours(-1).ToLocalTime();
        await ForceLastFiredAtAsync(stale.Id, stalePlanted);
        await SettledRunAsync(stale.Id, settledAtUtc);

        var fresh = await _jobs.CreateAsync("TEST_TzFresh", "q", RecurrenceType.Daily, new TimeOnly(9, 0));
        var freshPlanted = settledAtUtc.AddHours(1).ToLocalTime();
        await ForceLastFiredAtAsync(fresh.Id, freshPlanted);
        await SettledRunAsync(fresh.Id, settledAtUtc);

        Assert.Equal(1, await _reconciler.ReconcileAsync(ct));

        var staleAfter = await _jobs.GetAsync(stale.Id);
        Assert.Equal(1, staleAfter!.ConsecutiveFailures);
        Assert.Equal(settledAtUtc.ToLocalTime(), staleAfter.LastFiredAt!.Value, TimeSpan.FromSeconds(1));

        var freshAfter = await _jobs.GetAsync(fresh.Id);
        Assert.Equal(0, freshAfter!.ConsecutiveFailures);
        Assert.Equal(freshPlanted, freshAfter.LastFiredAt!.Value, TimeSpan.FromSeconds(1));
    }

    /// <summary>Startup runs the reconcile once, so one orphaned firing must not cost every other job its booking.</summary>
    [Fact]
    public async Task ReconcileAsync_IgnoresAFiringWhoseJobWasDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        await SettledRunAsync(Guid.NewGuid(), DateTime.UtcNow.AddHours(-1));

        var job = await DispatchedOnceJobAsync();
        await CrashedRunAsync(job.Id);
        await _runs.FailInterruptedRunsAsync(ct);

        Assert.Equal(1, await _reconciler.ReconcileAsync(ct));
        Assert.NotNull((await _jobs.GetAsync(job.Id))!.LastFiredAt);
    }

    private async Task<ScheduledJob> DispatchedOnceJobAsync()
    {
        var job = await _jobs.CreateAsync("TEST_Crashed", "q", RecurrenceType.Once, new TimeOnly(9, 0),
            specificDate: DateTime.Now.Date.AddDays(-1), kind: ScheduledJobKind.AgentTask);
        await ForceNextFireAtAsync(job.Id, DateTime.Now.AddMinutes(-30));
        await _jobs.MarkOccurrenceDispatchedAsync(job.Id);
        job.NextFireAt = (await _jobs.GetAsync(job.Id))!.NextFireAt;
        return job;
    }

    private async Task<AgentRun> CrashedRunAsync(Guid jobId)
    {
        var chatId = await MakeChatAsync();
        return await _runs.CreateAsync(new AgentRunCreateRequest(
            chatId, RunShape.Planned, AgentRunTrigger.Schedule, jobId, null, "goal"),
            TestContext.Current.CancellationToken);
    }

    private async Task<AgentRun> SettledRunAsync(Guid jobId, DateTime completedAtUtc)
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await CrashedRunAsync(jobId);
        await _runs.FailAsync(run.Id, "interrupted", cancelled: true, ct);
        var conn = _ctx.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE AgentRuns SET CompletedAt = @t WHERE Id = @id";
        cmd.Parameters.AddWithValue("@t", completedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@id", run.Id.ToString());
        await cmd.ExecuteNonQueryAsync();
        return run;
    }

    private async Task<Guid> MakeChatAsync()
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await _chats.SaveAsync(new SyncAssistantChat
        {
            Id = id,
            CreatedAt = now,
            UpdatedAt = now,
            LastAccessedAt = now,
            WindowMode = "Assistant",
        }, TestContext.Current.CancellationToken);
        return id;
    }

    private async Task ForceNextFireAtAsync(Guid id, DateTime when) =>
        await ForceJobColumnAsync(id, "NextFireAt", when);

    /// <summary>Plants a LOCAL <c>"O"</c> string with its offset, exactly as the production writers do.</summary>
    private async Task ForceLastFiredAtAsync(Guid id, DateTime when) =>
        await ForceJobColumnAsync(id, "LastFiredAt", when);

    private async Task ForceJobColumnAsync(Guid id, string column, DateTime when)
    {
        var conn = _ctx.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE ScheduledJobs SET {column} = @t WHERE Id = @id";
        cmd.Parameters.AddWithValue("@t", when.ToString("O"));
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await cmd.ExecuteNonQueryAsync();
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

    public void Dispose()
    {
        _runs.Dispose();
        _chats.Dispose();
        _ctx.Dispose();
        TempPath.Remove(_tmpDir);
    }
}
