using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

public class AppRestartServiceTests
{
    private readonly ISyncClientService _sync = Substitute.For<ISyncClientService>();
    private readonly ITrayIconService _tray = Substitute.For<ITrayIconService>();
    private readonly List<string> _steps = [];
    private TaskCompletionSource _syncStopped = CompletedSource();
    private bool _syncThrows;
    private bool _trayThrows;

    public AppRestartServiceTests()
    {
        _sync.StopBackgroundSyncAndWaitAsync().Returns(_ => RecordSyncOnCompletionAsync());
        _tray.When(t => t.PrepareForExit()).Do(_ =>
        {
            if (_trayThrows)
            {
                Record("tray-failed");
                throw new InvalidOperationException("the window teardown threw");
            }

            Record("tray");
        });
    }

    private void Record(string step)
    {
        lock (_steps)
            _steps.Add(step);
    }

    /// <summary>Recorded when the returned task completes, not when the call is made: an unawaited stop
    /// would otherwise record in order too.</summary>
    private async Task RecordSyncOnCompletionAsync()
    {
        await _syncStopped.Task;

        if (_syncThrows)
        {
            Record("sync-failed");
            throw new InvalidOperationException("the sync loop threw");
        }

        Record("sync");
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource();
        source.SetResult();
        return source;
    }

    private TestableAppRestartService Create() => new(_sync, _tray, _steps);

    /// <summary>Ending the process is the one step a test may not run.</summary>
    private sealed class TestableAppRestartService : AppRestartService
    {
        private readonly List<string> _steps;

        public TestableAppRestartService(ISyncClientService sync, ITrayIconService tray, List<string> steps)
            : base(sync, tray, NullLogger<AppRestartService>.Instance)
        {
            _steps = steps;
        }

        protected override TimeSpan SyncStopTimeout => TimeSpan.FromMilliseconds(50);

        protected override void RequestRestartAndShutdown()
        {
            lock (_steps)
                _steps.Add("shutdown");
        }
    }

    [Fact]
    public async Task TheSequenceIsSyncStopThenTearDownThenShutdown()
    {
        _syncStopped = new TaskCompletionSource();
        var service = Create();

        var restarting = service.RestartAsync();
        Assert.Empty(_steps);

        _syncStopped.SetResult();
        await restarting;

        Assert.Equal(new[] { "sync", "tray", "shutdown" }, _steps);
    }

    [Fact]
    public async Task ASecondCall_DoesNothing()
    {
        var service = Create();

        await service.RestartAsync();
        await service.RestartAsync();

        Assert.Equal(new[] { "sync", "tray", "shutdown" }, _steps);
        await _sync.Received(1).StopBackgroundSyncAndWaitAsync();
        _tray.Received(1).PrepareForExit();
    }

    /// <summary>The overlay that got here has no dismiss, so a throw must not strand the user.</summary>
    [Fact]
    public async Task AFailingSyncStop_StillShutsDown()
    {
        _syncThrows = true;
        var service = Create();

        await service.RestartAsync();

        Assert.Equal(new[] { "sync-failed", "tray", "shutdown" }, _steps);
    }

    /// <summary>The wait is unbounded behind a 60s-per-request HttpClient, and the overlay stays up for all
    /// of it with no dismiss. A push left in flight costs nothing, so proceeding is the safe direction.</summary>
    [Fact]
    public async Task ASyncStopThatNeverReturns_StillShutsDown()
    {
        _syncStopped = new TaskCompletionSource();
        var service = Create();

        await service.RestartAsync();

        Assert.Equal(new[] { "tray", "shutdown" }, _steps);
    }

    [Fact]
    public async Task AFailingTearDown_StillShutsDown()
    {
        _trayThrows = true;
        var service = Create();

        await service.RestartAsync();

        Assert.Equal(new[] { "sync", "tray-failed", "shutdown" }, _steps);
    }
}
