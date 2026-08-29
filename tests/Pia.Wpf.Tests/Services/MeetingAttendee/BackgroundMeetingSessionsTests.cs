using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.MeetingAttendee;
using Xunit;

namespace Pia.Tests.Services.MeetingAttendee;

/// <summary>
/// The pool is what makes concurrent scheduled meetings safe: a slot per meeting, its own attendee, and
/// silent capture forced on every one of them.
/// </summary>
public sealed class BackgroundMeetingSessionsTests
{
    private static ISettingsService NewSettings(int capacity)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { MaxConcurrentBackgroundMeetings = capacity });
        return settings;
    }

    private static (BackgroundMeetingSessions Pool, Func<int> Built) NewPool(int capacity)
    {
        var built = 0;
        var settings = NewSettings(capacity);
        var pool = new BackgroundMeetingSessions(
            () =>
            {
                built++;
                return new MeetingAttendeeService(
                    settings,
                    Substitute.For<IBrowserProvisioner>(),
                    Substitute.For<System.Net.Http.IHttpClientFactory>(),
                    Substitute.For<IDefaultBrowserResolver>(),
                    NullLoggerFactory.Instance,
                    Substitute.For<ILocalizationService>());
            },
            settings,
            NullLogger<BackgroundMeetingSessions>.Instance);
        return (pool, () => built);
    }

    [Fact]
    public async Task TryAcquireAsync_HandsOutUpToCapacity_ThenRefuses()
    {
        var (pool, _) = NewPool(2);

        var first = await pool.TryAcquireAsync(TestContext.Current.CancellationToken);
        var second = await pool.TryAcquireAsync(TestContext.Current.CancellationToken);
        var third = await pool.TryAcquireAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(third);
        Assert.Equal(2, pool.Active);
    }

    [Fact]
    public async Task TryAcquireAsync_ReusesASlotOnceItsLeaseIsDisposed()
    {
        var (pool, _) = NewPool(1);

        var first = await pool.TryAcquireAsync(TestContext.Current.CancellationToken);
        Assert.Null(await pool.TryAcquireAsync(TestContext.Current.CancellationToken));

        await first!.DisposeAsync();

        Assert.Equal(0, pool.Active);
        Assert.NotNull(await pool.TryAcquireAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Lease_IsIdempotent_SoADoubleDisposeCannotInflateThePool()
    {
        var (pool, _) = NewPool(1);
        var lease = await pool.TryAcquireAsync(TestContext.Current.CancellationToken);

        await lease!.DisposeAsync();
        await lease.DisposeAsync();

        // A second release would take Active negative and let the pool hand out more than its capacity.
        Assert.Equal(0, pool.Active);
    }

    [Fact]
    public async Task TryAcquireAsync_BuildsAFreshAttendeePerSlot()
    {
        var settings = NewSettings(2);
        var pool = new BackgroundMeetingSessions(
            () => new MeetingAttendeeService(
                settings,
                Substitute.For<IBrowserProvisioner>(),
                Substitute.For<System.Net.Http.IHttpClientFactory>(),
                Substitute.For<IDefaultBrowserResolver>(),
                NullLoggerFactory.Instance,
                Substitute.For<ILocalizationService>()),
            settings,
            NullLogger<BackgroundMeetingSessions>.Instance);

        var first = await pool.TryAcquireAsync(TestContext.Current.CancellationToken);
        var second = await pool.TryAcquireAsync(TestContext.Current.CancellationToken);

        // Sharing one attendee would mean the second join is refused outright — it holds a single session
        // and a single SingleReader utterance channel.
        Assert.NotSame(first!.Attendee, second!.Attendee);

        // And every one of them is pinned to the in-browser tap, which is what makes two at once safe.
        Assert.True(((MeetingAttendeeService)first.Attendee).SilentCaptureOnly);
        Assert.True(((MeetingAttendeeService)second.Attendee).SilentCaptureOnly);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task TryAcquireAsync_ReadsAnImpossibleCapacityAsOne(int configured)
    {
        var (pool, _) = NewPool(configured);

        // Zero would switch scheduled meetings off with no message anywhere; one is the honest floor.
        Assert.NotNull(await pool.TryAcquireAsync(TestContext.Current.CancellationToken));
        Assert.Null(await pool.TryAcquireAsync(TestContext.Current.CancellationToken));
    }
}
