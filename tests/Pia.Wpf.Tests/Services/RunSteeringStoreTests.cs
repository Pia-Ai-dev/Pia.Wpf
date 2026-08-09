using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

public sealed class RunSteeringStoreTests
{
    /// <summary>A run parked by a previous process has no loop to interrupt, so an intent recorded against it
    /// would be honoured by whatever dispatch came next.</summary>
    [Fact]
    public void RecordPauseRequest_WithNoRegisteredDispatch_IsRefused()
    {
        var store = new RunSteeringStore();
        var runId = Guid.NewGuid();

        Assert.False(store.RecordPauseRequest(runId));
        Assert.False(store.TryConsumePauseRequest(runId));

        // Non-vacuity: the same call succeeds once a dispatch of that run is registered.
        store.RegisterDispatch(runId, () => { });
        Assert.True(store.RecordPauseRequest(runId));
    }

    /// <summary>A resume registers its sink while the previous dispatch is still unwinding; an unguarded remove
    /// would leave the live loop with no sink at all and nothing would repair it.</summary>
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

        Assert.True(store.RecordPauseRequest(runId));
        store.FireCancel(runId);
        Assert.Equal(0, firstFired);
        Assert.Equal(1, secondFired);

        // Non-vacuity: the owner's own release does remove it.
        store.ReleaseDispatch(runId, second);
        Assert.False(store.RecordPauseRequest(runId));
    }

    /// <summary>A request belongs to the dispatch registered when it was recorded, or the new dispatch consumes
    /// an intent the user aimed at a run that has already stopped.</summary>
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

        store.ReleaseDispatch(runId, first);
        Assert.True(store.RecordPauseRequest(runId));  // non-vacuity: `second` is still the live registration
    }

    /// <summary>Re-registering the same delegate is not a dispatch boundary: a blind drop would leave the step
    /// cancelled with no request to explain it.</summary>
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

    /// <summary>The launcher's not-started arms never enter the orchestrator, so nothing there consumes a
    /// request and it would outlive its dispatch.</summary>
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

    /// <summary>The loop consumes at two sites, so a request readable twice would pause the run again on the
    /// next abort nobody asked for.</summary>
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

    /// <summary>The live executor's pause hook disposes the very source it fired, and the cascade fires one sink
    /// per child — one throwing sink would abandon every child after it.</summary>
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

        Assert.True(store.RecordPauseRequest(runId));
        Assert.True(store.TryConsumePauseRequest(runId));
    }

    /// <summary>A leaked mark would send every later pause of that run down the cascade branch and never fire
    /// its cancel.</summary>
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

        Assert.True(store.RecordPauseRequest(fanning));
        Assert.True(store.IsFanningOut(fanning));
        Assert.True(store.TryConsumePauseRequest(fanning));
        Assert.True(store.IsFanningOut(fanning));   // consuming a request does not end a fan-out

        store.EndFanOut(fanning);
        Assert.False(store.IsFanningOut(fanning));
        Assert.True(store.RecordPauseRequest(fanning)); // … and ending one does not disturb the registration

        store.EndFanOut(Guid.NewGuid());            // clearing a mark that was never set is a no-op
    }

    /// <summary>The row still reads Running while a stopped step unwinds, so a Pause pressed in that window
    /// would re-arm the request and park a run the user asked to terminate.</summary>
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

        // Dies with the dispatch: a relaunch is pausable again.
        store.ReleaseDispatch(runId, sink);
        Action relaunch = () => { };
        store.RegisterDispatch(runId, relaunch);
        Assert.True(store.RecordPauseRequest(runId));
        Assert.True(store.TryConsumePauseRequest(runId));
    }

    /// <summary>Without the clear, a run that was stopped and later resumed from a parked state would refuse
    /// every pause for the rest of the process's life.</summary>
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

    /// <summary>Nothing would ever clear a mark left with no dispatch registered, because the release is
    /// ownership-guarded and there is no owner.</summary>
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
