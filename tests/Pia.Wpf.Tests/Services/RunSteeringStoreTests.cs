using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Batch 08 G3 — <see cref="RunSteeringStore"/>, the registry that lets a run's own loop tell a USER PAUSE
/// from a Stop and that carries the cancel sink a pause has to fire.
/// <para>
/// Every fact here is one of the four collision hardenings D1 lists, or the ownership guard hazard 7 names.
/// The store is pure in-process bookkeeping — no SQLite, no run rows — so these are the cheapest place to pin
/// the rules the orchestrator then depends on.
/// </para>
/// </summary>
public sealed class RunSteeringStoreTests
{
    /// <summary>
    /// Hardening 1, registration-scoped. A pause may not be recorded for a run nothing in THIS process is
    /// dispatching: a run parked by a previous process has no loop to interrupt, and an intent recorded against
    /// it would be honoured by whatever dispatch came next — a "pause" the user asked for minutes earlier
    /// silently aborting the first step a later Continue started. Refused, not silently dropped.
    /// </summary>
    [Fact]
    public void RecordPauseRequest_WithNoRegisteredDispatch_IsRefused()
    {
        var store = new RunSteeringStore();
        var runId = Guid.NewGuid();

        Assert.False(store.RecordPauseRequest(runId));
        Assert.False(store.TryConsumePauseRequest(runId)); // and nothing was written on the way out

        // Non-vacuity: the SAME call succeeds once a dispatch of that run is registered, so the refusal above
        // is the registration check and not a store that never records anything.
        store.RegisterDispatch(runId, () => { });
        Assert.True(store.RecordPauseRequest(runId));
    }

    /// <summary>
    /// Hazard 7, the ownership guard, in the order that actually happens: a resume dispatch registers its own
    /// sink while the PREVIOUS dispatch is still unwinding its <c>finally</c>. The old dispatch's release must
    /// drop nothing — an unguarded <c>TryRemove(runId)</c> would leave the live loop with no sink at all, i.e. a
    /// run that cannot be paused for as long as it runs, and nothing would ever repair it.
    /// </summary>
    [Fact]
    public void ReleaseDispatch_OnlyRemovesItsOwnRegistration()
    {
        var store = new RunSteeringStore();
        var runId = Guid.NewGuid();
        var firstFired = 0;
        var secondFired = 0;
        Action first = () => firstFired++;
        Action second = () => secondFired++;

        store.RegisterDispatch(runId, first);
        store.RegisterDispatch(runId, second); // the resume overwrites, deliberately
        store.ReleaseDispatch(runId, first);   // the old dispatch unwinds afterwards

        // The NEW sink survived: a pause is still recordable and still reaches the live loop.
        Assert.True(store.RecordPauseRequest(runId));
        store.FireCancel(runId);
        Assert.Equal(0, firstFired);
        Assert.Equal(1, secondFired);

        // And the owner's own release does remove it — the guard is a guard, not a no-op.
        store.ReleaseDispatch(runId, second);
        Assert.False(store.RecordPauseRequest(runId));
    }

    /// <summary>
    /// Hazard 7's second half. The launcher's <c>!started</c> arms settle the row themselves and never enter the
    /// orchestrator, so nothing there ever consumes a request — without this drop the request would outlive its
    /// dispatch and be honoured by the next one.
    /// </summary>
    [Fact]
    public void ReleaseDispatch_DropsAnUnconsumedRequest()
    {
        var store = new RunSteeringStore();
        var runId = Guid.NewGuid();
        Action cancel = () => { };

        store.RegisterDispatch(runId, cancel);
        Assert.True(store.RecordPauseRequest(runId));

        store.ReleaseDispatch(runId, cancel);

        Assert.False(store.TryConsumePauseRequest(runId));
    }

    /// <summary>
    /// Honoured EXACTLY once. The loop consumes at two sites (the step boundary and the
    /// <c>catch (OperationCanceledException)</c> arm) and a request readable twice would pause a run, then
    /// pause it again on the next abort the user did not ask for.
    /// </summary>
    [Fact]
    public void TryConsumePauseRequest_HonoursARequestExactlyOnce()
    {
        var store = new RunSteeringStore();
        var runId = Guid.NewGuid();
        store.RegisterDispatch(runId, () => { });
        Assert.True(store.RecordPauseRequest(runId));

        Assert.True(store.TryConsumePauseRequest(runId));
        Assert.False(store.TryConsumePauseRequest(runId));
    }

    /// <summary>
    /// A DISPOSED sink must not break a cascade. This is not hypothetical: the live executor's own pause hook
    /// disposes <c>session.Cts</c>, i.e. the pause destroys the very source it fired, and D6's cascade fires one
    /// sink per child in a loop — one throwing sink would abandon every child after it.
    /// </summary>
    [Fact]
    public void FireCancel_WithADisposedSink_DoesNotThrow()
    {
        var store = new RunSteeringStore();
        var runId = Guid.NewGuid();
        var cts = new CancellationTokenSource();
        cts.Dispose();
        var invoked = 0;
        store.RegisterDispatch(runId, () => { invoked++; cts.Cancel(); });

        store.FireCancel(runId);                 // the sink's throw is swallowed
        store.FireCancel(Guid.NewGuid());        // unregistered run: a no-op, not a null-deref

        Assert.Equal(1, invoked);                // non-vacuity: the sink really ran and really threw

        // The store is still usable afterwards — the swallow does not leave it wedged.
        Assert.True(store.RecordPauseRequest(runId));
        Assert.True(store.TryConsumePauseRequest(runId));
    }
}
