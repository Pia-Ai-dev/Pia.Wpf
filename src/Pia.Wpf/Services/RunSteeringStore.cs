using System.Collections.Concurrent;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Default <see cref="IRunSteeringStore"/>. Three lock-free <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// maps keyed on the run id — the sink of the dispatch running it here, the pending pause intent, and whether
/// that dispatch is inside a fan-out — so nothing on the UI thread ever waits on the run pool (the
/// <c>ExecutingRunStore</c> discipline). Nothing here throws.
/// <para>
/// SEPARATE maps rather than one composite entry, deliberately: the sink is written and removed by the DISPATCH
/// (run pool) and the request by the PAUSE COMMAND (UI thread), so a composite value would need a
/// compare-and-swap loop to keep one writer from clobbering the other's half. The cost is that
/// <see cref="RecordPauseRequest"/>'s registration check and its write are not one atomic step: a dispatch
/// that releases in between leaves a request with no dispatch. That is the recoverable direction and it is
/// closed by the two ends of the OWNERSHIP RULE — <see cref="RegisterDispatch"/> drops the superseded
/// dispatch's request and <see cref="ReleaseDispatch"/> drops its owner's, so a stale request can never be
/// consumed by a LATER dispatch (which is the only way it could do harm).
/// </para>
/// </summary>
public sealed class RunSteeringStore : IRunSteeringStore
{
    private readonly ConcurrentDictionary<Guid, Action> _cancelByRun = new();

    /// <summary>Pending user-pause intents. The value is a placeholder — presence IS the request.</summary>
    private readonly ConcurrentDictionary<Guid, byte> _pauseRequests = new();

    /// <summary>
    /// Runs currently inside their fan-out (Batch 08 F2). A THIRD map for the same reason there are already
    /// two: this one is written by the DISPATCH and read by the PAUSE COMMAND, and folding it into either
    /// existing entry would need a compare-and-swap to keep one writer off the other's half.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, byte> _fanningOut = new();

    /// <summary>
    /// Batch 08 F3, and the whole of the ownership rule in three lines: a pause request belongs to the dispatch
    /// whose sink was registered when it was recorded, so superseding that sink is what drops the request.
    /// <para>
    /// The reference check is the "superseding" part. Re-registering the SAME delegate is not a dispatch
    /// boundary and must not eat a request that dispatch may be about to consume; only a DIFFERENT sink means
    /// a different dispatch now owns the run. First registration (no previous entry) supersedes nothing, and
    /// there is nothing to drop anyway — <see cref="RecordPauseRequest"/> refuses without a registration.
    /// </para>
    /// <para>
    /// Install THEN drop, never the other way round: dropping first leaves a hole in which a concurrent
    /// <see cref="RecordPauseRequest"/> can plant the old dispatch's request for the new one to consume — a
    /// spurious pause, the direction the interface's FAILURE DIRECTION paragraph calls unrecoverable. This
    /// order can only lose a request the user can repeat.
    /// </para>
    /// <para>
    /// Honest limit, and it is one instruction wide rather than the milliseconds F3 was: a pause command that
    /// has already written its request but not yet called <see cref="FireCancel"/> when a resume registers here
    /// loses the request and still fires the (now new) sink, i.e. a cancel with nothing to consume it. It is
    /// not reachable from the panel — <c>CanPause</c> needs Running/WaitingForChildren and <c>CanContinue</c>
    /// needs WaitingForInput/Paused, so the two buttons are never live at the same time — and closing it would
    /// mean making record-and-fire one operation, which is exactly what D6's cascade forbids.
    /// </para>
    /// </summary>
    public void RegisterDispatch(Guid runId, Action cancel)
    {
        var superseded = _cancelByRun.TryGetValue(runId, out var previous) && !ReferenceEquals(previous, cancel);
        _cancelByRun[runId] = cancel;
        if (superseded)
            _pauseRequests.TryRemove(runId, out _);
    }

    /// <summary>
    /// Ownership-guarded, and the guard matters in both directions.
    /// <list type="bullet">
    /// <item>A dispatch that never entered the orchestrator (the launcher's <c>!started</c> arms settle the
    /// row themselves, so nothing ever consumes a request) still owns its registration here, so the guard
    /// passes and its unconsumed request is dropped — otherwise the NEXT dispatch of that run would consume
    /// an intent recorded against a run that already settled.</item>
    /// <item>A dispatch unwinding while a RESUME has already registered its own sink does NOT own the entry,
    /// so it removes neither the sink nor the request. That looks like a leak and is not: any request this
    /// dispatch left unconsumed was already dropped by the resume's own <see cref="RegisterDispatch"/>, and a
    /// request standing now was recorded against the RESUME and is the resume's to honour. Removing it here
    /// would be this dispatch reaching into the next one's business — which is what F3 was.</item>
    /// </list>
    /// </summary>
    public void ReleaseDispatch(Guid runId, Action ownCancel)
    {
        if (_cancelByRun.TryGetValue(runId, out var stored) && ReferenceEquals(stored, ownCancel)
            && _cancelByRun.TryRemove(new KeyValuePair<Guid, Action>(runId, stored)))
        {
            _pauseRequests.TryRemove(runId, out _);
        }
    }

    public bool RecordPauseRequest(Guid runId)
    {
        // Registration-scoped (D1 item 5, hardening 1): the intent may not exist for a run nothing in this
        // process is running. Without this a pause on a run parked by a PREVIOUS process would be recorded
        // and then honoured by the run's next dispatch — a "pause" the user asked for minutes earlier
        // silently aborting the step a later Continue started.
        if (!_cancelByRun.ContainsKey(runId))
            return false;

        _pauseRequests[runId] = 0;
        return true;
    }

    public void FireCancel(Guid runId)
    {
        if (!_cancelByRun.TryGetValue(runId, out var cancel))
            return;

        // Never throws: a disposed CTS (the live executor disposes session.Cts inside its own pause hook)
        // must not break a cascade over the remaining children. Same rule as CancelAsync's best-effort catch.
        try { cancel(); }
        catch { /* the sink is gone; the run is already unwinding */ }
    }

    public bool TryConsumePauseRequest(Guid runId) => _pauseRequests.TryRemove(runId, out _);

    public void BeginFanOut(Guid runId) => _fanningOut[runId] = 0;

    public void EndFanOut(Guid runId) => _fanningOut.TryRemove(runId, out _);

    public bool IsFanningOut(Guid runId) => _fanningOut.ContainsKey(runId);

    public void RevokePauseRequest(Guid runId) => _pauseRequests.TryRemove(runId, out _);
}
