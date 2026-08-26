using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Services.Operators;
using Pia.Shared.Operators;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The one shared surface read. What matters here is not the caching but its two honesty properties: a null run
/// list stays null instead of becoming an empty one, and an unanswered read leaves the TTL unarmed so the next
/// caller retries rather than being served a failure for fifteen seconds.
/// </summary>
public class AssignmentSurfaceCacheTests
{
    private static readonly DateTime Created = new(2026, 8, 26, 9, 0, 0, DateTimeKind.Utc);

    private readonly IAssignmentApiClient _api = Substitute.For<IAssignmentApiClient>();
    private readonly MovableClock _clock = new(new DateTimeOffset(Created));

    private AssignmentSurfaceCache Cache() =>
        new(_api, _clock, NullLogger<AssignmentSurfaceCache>.Instance);

    private static AssignmentSurface Available(params string[] skillNames) =>
        new(true, skillNames.Select(n => new AssignmentSkill(n, $"{n} (display)", "Assistant", [])).ToList());

    private static AssignmentDto Row(Guid id) =>
        new(id, "deep-research", "Assistant", "Completed", 2, 100, 0,
            Created, Created, Created, Created, null, null, null);

    [Fact]
    public void SurfaceIsHiddenBeforeTheFirstRefresh()
    {
        Assert.False(Cache().Surface.Available);
    }

    [Fact]
    public async Task ChangedFiresWhenAvailabilityFlips()
    {
        _api.GetSurfaceAsync(Arg.Any<CancellationToken>()).Returns(Available("deep-research"));
        var cache = Cache();
        var flips = 0;
        cache.Changed += (_, _) => flips++;

        await cache.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, flips);
        Assert.True(cache.Surface.Available);
    }

    [Fact]
    public async Task ChangedDoesNotFireWhenARefreshRepeatsTheSameAvailability()
    {
        _api.GetSurfaceAsync(Arg.Any<CancellationToken>()).Returns(Available("deep-research"));
        var cache = Cache();
        await cache.RefreshAsync(TestContext.Current.CancellationToken);

        var flips = 0;
        cache.Changed += (_, _) => flips++;

        // A different skill list is still "available", so the route rebuild hanging off this event must not run.
        _api.GetSurfaceAsync(Arg.Any<CancellationToken>()).Returns(Available("deep-research", "competitor-watch"));
        await cache.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, flips);
        Assert.Equal(2, cache.Surface.Skills.Count);
    }

    [Fact]
    public async Task ChangedFiresWhenTheSurfaceGoesAway()
    {
        _api.GetSurfaceAsync(Arg.Any<CancellationToken>()).Returns(Available("deep-research"));
        var cache = Cache();
        await cache.RefreshAsync(TestContext.Current.CancellationToken);

        var flips = 0;
        cache.Changed += (_, _) => flips++;

        _api.GetSurfaceAsync(Arg.Any<CancellationToken>()).Returns(AssignmentSurface.Hidden);
        await cache.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, flips);
        Assert.False(cache.Surface.Available);
    }

    [Fact]
    public async Task AThrowingProbeHidesTheSurfaceRatherThanPropagating()
    {
        _api.GetSurfaceAsync(Arg.Any<CancellationToken>()).Throws(new InvalidOperationException("down"));

        var surface = await Cache().RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(surface.Available);
    }

    [Fact]
    public async Task FindSkillIsOrdinalAndNullForAnUnknownName()
    {
        _api.GetSurfaceAsync(Arg.Any<CancellationToken>()).Returns(Available("deep-research"));
        var cache = Cache();
        await cache.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(cache.FindSkill("deep-research"));
        Assert.Null(cache.FindSkill("Deep-Research"));
        Assert.Null(cache.FindSkill("competitor-watch"));
        Assert.Null(cache.FindSkill(""));
    }

    [Fact]
    public void FindSkillIsNullWhileTheSurfaceIsHidden()
    {
        Assert.Null(Cache().FindSkill("deep-research"));
    }

    [Fact]
    public async Task GetRunsAsyncPropagatesNullRatherThanSubstitutingAnEmptyList()
    {
        _api.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<AssignmentDto>?)null);

        Assert.Null(await Cache().GetRunsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetRunsAsyncMakesNoSecondCallInsideTheTtl()
    {
        IReadOnlyList<AssignmentDto> rows = [Row(Guid.NewGuid())];
        _api.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(rows);
        var cache = Cache();

        await cache.GetRunsAsync(TestContext.Current.CancellationToken);
        _clock.Advance(AssignmentSurfaceCache.RunsTtl - TimeSpan.FromSeconds(1));
        var second = await cache.GetRunsAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(second);
        await _api.Received(1).ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetRunsAsyncRefetchesOnceTheTtlHasPassed()
    {
        IReadOnlyList<AssignmentDto> rows = [Row(Guid.NewGuid())];
        _api.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(rows);
        var cache = Cache();

        await cache.GetRunsAsync(TestContext.Current.CancellationToken);
        _clock.Advance(AssignmentSurfaceCache.RunsTtl + TimeSpan.FromSeconds(1));
        await cache.GetRunsAsync(TestContext.Current.CancellationToken);

        await _api.Received(2).ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnUnansweredReadLeavesTheTtlUnarmed()
    {
        _api.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<AssignmentDto>?)null);
        var cache = Cache();

        await cache.GetRunsAsync(TestContext.Current.CancellationToken);
        await cache.GetRunsAsync(TestContext.Current.CancellationToken);

        await _api.Received(2).ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailedReadAfterAGoodOneReportsFailureRatherThanStaleRows()
    {
        IReadOnlyList<AssignmentDto> rows = [Row(Guid.NewGuid())];
        _api.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(rows);
        var cache = Cache();
        await cache.GetRunsAsync(TestContext.Current.CancellationToken);

        _clock.Advance(AssignmentSurfaceCache.RunsTtl + TimeSpan.FromSeconds(1));
        _api.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<AssignmentDto>?)null);

        Assert.Null(await cache.GetRunsAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>No FakeTimeProvider package is available, and the TTL needs both directions.</summary>
    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
