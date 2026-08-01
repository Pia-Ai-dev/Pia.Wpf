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
    /// <b>Batch 08 F3 — THE OWNERSHIP RULE, side one.</b> A pause request belongs to the dispatch whose sink was
    /// registered when it was recorded, so superseding that sink drops the request. This is the case the run
    /// loop's old blind clear-on-entry was really aiming at: the previous dispatch is still unwinding, its
    /// <c>ReleaseDispatch</c> will find it no longer owns the entry and drop nothing, so if the boundary does
    /// not drop the request here the NEW dispatch consumes an intent the user aimed at a run that has already
    /// stopped — a first step silently aborted by a pause pressed minutes ago.
    /// </summary>
    [Fact]
    public void RegisterDispatch_DropsTheSupersededDispatchsUnconsumedRequest()
    {
        var store = new RunSteeringStore();
        var runId = Guid.NewGuid();
        Action first = () => { };
        Action second = () => { };

        store.RegisterDispatch(runId, first);
        Assert.True(store.RecordPauseRequest(runId));  // recorded against `first`, and never consumed

        store.RegisterDispatch(runId, second);         // the resume supersedes it

        Assert.False(store.TryConsumePauseRequest(runId));

        // …and the old dispatch unwinding afterwards changes nothing in either direction: it owns neither the
        // sink nor a request any more.
        store.ReleaseDispatch(runId, first);
        Assert.True(store.RecordPauseRequest(runId));  // non-vacuity: `second` is still the live registration
    }

    /// <summary>
    /// <b>Batch 08 F3 — THE OWNERSHIP RULE, side two, and the half that was broken.</b> A request recorded
    /// AFTER the new dispatch registered belongs to that dispatch and must survive until it is consumed. The
    /// run loop used to revoke blindly on entry, which could not tell these two cases apart: a pause landing in
    /// the resume ramp-up fired the new dispatch's token and was then thrown away by that same dispatch, so the
    /// step came back cancelled with no request to explain it and the run settled terminally.
    /// <para>
    /// Re-registering the SAME delegate is not a dispatch boundary and must not eat the request either — the
    /// reference check, asserted so a "simplification" to an unconditional drop reds.
    /// </para>
    /// </summary>
    [Fact]
    public void RegisterDispatch_DoesNotDropARequestRecordedAgainstTheNewDispatch()
    {
        var store = new RunSteeringStore();
        var runId = Guid.NewGuid();
        Action first = () => { };
        Action second = () => { };

        store.RegisterDispatch(runId, first);
        store.RegisterDispatch(runId, second);         // the ramp-up: the resume's sink is in place …
        Assert.True(store.RecordPauseRequest(runId));  // … and only NOW does the user press Pause

        store.RegisterDispatch(runId, second);         // the same sink again is not a boundary

        Assert.True(store.TryConsumePauseRequest(runId));
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

    /// <summary>
    /// Batch 08 F2, the fan-out mark. It is a per-run flag that the DISPATCH sets and the PAUSE COMMAND reads,
    /// and it must be independent of the two maps beside it — a run can be fanning out with no request standing,
    /// and a request can stand against a run that is not fanning out.
    /// <para>
    /// The clear is the half worth pinning: a leaked mark would make every later pause of that run take the
    /// cascade branch and never fire its cancel, i.e. a pause on an ordinary step that silently interrupts
    /// nothing. It is unkeyed by dispatch on purpose — the fan-out's own <c>finally</c> owns it.
    /// </para>
    /// </summary>
    [Fact]
    public void FanOutMark_IsPerRun_AndIsClearedIndependentlyOfTheRequest()
    {
        var store = new RunSteeringStore();
        var fanning = Guid.NewGuid();
        var other = Guid.NewGuid();
        store.RegisterDispatch(fanning, () => { });
        store.RegisterDispatch(other, () => { });

        Assert.False(store.IsFanningOut(fanning));  // nothing is fanning out until a fan-out says so
        Assert.False(store.IsFanningOut(Guid.NewGuid())); // an unknown run is not, rather than a null-deref

        store.BeginFanOut(fanning);
        Assert.True(store.IsFanningOut(fanning));
        Assert.False(store.IsFanningOut(other));    // per RUN, not a global flag

        // Orthogonal to the request in both directions.
        Assert.True(store.RecordPauseRequest(fanning));
        Assert.True(store.IsFanningOut(fanning));
        Assert.True(store.TryConsumePauseRequest(fanning));
        Assert.True(store.IsFanningOut(fanning));   // consuming a request does not end a fan-out

        store.EndFanOut(fanning);
        Assert.False(store.IsFanningOut(fanning));
        Assert.True(store.RecordPauseRequest(fanning)); // … and ending one does not disturb the registration

        store.EndFanOut(Guid.NewGuid());            // clearing a mark that was never set is a no-op
    }

    /// <summary>
    /// <b>Batch 08 F10: terminal intent is STICKY for the dispatch it was aimed at.</b> A one-shot revoke lost
    /// the Stop → Pause ordering: the user presses Stop, the step takes a second to unwind, the row still reads
    /// <c>Running</c> so the panel's Pause button is still live, and a Pause pressed in that window re-armed the
    /// request — which the unwinding loop then consumed, PARKING a run the user asked to terminate.
    /// <c>IRunSteeringStore</c>'s own FAILURE DIRECTION paragraph names that as the unrecoverable direction.
    /// <para>
    /// Both directions are pinned here, because the sticky half alone would be satisfied by simply refusing
    /// forever: the mark must also DIE with the dispatch, or a Stopped-then-relaunched run would be permanently
    /// unpausable with nothing to explain it.
    /// </para>
    /// </summary>
    [Fact]
    public void RevokePauseRequest_IsStickyForThatDispatch_AndDiesWithIt()
    {
        var store = new RunSteeringStore();
        var runId = Guid.NewGuid();
        Action sink = () => { };
        store.RegisterDispatch(runId, sink);

        Assert.True(store.RecordPauseRequest(runId));   // non-vacuity: pausable before the terminal intent
        store.RevokePauseRequest(runId);                // the Stop
        Assert.False(store.TryConsumePauseRequest(runId), "the revoke must drop the standing request");
        Assert.False(store.RecordPauseRequest(runId), "and refuse the next one while this dispatch unwinds");
        Assert.False(store.TryConsumePauseRequest(runId), "so the unwinding loop finds nothing to honour");

        // Dies with the dispatch: a re-LAUNCH (release, then a fresh registration) is pausable again.
        store.ReleaseDispatch(runId, sink);
        Action relaunch = () => { };
        store.RegisterDispatch(runId, relaunch);
        Assert.True(store.RecordPauseRequest(runId));
        Assert.True(store.TryConsumePauseRequest(runId));
    }

    /// <summary>
    /// Batch 08 F10's other exit: a RESUME (a new sink registered while the old one is still installed) is a new
    /// dispatch and clears the mark too — otherwise a run that was Stopped, settled, and later resumed from a
    /// parked state would silently refuse every pause for the rest of the process's life.
    /// <para>
    /// Also pins the ORDER the store documents: the mark is cleared only on a SUPERSEDING registration, i.e. one
    /// that installs a different sink. Re-registering the same delegate is not a dispatch boundary.
    /// </para>
    /// </summary>
    [Fact]
    public void ANewDispatchsRegistration_ClearsTheTerminalMark_ButReRegisteringTheSameSinkDoesNot()
    {
        var store = new RunSteeringStore();
        var runId = Guid.NewGuid();
        Action first = () => { };
        store.RegisterDispatch(runId, first);
        store.RevokePauseRequest(runId);
        Assert.False(store.RecordPauseRequest(runId));

        store.RegisterDispatch(runId, first);          // the SAME sink: not a boundary
        Assert.False(store.RecordPauseRequest(runId), "re-registering the same dispatch must not clear its own terminal intent");

        Action second = () => { };
        store.RegisterDispatch(runId, second);         // a genuinely new dispatch
        Assert.True(store.RecordPauseRequest(runId));
    }

    /// <summary>
    /// Batch 08 F10, the guard on the mark itself: a revoke for a run with NO dispatch registered must not
    /// leave a mark behind, because nothing would ever clear it — <c>ReleaseDispatch</c> is ownership-guarded
    /// and there is no owner. The run would then be unpausable for the rest of the process's life the first
    /// time it was actually dispatched.
    /// </summary>
    [Fact]
    public void RevokingAnUndispatchedRun_LeavesNoMarkForItsNextDispatchToInherit()
    {
        var store = new RunSteeringStore();
        var runId = Guid.NewGuid();

        store.RevokePauseRequest(runId); // e.g. the chat-delete path firing at a run nothing here is running

        store.RegisterDispatch(runId, () => { });
        Assert.True(store.RecordPauseRequest(runId));
    }
}
