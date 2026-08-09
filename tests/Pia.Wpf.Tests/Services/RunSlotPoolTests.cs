using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

public class RunSlotPoolTests
{
    // The ceiling is always set ABOVE the expected width, so running out of attempts cannot report a too-wide
    // pool as correct; the waiter that queues is cancelled so measuring never swallows a permit.
    private static int MeasureWidth(RunSlotPool pool, int ceiling)
    {
        var admitted = 0;
        for (var i = 0; i < ceiling; i++)
        {
            using var cts = new CancellationTokenSource();
            var wait = pool.WaitAsync(cts.Token);
            if (wait.IsCompleted) { admitted++; continue; }

            cts.Cancel();
            // A withdrawal that lost a race to a concurrent release still holds the permit, so it counts.
            if (wait.IsCompletedSuccessfully) { admitted++; continue; }
            return admitted;
        }

        return admitted;
    }

    /// <summary>Takes <paramref name="count"/> permits, asserting each was granted SYNCHRONOUSLY.</summary>
    private static void TakeOrFail(RunSlotPool pool, int count)
    {
        for (var i = 0; i < count; i++)
            Assert.True(pool.WaitAsync(CancellationToken.None).IsCompleted, $"permit {i} was not available");
    }

    [Fact]
    public async Task Resize_Raise_AdmitsAQueuedWaiterImmediately()
    {
        var pool = new RunSlotPool(2, 8);
        TakeOrFail(pool, 2);

        var queued = pool.WaitAsync(CancellationToken.None);

        Assert.False(queued.IsCompleted);

        pool.Resize(3);

        // No run finished, so the raise itself is what admitted this waiter.
        await queued.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Resize_Lower_DoesNotPreemptAnInFlightRun_AndAbsorbsThePermitOnRelease()
    {
        var pool = new RunSlotPool(2, 8);
        TakeOrFail(pool, 2);
        var queued = pool.WaitAsync(CancellationToken.None);
        Assert.False(queued.IsCompleted);

        pool.Resize(1);

        Assert.False(queued.IsCompleted);

        pool.Release();

        // The first release funds the debt, not the queue: at width 1 with one run in flight there is nothing
        // to admit.
        Assert.False(queued.IsCompleted);

        pool.Release();

        await queued.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, MeasureWidth(pool, 2));
    }

    [Fact]
    public void Resize_LowerThenRaise_NeverOverReleases()
    {
        var pool = new RunSlotPool(2, 8);
        TakeOrFail(pool, 2);

        // Both permits are held, so the lowering can only be recorded as debt.
        pool.Resize(1);
        pool.Resize(2);
        pool.Release();
        pool.Release();

        // The raise must cancel the debt rather than release against it; 3 here would be a permanent extra slot.
        Assert.Equal(2, MeasureWidth(pool, 4));
    }

    [Fact]
    public void Resize_LowerWhileAPermitIsFree_ThenRaise_NeverUnderReleases()
    {
        // With a permit free the lowering absorbs it instead of recording debt, so the raise has no debt to
        // cancel and must release — getting it wrong loses a permit for the life of the process.
        var pool = new RunSlotPool(2, 8);
        TakeOrFail(pool, 1); // one taken, one FREE

        pool.Resize(1);
        pool.Resize(2);
        pool.Release();

        Assert.Equal(2, MeasureWidth(pool, 4));
    }

    [Fact]
    public void Resize_ClampsAWidthAboveTheHardCap()
    {
        // The wrapped semaphore's maxCount is the hard cap, so an unclamped raise throws SemaphoreFullException.
        var pool = new RunSlotPool(2, 8);

        pool.Resize(99);

        Assert.Equal(8, pool.Width);
        Assert.Equal(8, MeasureWidth(pool, 12));
    }

    [Fact]
    public void Resize_ClampsAWidthBelowOne()
    {
        // A 0 or negative width would be a dead scheduler: no permits, and nothing that can ever release one.
        var pool = new RunSlotPool(2, 8);

        pool.Resize(0);

        Assert.Equal(1, pool.Width);
        Assert.Equal(1, MeasureWidth(pool, 4));
    }

    [Fact]
    public void Constructor_ClampsTheInitialWidthIntoTheCap()
    {
        Assert.Equal(1, new RunSlotPool(0, 8).Width);
        Assert.Equal(8, new RunSlotPool(99, 8).Width);
        Assert.Equal(8, MeasureWidth(new RunSlotPool(99, 8), 12));
    }

    [Fact]
    public void APoolWhoseHardCapEqualsItsWidth_CannotBeWidened()
    {
        // The child pool stays fixed by construction: nested acquires deadlock unless the two pools stay separate.
        var children = new RunSlotPool(2, 2);

        children.Resize(8);

        Assert.Equal(2, children.Width);
        Assert.Equal(2, MeasureWidth(children, 4));
    }

    [Fact]
    public async Task AdmitsInTicketOrder_EvenWhenTheWaitsArriveReversed()
    {
        // The waits are called in reverse because in the launcher the wait is the first statement of a detached
        // Task.Run, so arrival order is thread-pool scheduling — admission must still follow ticket order.
        var ct = TestContext.Current.CancellationToken;
        var pool = new RunSlotPool(1, 4);

        var t1 = pool.TakeTicket();
        var t2 = pool.TakeTicket();
        var t3 = pool.TakeTicket();

        var w3 = pool.WaitAsync(t3, ct);
        var w2 = pool.WaitAsync(t2, ct);
        var w1 = pool.WaitAsync(t1, ct);

        await w1.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.False(w2.IsCompleted);
        Assert.False(w3.IsCompleted);

        pool.Release();

        await w2.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.False(w3.IsCompleted);

        pool.Release();
        await w3.WaitAsync(TimeSpan.FromSeconds(5), ct);
        pool.Release();
    }

    [Fact]
    public async Task ACancelledWaiterStillReleasesItsSuccessor()
    {
        // Without the successor signal in a finally, one abandoned wait wedges every later run of the process.
        var ct = TestContext.Current.CancellationToken;
        var pool = new RunSlotPool(1, 4);

        // The head ticket is deliberately never awaited: that is the only way to park a wait on its predecessor.
        var head = pool.TakeTicket();
        var t2 = pool.TakeTicket();
        var t3 = pool.TakeTicket();

        using var cts = new CancellationTokenSource();
        var w2 = pool.WaitAsync(t2, cts.Token);
        var w3 = pool.WaitAsync(t3, ct);

        // A permit is free, so an unordered pool would have admitted w2 immediately.
        Assert.False(w2.IsCompleted);
        Assert.False(w3.IsCompleted);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w2.WaitAsync(TimeSpan.FromSeconds(5), ct));

        // t2 never reached the semaphore, so only its finally handing the chain on can admit t3.
        await w3.WaitAsync(TimeSpan.FromSeconds(5), ct);
        pool.Release();
        Assert.NotNull(head);
    }

    [Fact]
    public async Task ATicketFromAnotherPool_IsRejectedAndStillHandsItsOwnChainOn()
    {
        // Tickets are per-pool because merging the parent and child pools deadlocks; the reject signals the
        // successor first, so the mistake cannot also wedge the issuing pool.
        var ct = TestContext.Current.CancellationToken;
        var parent = new RunSlotPool(1, 4);
        var child = new RunSlotPool(1, 4);

        var foreign = parent.TakeTicket();
        var next = parent.TakeTicket();

        await Assert.ThrowsAsync<ArgumentException>(() => child.WaitAsync(foreign, ct));

        await parent.WaitAsync(next, ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        parent.Release();
    }
}
