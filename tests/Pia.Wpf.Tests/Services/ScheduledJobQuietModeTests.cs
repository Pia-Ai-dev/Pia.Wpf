using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Flow;
using Pia.Services.Interfaces;
using Pia.Services.Scheduling;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>Quiet mode suppresses a monitor job's success notification only: a job that has silently stopped
/// working is a problem, so <c>NotifyFailure</c> ignores the flag entirely.</summary>
public sealed class ScheduledJobQuietModeTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteContext _ctx;
    private readonly ScheduledJobService _jobs;

    public ScheduledJobQuietModeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaQuiet_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _ctx = new SqliteContext(Path.Combine(_dir, "history.db"));
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        var deleteTracker = new SyncDeleteTrackerService(_dir, NullLogger<SyncDeleteTrackerService>.Instance);
        _jobs = new ScheduledJobService(_ctx, new RecurrenceCalculator(), settings, deleteTracker,
            NullLogger<ScheduledJobService>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ScheduledJob> NewJobAsync() =>
        await _jobs.CreateAsync("Monitor", "check the feed", RecurrenceType.Daily, new TimeOnly(9, 0));

    [Fact]
    public async Task ANewJob_IsNotQuiet()
    {
        var job = await NewJobAsync();

        Assert.False(job.QuietOnSuccess);
        Assert.False((await _jobs.GetAsync(job.Id))!.QuietOnSuccess);
    }

    /// <summary>A create path that drops the flag leaves a notifying job with no error and no hint that the
    /// choice was lost.</summary>
    [Fact]
    public async Task AJobCanBeCreatedQuiet()
    {
        var job = await _jobs.CreateAsync("Monitor", "check the feed", RecurrenceType.Daily,
            new TimeOnly(9, 0), quietOnSuccess: true);

        Assert.True(job.QuietOnSuccess);                             // the returned object
        Assert.True((await _jobs.GetAsync(job.Id))!.QuietOnSuccess);  // and the row it wrote
    }

    [Fact]
    public async Task QuietMode_RoundTripsThroughTheDatabase()
    {
        var job = await NewJobAsync();

        await _jobs.UpdateAsync(job.Id, quietOnSuccess: true);
        Assert.True((await _jobs.GetAsync(job.Id))!.QuietOnSuccess);

        await _jobs.UpdateAsync(job.Id, quietOnSuccess: false);
        Assert.False((await _jobs.GetAsync(job.Id))!.QuietOnSuccess);
    }

    /// <summary>Every other <c>UpdateAsync</c> parameter means "leave it alone" when null, and this one must
    /// too.</summary>
    [Fact]
    public async Task AnUnrelatedEdit_DoesNotClearQuietMode()
    {
        var job = await NewJobAsync();
        await _jobs.UpdateAsync(job.Id, quietOnSuccess: true);

        await _jobs.UpdateAsync(job.Id, name: "Renamed");

        var reloaded = await _jobs.GetAsync(job.Id);
        Assert.Equal("Renamed", reloaded!.Name);
        Assert.True(reloaded.QuietOnSuccess);
    }

    /// <summary>The flag is device-local: it is not on the wire, and <c>UpsertFromSyncAsync</c> writes only the
    /// synced config columns.</summary>
    [Fact]
    public async Task ASyncPull_CannotResetQuietMode()
    {
        var job = await NewJobAsync();
        await _jobs.UpdateAsync(job.Id, quietOnSuccess: true);

        // What a pull hands over: the same job id, config fields only — QuietOnSuccess left at its default.
        await _jobs.UpsertFromSyncAsync(new ScheduledJob
        {
            Id = job.Id,
            Name = "Renamed by a peer",
            Query = "check the feed",
            Kind = ScheduledJobKind.AgentTask,
            Recurrence = RecurrenceType.Daily,
            TimeOfDay = new TimeOnly(9, 0),
            NextFireAt = DateTime.Now.AddDays(1),
            Status = ScheduledJobStatus.Active,
            UpdatedAt = DateTime.Now.AddMinutes(5),
        });

        var reloaded = await _jobs.GetAsync(job.Id);
        Assert.Equal("Renamed by a peer", reloaded!.Name); // the pull really did land
        Assert.True(reloaded.QuietOnSuccess);              // and it did not touch this
    }

    /// <summary>Asserted through <c>IFlowService</c> because the Flow card is the in-app half a test can see —
    /// the Windows toast needs a desktop.</summary>
    [Fact]
    public void AQuietJob_PublishesNoSuccessCard_ButStillPublishesFailures()
    {
        var flow = Substitute.For<IFlowService>();
        var localization = Substitute.For<ILocalizationService>();
        localization[Arg.Any<string>()].Returns(ci => ci.Arg<string>());
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => ci.ArgAt<string>(0));
        var surface = new ScheduledJobNotificationSurface(
            flow, localization, Substitute.For<IWindowManagerService>(),
            NullLogger<ScheduledJobNotificationSurface>.Instance);

        var quiet = new ScheduledJob
        {
            Name = "Monitor", Query = "q", Recurrence = RecurrenceType.Daily,
            TimeOfDay = new TimeOnly(9, 0), NextFireAt = DateTime.Now, QuietOnSuccess = true,
        };
        var loud = new ScheduledJob
        {
            Name = "Monitor", Query = "q", Recurrence = RecurrenceType.Daily,
            TimeOfDay = new TimeOnly(9, 0), NextFireAt = DateTime.Now,
        };

        surface.NotifySuccess(quiet, Guid.NewGuid(), "chat");
        flow.DidNotReceiveWithAnyArgs().Publish(default!);

        // Non-vacuity: the same call on a non-quiet job DOES publish, so the assertion above is about the flag
        // and not about a surface that publishes nothing.
        surface.NotifySuccess(loud, Guid.NewGuid(), "chat");
        flow.ReceivedWithAnyArgs(1).Publish(default!);

        // A quiet monitor that BREAKS still says so.
        surface.NotifyFailure(quiet, "the feed is unreachable");
        flow.ReceivedWithAnyArgs(2).Publish(default!);
    }

    public void Dispose()
    {
        _ctx.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
        GC.SuppressFinalize(this);
    }
}
