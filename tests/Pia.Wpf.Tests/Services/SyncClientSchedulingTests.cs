using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

public class SyncClientSchedulingTests
{
    private static readonly TimeSpan BasePeriod = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(15);
    private const double JitterFraction = 0.20;

    [Fact]
    public void ComputeNextSyncDelay_BelowThreshold_UsesBasePeriod_NoJitterAtMidpoint()
    {
        // randomUnit 0.5 => jitter multiplier exactly 1.0 => the raw base period.
        var delay = SyncClientService.ComputeNextSyncDelay(consecutiveIdleCycles: 0, randomUnit: 0.5);

        Assert.Equal(BasePeriod, delay);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)] // last cycle before the backoff threshold (6)
    public void ComputeNextSyncDelay_BelowThreshold_StaysWithinJitterBandOfBase(int idleCycles)
    {
        var lower = BasePeriod.TotalMilliseconds * (1.0 - JitterFraction);
        var upper = BasePeriod.TotalMilliseconds * (1.0 + JitterFraction);

        var atMin = SyncClientService.ComputeNextSyncDelay(idleCycles, randomUnit: 0.0);
        var atMax = SyncClientService.ComputeNextSyncDelay(idleCycles, randomUnit: 0.999999);

        // At randomUnit 0 => 1 - JitterFraction (the floor); near 1 => approaches 1 + JitterFraction.
        Assert.Equal(lower, atMin.TotalMilliseconds, precision: 3);
        Assert.True(atMax.TotalMilliseconds > BasePeriod.TotalMilliseconds);
        Assert.True(atMax.TotalMilliseconds <= upper);
    }

    [Fact]
    public void ComputeNextSyncDelay_JitterIsSymmetricAroundBase()
    {
        var atMin = SyncClientService.ComputeNextSyncDelay(0, 0.0).TotalMilliseconds;
        var atMid = SyncClientService.ComputeNextSyncDelay(0, 0.5).TotalMilliseconds;
        var belowByFloor = atMid - atMin;

        // Symmetry: the ceiling sits the same JitterFraction above the base that the floor sits below it.
        Assert.Equal(BasePeriod.TotalMilliseconds * JitterFraction, belowByFloor, precision: 3);
    }

    [Fact]
    public void ComputeNextSyncDelay_AtThreshold_GrowsBeyondBase()
    {
        // At the threshold (6) the period is base * 1.5 = 7.5 min, still under the ceiling.
        var delay = SyncClientService.ComputeNextSyncDelay(consecutiveIdleCycles: 6, randomUnit: 0.5);

        Assert.True(delay > BasePeriod, $"expected backoff beyond base, got {delay}");
        Assert.True(delay <= Ceiling);
        Assert.Equal(TimeSpan.FromMinutes(7.5), delay);
    }

    [Fact]
    public void ComputeNextSyncDelay_Backoff_IsMonotonicUpToCeiling()
    {
        var previous = SyncClientService.ComputeNextSyncDelay(6, 0.5);
        for (var idle = 7; idle <= 9; idle++)
        {
            var next = SyncClientService.ComputeNextSyncDelay(idle, 0.5);
            Assert.True(next >= previous, $"period should not shrink as idle grows (idle={idle})");
            previous = next;
        }
    }

    [Theory]
    [InlineData(9)]
    [InlineData(20)]
    [InlineData(1000)]
    public void ComputeNextSyncDelay_Backoff_CapsAtCeiling_EvenWithMaxJitter(int idleCycles)
    {
        // The cap is applied BEFORE jitter, so the reachable maximum is Ceiling * (1 + JitterFraction).
        var delay = SyncClientService.ComputeNextSyncDelay(idleCycles, randomUnit: 0.999999);

        Assert.True(delay.TotalMilliseconds <= Ceiling.TotalMilliseconds * (1.0 + JitterFraction) + 1,
            $"delay {delay} exceeded jittered ceiling");
        // At the mid-jitter point a deeply-idle client sits exactly at the ceiling.
        var atMid = SyncClientService.ComputeNextSyncDelay(idleCycles, 0.5);
        Assert.Equal(Ceiling, atMid);
    }

    [Fact]
    public void ClassifyCycle_PushSentChanges_IsActive()
    {
        var outcome = SyncClientService.ClassifyCycle(pushSucceeded: true, pushSentChanges: true, pulled: 0, pullSucceeded: true, serverTimestamp: null);
        Assert.Equal(SyncCycleOutcome.Active, outcome);
    }

    [Fact]
    public void ClassifyCycle_PullReturnedRows_IsActive()
    {
        var outcome = SyncClientService.ClassifyCycle(pushSucceeded: true, pushSentChanges: false, pulled: 2, pullSucceeded: true, serverTimestamp: DateTime.UtcNow);
        Assert.Equal(SyncCycleOutcome.Active, outcome);
    }

    [Fact]
    public void ClassifyCycle_NotModified304_IsIdle()
    {
        // The 304 path returns (pulled: 0, pullSucceeded: true, serverTimestamp: null).
        var outcome = SyncClientService.ClassifyCycle(pushSucceeded: true, pushSentChanges: false, pulled: 0, pullSucceeded: true, serverTimestamp: null);
        Assert.Equal(SyncCycleOutcome.Idle, outcome);
    }

    [Fact]
    public void ClassifyCycle_FailedPull_IsInconclusive()
    {
        var outcome = SyncClientService.ClassifyCycle(pushSucceeded: true, pushSentChanges: false, pulled: 0, pullSucceeded: false, serverTimestamp: null);
        Assert.Equal(SyncCycleOutcome.Inconclusive, outcome);
    }

    [Fact]
    public void ClassifyCycle_SuccessfulPullWithTimestampButNoRows_IsInconclusive()
    {
        // A 200 that advanced the cursor but merged no rows is not a clean 304, so backoff must not advance.
        var outcome = SyncClientService.ClassifyCycle(pushSucceeded: true, pushSentChanges: false, pulled: 0, pullSucceeded: true, serverTimestamp: DateTime.UtcNow);
        Assert.Equal(SyncCycleOutcome.Inconclusive, outcome);
    }

    [Fact]
    public void ClassifyCycle_FailedPush_IsInconclusive()
    {
        // Local changes are still pending after a failed push, so backoff would slow their retry.
        var outcome = SyncClientService.ClassifyCycle(pushSucceeded: false, pushSentChanges: false, pulled: 0, pullSucceeded: true, serverTimestamp: null);
        Assert.Equal(SyncCycleOutcome.Inconclusive, outcome);
    }

    [Fact]
    public void ClassifyCycle_DeletesOrPrefsOnlyPush_IsActive()
    {
        // A deletes-only or prefs-only push has PushedCount == 0 (upserts only) but is still activity.
        var outcome = SyncClientService.ClassifyCycle(pushSucceeded: true, pushSentChanges: true, pulled: 0, pullSucceeded: true, serverTimestamp: null);
        Assert.Equal(SyncCycleOutcome.Active, outcome);
    }

    [Fact]
    public void UpdateIdleCycleCount_Active_ResetsToZero()
    {
        Assert.Equal(0, SyncClientService.UpdateIdleCycleCount(current: 5, SyncCycleOutcome.Active));
    }

    [Fact]
    public void UpdateIdleCycleCount_Idle_Increments()
    {
        Assert.Equal(6, SyncClientService.UpdateIdleCycleCount(current: 5, SyncCycleOutcome.Idle));
    }

    [Fact]
    public void UpdateIdleCycleCount_Inconclusive_LeavesUnchanged()
    {
        Assert.Equal(5, SyncClientService.UpdateIdleCycleCount(current: 5, SyncCycleOutcome.Inconclusive));
    }

    [Fact]
    public void Scheduling_SixConsecutiveIdleCycles_EngagesBackoff()
    {
        var idle = 0;
        for (var i = 0; i < 6; i++)
        {
            var outcome = SyncClientService.ClassifyCycle(pushSucceeded: true, pushSentChanges: false, pulled: 0, pullSucceeded: true, serverTimestamp: null);
            idle = SyncClientService.UpdateIdleCycleCount(idle, outcome);
        }

        Assert.Equal(6, idle);
        var delay = SyncClientService.ComputeNextSyncDelay(idle, randomUnit: 0.5);
        Assert.True(delay > BasePeriod, "six idle cycles should have engaged backoff");
    }

    [Fact]
    public void Scheduling_ActivityAfterBackoff_ResetsToBase()
    {
        var idle = 0;
        for (var i = 0; i < 10; i++)
            idle = SyncClientService.UpdateIdleCycleCount(idle, SyncCycleOutcome.Idle);
        Assert.Equal(Ceiling, SyncClientService.ComputeNextSyncDelay(idle, 0.5));

        var active = SyncClientService.ClassifyCycle(pushSucceeded: true, pushSentChanges: true, pulled: 0, pullSucceeded: true, serverTimestamp: null);
        idle = SyncClientService.UpdateIdleCycleCount(idle, active);

        Assert.Equal(0, idle);
        Assert.Equal(BasePeriod, SyncClientService.ComputeNextSyncDelay(idle, 0.5));
    }

    [Fact]
    public void Scheduling_InconclusiveCyclesDoNotAdvanceBackoff()
    {
        var idle = 0;
        for (var i = 0; i < 6; i++)
            idle = SyncClientService.UpdateIdleCycleCount(idle, SyncCycleOutcome.Inconclusive);

        Assert.Equal(0, idle);
        Assert.Equal(BasePeriod, SyncClientService.ComputeNextSyncDelay(idle, 0.5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(12)]
    public void ShouldCheckDevices_OnCadenceBoundary_ReturnsTrue(int counter)
    {
        Assert.True(SyncClientService.ShouldCheckDevices(counter));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ShouldCheckDevices_BetweenCadenceBoundaries_ReturnsFalse(int counter)
    {
        Assert.False(SyncClientService.ShouldCheckDevices(counter));
    }

    [Theory]
    [InlineData(6, 1)]   // exactly one cadence's worth of idle cycles -> 1 check
    [InlineData(12, 2)]  // two cadences -> 2 checks
    [InlineData(18, 3)]  // three cadences -> 3 checks
    [InlineData(5, 1)]   // first eligible cycle always checks (counter starts at 0)
    [InlineData(11, 2)]  // ceil(11/6) = 2
    public void AdvanceDeviceCheck_OverNCycles_ChecksEveryCadenceThCycle(int cycles, int expectedChecks)
    {
        var counter = 0;
        var checks = 0;
        for (var i = 0; i < cycles; i++)
        {
            var (shouldCheck, nextCounter) = SyncClientService.AdvanceDeviceCheck(counter, got200Pull: false);
            counter = nextCounter;
            if (shouldCheck) checks++;
        }

        Assert.Equal(expectedChecks, checks);
    }

    [Fact]
    public void AdvanceDeviceCheck_ThrottleActuallyEngages_DoesNotCheckEveryCycle()
    {
        // Resetting the counter to 0 instead of 1 made every eligible cycle check.
        var (firstCheck, counterAfterFirst) = SyncClientService.AdvanceDeviceCheck(0, got200Pull: false);
        Assert.True(firstCheck);
        Assert.Equal(1, counterAfterFirst);

        var (secondCheck, _) = SyncClientService.AdvanceDeviceCheck(counterAfterFirst, got200Pull: false);
        Assert.False(secondCheck);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void AdvanceDeviceCheck_Got200Pull_ChecksRegardlessOfCounter(int counter)
    {
        var (shouldCheck, nextCounter) = SyncClientService.AdvanceDeviceCheck(counter, got200Pull: true);

        Assert.True(shouldCheck);
        Assert.Equal(1, nextCounter);
    }
}
