using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// T1-1's primitive: the live-resizable run pool behind <c>HeadlessRunLauncher._slots</c>. Everything here is
/// about the two ways a resizable semaphore goes silently wrong — over-releasing (the real cap ends up above
/// the configured one, forever) and under-releasing (a permit is lost for the lifetime of the process).
/// <para>
/// EVERY width here is measured by WHO GETS ADMITTED, never by <c>SemaphoreSlim.CurrentCount</c>, and
/// admission is read as a STATE FACT rather than timed: <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/>
/// returns an ALREADY-COMPLETED task exactly when a permit was available synchronously, so
/// <see cref="MeasureWidth"/> needs no timer and no polling at all. Only the positive "a queued waiter is
/// admitted" direction takes a bounded await, because there the thing under test is a transition rather than a
/// state.
/// </para>
/// </summary>
public class RunSlotPoolTests
{
    /// <summary>
    /// The pool's EFFECTIVE width right now: how many permits can be taken before one queues. Bounded by
    /// <paramref name="ceiling"/>, which every caller sets ABOVE the width it expects — a measurement that ran
    /// out of attempts would otherwise report a too-wide pool as correct.
    /// <para>
    /// The waiter that queues is CANCELLED rather than left hanging, so measuring the pool cannot itself
    /// swallow a permit that a later <c>Release</c> in the same fact is supposed to hand elsewhere. (The first
    /// version of this file left it hanging and the round-trip fact read one permit short — the measurement was
    /// the consumer.)
    /// </para>
    /// </summary>
    private static int MeasureWidth(RunSlotPool pool, int ceiling)
    {
        var admitted = 0;
        for (var i = 0; i < ceiling; i++)
        {
            using var cts = new CancellationTokenSource();
            var wait = pool.WaitAsync(cts.Token);
            if (wait.IsCompleted) { admitted++; continue; }

            cts.Cancel();
            // Withdrawing lost a race with a concurrent release: the waiter got the permit anyway, so it counts.
            // Not reachable in these single-threaded facts; here so the helper can never under-report.
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

        // The pre-state matters as much as the post-state: without it, a pool that admitted this waiter for any
        // reason at all would look like a successful raise.
        Assert.False(queued.IsCompleted);

        pool.Resize(3);

        // No run finished — the raise itself is what admitted this waiter.
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

        // NOT PREEMPTED: both permits are still out, so the queued waiter is still queued. A lowering that
        // stole a permit back from a running dispatch would be observable here as nothing at all — which is why
        // the observable consequence asserted is the one below, on the release side.
        Assert.False(queued.IsCompleted);

        pool.Release();

        // The first release funds the debt, not the queue: at width 1 with one run still in flight there is
        // nothing to admit. THIS is the assertion that fails if Release() hands the permit to the semaphore
        // instead of consuming the debt the lowering recorded.
        Assert.False(queued.IsCompleted);

        pool.Release();

        // Now the pool is idle at width 1, so the queued waiter is admitted and NOTHING else is.
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

        // Back at the width it started at: the raise must have CANCELLED the debt rather than released a permit
        // against it. 3 here would mean a user who nudged the slider down and back up permanently runs one
        // extra job — and permanently, because nothing ever recounts.
        Assert.Equal(2, MeasureWidth(pool, 4));
    }

    [Fact]
    public void Resize_LowerWhileAPermitIsFree_ThenRaise_NeverUnderReleases()
    {
        // The other half of the round trip, and the one that stresses the free-permit sweep: here the lowering
        // ABSORBS a free permit immediately instead of recording debt, so the raise has no debt to cancel and
        // MUST release. Getting this wrong loses a permit for the lifetime of the process — silent,
        // unrecoverable, and invisible to the fact above.
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
        // The clamp is load-bearing, not decorative: the wrapped semaphore's maxCount IS the hard cap, so an
        // unclamped Resize(99) would throw SemaphoreFullException on the resizing thread.
        var pool = new RunSlotPool(2, 8);

        pool.Resize(99);

        Assert.Equal(8, pool.Width);
        Assert.Equal(8, MeasureWidth(pool, 12));
    }

    [Fact]
    public void Resize_ClampsAWidthBelowOne()
    {
        // A hand-edited 0 (or a negative) must not produce a pool with no permits and nothing that can ever
        // release one — that is a dead scheduler, not a slower one.
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
        // How the CHILD pool stays fixed at 2 by construction rather than by convention
        // (HeadlessRunLauncher._childSlots): the nested-acquire deadlock note requires the two pools stay
        // separate numbers, so a stray Resize on the child pool must be a no-op, not a widening.
        var children = new RunSlotPool(2, 2);

        children.Resize(8);

        Assert.Equal(2, children.Width);
        Assert.Equal(2, MeasureWidth(children, 4));
    }

    // ---- T1-3: the admission chain ----

    [Fact]
    public async Task AdmitsInTicketOrder_EvenWhenTheWaitsArriveReversed()
    {
        // THE fact T1-3 exists for, and it reverses the arrival order on purpose: in the launcher the wait runs
        // as the first statement of a detached Task.Run, so which dispatch reaches the semaphore first is
        // thread-pool scheduling. Here the waits are CALLED in the order t3, t2, t1 — the worst case that
        // scheduling can produce — and admission must still be t1, t2, t3.
        //
        // On a tree where the ticket is ignored (an unordered WaitAsync), t3's wait is called first and takes
        // the only free permit, so `await w1` below never completes: the fact reds by timeout in the reversed
        // direction and passes in it here, which is exactly the difference the chain makes.
        var ct = TestContext.Current.CancellationToken;
        var pool = new RunSlotPool(1, 4);

        var t1 = pool.TakeTicket();
        var t2 = pool.TakeTicket();
        var t3 = pool.TakeTicket();

        var w3 = pool.WaitAsync(t3, ct);
        var w2 = pool.WaitAsync(t2, ct);
        var w1 = pool.WaitAsync(t1, ct);

        // t1 was issued first, so it gets the single permit even though it asked LAST.
        await w1.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.False(w2.IsCompleted);
        Assert.False(w3.IsCompleted);

        pool.Release();

        // One permit, two waiters, and the older ticket takes it. Nothing here is a stopwatch: w3 cannot even be
        // ENQUEUED until w2 is, so "w3 is not completed while w2 has just been admitted" is a state fact.
        await w2.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.False(w3.IsCompleted);

        pool.Release();
        await w3.WaitAsync(TimeSpan.FromSeconds(5), ct);
        pool.Release();
    }

    [Fact]
    public async Task ACancelledWaiterStillReleasesItsSuccessor()
    {
        // The chain's failure mode if the successor signal were not in a finally: one abandoned wait blocks every
        // later run of the process, permanently. A run cancelled at shutdown, or one whose token was already
        // cancelled when it reached the wait, is an ordinary event — not a reason to wedge the pool.
        var ct = TestContext.Current.CancellationToken;
        var pool = new RunSlotPool(1, 4);

        // Taken and DELIBERATELY never awaited: holding the head of the chain open from outside is the only way
        // to park a wait on its predecessor signal, which is the state under test. Production must not do this —
        // see TakeTicket's caller contract.
        var head = pool.TakeTicket();
        var t2 = pool.TakeTicket();
        var t3 = pool.TakeTicket();

        using var cts = new CancellationTokenSource();
        var w2 = pool.WaitAsync(t2, cts.Token);
        var w3 = pool.WaitAsync(t3, ct);

        // PRE-STATE, and it is also the chain assertion: a permit is FREE (nothing has been admitted at all),
        // so an unordered pool would have handed it to w2 immediately.
        Assert.False(w2.IsCompleted);
        Assert.False(w3.IsCompleted);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w2.WaitAsync(TimeSpan.FromSeconds(5), ct));

        // t2 gave up BEFORE it ever reached the semaphore, so it holds nothing and released nothing — the only
        // thing that can admit t3 is t2's finally handing the chain on.
        await w3.WaitAsync(TimeSpan.FromSeconds(5), ct);
        pool.Release();
        Assert.NotNull(head);
    }

    [Fact]
    public async Task ATicketFromAnotherPool_IsRejectedAndStillHandsItsOwnChainOn()
    {
        // Tickets are per-pool because the pools are (parent vs child — merging them deadlocks). Getting this
        // wrong silently orders one pool's dispatches against the other's, so it throws; and it signals first,
        // so the mistake cannot ALSO wedge the pool that issued the ticket.
        var ct = TestContext.Current.CancellationToken;
        var parent = new RunSlotPool(1, 4);
        var child = new RunSlotPool(1, 4);

        var foreign = parent.TakeTicket();
        var next = parent.TakeTicket();

        await Assert.ThrowsAsync<ArgumentException>(() => child.WaitAsync(foreign, ct));

        // The rejected ticket's successor is still admissible on its OWN pool.
        await parent.WaitAsync(next, ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        parent.Release();
    }
}
