using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Scheduling;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// T0-1: the startup reconcile that books a scheduled firing whose outcome nobody was left alive to record.
/// <para>
/// Deliberately over the REAL SQLite round trip — both services, one context, a temp file — and not over fakes.
/// Two of these facts exist only in the persisted STRINGS: idempotence depends on
/// <c>DateTime.ToString("O")</c> → <c>DateTime.Parse</c> preserving the instant exactly, and the timezone fact
/// depends on <c>MapJob</c> re-projecting a stored offset into host-local time. A fake holds the
/// <see cref="DateTime"/> object, so it would preserve both trivially and both tests would be theatre.
/// </para>
/// </summary>
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

    /// <summary>
    /// The whole reason this class exists, in the shape that is unrecoverable rather than merely untidy: a ONE-OFF
    /// job whose dispatch settled it <c>Completed</c> and whose process then died mid-run. It produced no chat, it
    /// recorded no firing, and it will never fire again. The reconcile must book the outcome — and ONLY the
    /// outcome: re-arming it or advancing it would either re-run an unattended goal or rewrite the honest record
    /// of when it was meant to fire.
    /// <para>
    /// Goes through the REAL <c>FailInterruptedRunsAsync</c> rather than writing a Cancelled row, because that
    /// ordering is a stated precondition: before the sweep the crashed run is <c>Planning</c>, i.e. not settled,
    /// and a reconcile that ran first would find nothing at all.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ACrashedOneOffFiring_GetsItsHealthColumnsBooked_WithoutReArmingOrAdvancing()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await DispatchedOnceJobAsync();
        var plantedFire = job.NextFireAt;

        var before = await _jobs.GetAsync(job.Id);
        Assert.Equal(ScheduledJobStatus.Completed, before!.Status);
        Assert.Null(before.LastFiredAt);   // premise: nothing recorded the firing — the defect's fingerprint

        var run = await CrashedRunAsync(job.Id);
        Assert.Equal(AgentRunState.Planning, (await _runs.GetAsync(run.Id, ct))!.State);
        await _runs.FailInterruptedRunsAsync(ct);   // the sweep this reconcile must run AFTER

        Assert.Equal(1, await _reconciler.ReconcileAsync(ct));

        var settledRun = await _runs.GetAsync(run.Id, ct);
        var after = await _jobs.GetAsync(job.Id);
        // Booked AT THE RUN'S SETTLE INSTANT, not at startup — the reconcile is recording history.
        Assert.NotNull(settledRun!.CompletedAt);
        Assert.Equal(settledRun.CompletedAt!.Value.ToLocalTime(), after!.LastFiredAt!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(1, after.ConsecutiveFailures);

        // ...and nothing else moved. Status in particular: a booking that flipped it to 'Failed' would retire
        // the job through a path with no 5-strike valve, and NextFireAt must keep the past instant it fired at.
        Assert.Equal(ScheduledJobStatus.Completed, after.Status);
        Assert.Equal(plantedFire, after.NextFireAt, TimeSpan.FromSeconds(1));
        Assert.DoesNotContain(await _jobs.GetDueJobsAsync(), j => j.Id == job.Id);
    }

    /// <summary>
    /// The reconcile runs on EVERY startup, so a second pass over the same rows must be a no-op — otherwise a
    /// crashed job's failure counter climbs by one per launch until it looks chronically broken. The guard is a
    /// <c>&gt;=</c> against what the first pass wrote, which is only sound if the instant survives the round trip
    /// through the column's local "O" string; a fake would preserve it for free and prove nothing.
    /// </summary>
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
        Assert.Equal(1, after!.ConsecutiveFailures);   // still ONE strike, not three
        Assert.Equal(firstPass!.LastFiredAt!.Value, after.LastFiredAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.NotEqual(Guid.Empty, run.Id);
    }

    /// <summary>
    /// A HEALTHY firing: the run completed and the live bookkeeping already booked it. The reconcile must leave
    /// it entirely alone — re-booking would stamp a failure over a success (the reconcile classifies a
    /// <c>Cancelled</c>/<c>Failed</c> run as a failure) and would do it on every single launch.
    /// </summary>
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
        Assert.NotNull(booked!.LastFiredAt);   // premise: the firing IS on the row

        Assert.Equal(0, await _reconciler.ReconcileAsync(ct));

        var after = await _jobs.GetAsync(job.Id);
        Assert.Equal(0, after!.ConsecutiveFailures);
        Assert.Equal(chatId, after.LastResultEntryId);
        Assert.Equal(booked.LastFiredAt!.Value, after.LastFiredAt!.Value, TimeSpan.FromMilliseconds(1));
    }

    /// <summary>
    /// THE TIMEZONE FACT, and the bug that cannot reproduce on a UTC+0 machine.
    /// <c>ScheduledJobs.LastFiredAt</c> is LOCAL <c>"O"</c> (with an offset) and comes back from
    /// <c>DateTime.Parse</c> as <see cref="DateTimeKind.Local"/>; <c>AgentRuns.CompletedAt</c> is UTC. C#'s
    /// comparison operators ignore <see cref="DateTime.Kind"/> entirely, so comparing the two raw is off by the
    /// host's offset — east of Greenwich every healthy job looks freshly booked and nothing is ever reconciled,
    /// west of it every healthy job looks stale and gets re-booked on EVERY startup.
    /// <para>
    /// Two jobs in one fact, straddling the settle instant by an hour each way, so it reds for EITHER sign of the
    /// offset instead of only the sign this machine happens to have. Honest limitation: inside a
    /// −1h..+1h band (Greenwich, Iceland, west Africa) both legs would still pass on the raw comparison, and
    /// there is no way to widen that without injecting the timezone into the reconciler.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReconcileAsync_ComparesInUtc_NotLocalStringOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var settledAtUtc = DateTime.UtcNow.AddHours(-3);

        // STALE: the row's record predates the settle, so this firing is unbooked and must be booked.
        var stale = await _jobs.CreateAsync("TEST_TzStale", "q", RecurrenceType.Daily, new TimeOnly(9, 0));
        var stalePlanted = settledAtUtc.AddHours(-1).ToLocalTime();
        await ForceLastFiredAtAsync(stale.Id, stalePlanted);
        await SettledRunAsync(stale.Id, settledAtUtc);

        // FRESH: the row's record postdates the settle, so it is already booked and must be left alone.
        var fresh = await _jobs.CreateAsync("TEST_TzFresh", "q", RecurrenceType.Daily, new TimeOnly(9, 0));
        var freshPlanted = settledAtUtc.AddHours(1).ToLocalTime();
        await ForceLastFiredAtAsync(fresh.Id, freshPlanted);
        await SettledRunAsync(fresh.Id, settledAtUtc);

        Assert.Equal(1, await _reconciler.ReconcileAsync(ct));

        // Reds at any positive offset > 1h: the stale row's LOCAL value compares as LATER than the UTC settle,
        // so a raw comparison skips a firing that was never booked.
        var staleAfter = await _jobs.GetAsync(stale.Id);
        Assert.Equal(1, staleAfter!.ConsecutiveFailures);
        Assert.Equal(settledAtUtc.ToLocalTime(), staleAfter.LastFiredAt!.Value, TimeSpan.FromSeconds(1));

        // Reds at any negative offset < -1h: the fresh row's LOCAL value compares as EARLIER than the UTC
        // settle, so a raw comparison re-books a healthy job — every launch, forever.
        var freshAfter = await _jobs.GetAsync(fresh.Id);
        Assert.Equal(0, freshAfter!.ConsecutiveFailures);
        Assert.Equal(freshPlanted, freshAfter.LastFiredAt!.Value, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// A firing whose job row is gone (deleted while its run was in flight) must not throw the whole reconcile —
    /// startup runs this once, and one orphan must not cost every other job its booking.
    /// </summary>
    [Fact]
    public async Task ReconcileAsync_IgnoresAFiringWhoseJobWasDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        await SettledRunAsync(Guid.NewGuid(), DateTime.UtcNow.AddHours(-1));

        var job = await DispatchedOnceJobAsync();
        await CrashedRunAsync(job.Id);
        await _runs.FailInterruptedRunsAsync(ct);

        // The orphan is skipped and the real firing is still booked — a single count proves both.
        Assert.Equal(1, await _reconciler.ReconcileAsync(ct));
        Assert.NotNull((await _jobs.GetAsync(job.Id))!.LastFiredAt);
    }

    // ---- fixture ----

    /// <summary>A one-off job whose occurrence has been dispatched: Status 'Completed', LastFiredAt null.</summary>
    private async Task<ScheduledJob> DispatchedOnceJobAsync()
    {
        var job = await _jobs.CreateAsync("TEST_Crashed", "q", RecurrenceType.Once, new TimeOnly(9, 0),
            specificDate: DateTime.Now.Date.AddDays(-1), kind: ScheduledJobKind.AgentTask);
        await ForceNextFireAtAsync(job.Id, DateTime.Now.AddMinutes(-30));
        await _jobs.MarkOccurrenceDispatchedAsync(job.Id);
        job.NextFireAt = (await _jobs.GetAsync(job.Id))!.NextFireAt;
        return job;
    }

    /// <summary>A run of <paramref name="jobId"/> left non-terminal, i.e. the shape a killed process leaves.</summary>
    private async Task<AgentRun> CrashedRunAsync(Guid jobId)
    {
        var chatId = await MakeChatAsync();
        return await _runs.CreateAsync(new AgentRunCreateRequest(
            chatId, RunShape.Planned, AgentRunTrigger.Schedule, jobId, null, "goal"),
            TestContext.Current.CancellationToken);
    }

    /// <summary>A run of <paramref name="jobId"/> settled Cancelled at a chosen UTC instant.</summary>
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

    /// <summary>
    /// Plants <c>LastFiredAt</c> as the production writers do — a LOCAL <c>"O"</c> string, offset included —
    /// which is the only way the timezone fact above can be about anything.
    /// </summary>
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
        _ctx.Dispose();
        try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
    }
}
