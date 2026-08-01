namespace Pia.Services.Interfaces;

/// <summary>
/// Batch 08 D1: the in-process registry that lets a run's own loop tell a USER PAUSE from a STOP, and that
/// carries the cancel SINK a pause has to fire to interrupt an in-flight step.
/// <para>
/// Why a registry and not a second linked CTS: on Live the only thing that releases a step blocked at
/// <c>ChatState.WaitingForTool</c> is <c>ChatSession.Cancel()</c> (which also releases the pending action
/// card — <c>ActionCardInfo.WaitForUserDecisionAsync()</c> takes no <see cref="System.Threading.CancellationToken"/>
/// at all), and for an interactive Planned run the action card is the NORMAL path, not an edge. A second
/// token cannot reach it; the sink can.
/// </para>
/// <para>
/// <b>Two operations, never one.</b> <see cref="RecordPauseRequest"/> and <see cref="FireCancel"/> are
/// deliberately separate: D6's cascade records a delegating PARENT's intent and must never fire the parent's
/// own token (that token's cancellation is read as a genuine cancel by the fan-out and settles the run
/// <c>Cancelled</c>), while it does fire every child's. A single combined call would make that rule
/// invisible in the API and leave it living in a comment.
/// </para>
/// <para>
/// THREADING: written from the UI thread (a pause command, a Stop) and from the run pool (register/release
/// inside each dispatch), read from the run pool (the loop consuming its own request). Implementations must
/// be thread-safe, lock-free, and must never make the UI thread wait on the run pool — the discipline
/// <c>IExecutingRunStore</c> states and for the same reason.
/// </para>
/// <para>
/// FAILURE DIRECTION: a MISSING request means the pause did not happen — recoverable, the user presses Pause
/// again. A SPURIOUS request means a Stop is read as a pause, i.e. a run the user wanted terminated comes back
/// resumable. So every terminal-intent path revokes before it cancels, and nothing here may throw: this is
/// bookkeeping and a fault must never break a cascade or fail a run.
/// </para>
/// </summary>
public interface IRunSteeringStore
{
    /// <summary>
    /// Register THIS dispatch's cancellation sink (the OUTER cancel — <c>HeadlessRunLauncher</c>'s per-run
    /// CTS, or <c>ChatSession.Cancel()</c>, which also releases pending action cards). Overwrites, like
    /// <c>IExecutingRunStore.Register</c>, and for the same reason: a resume dispatch may start while the
    /// previous one is still unwinding its <c>finally</c>.
    /// </summary>
    void RegisterDispatch(Guid runId, Action cancel);

    /// <summary>
    /// Drop this dispatch AND any pause request it never consumed. Ownership-guarded: removes only when the
    /// stored delegate is the caller's own, mirroring <c>HeadlessRunLauncher.RemoveInflight</c>.
    /// </summary>
    void ReleaseDispatch(Guid runId, Action ownCancel);

    /// <summary>
    /// Record a user pause request. <c>false</c> ⇒ no dispatch of this run is registered in this process,
    /// i.e. the pause is REFUSED rather than silently dropped (a run parked by a previous process has no
    /// loop here to interrupt).
    /// </summary>
    bool RecordPauseRequest(Guid runId);

    /// <summary>
    /// Invoke the registered cancel sink. No-op when nothing is registered; never throws — a disposed source
    /// must not break a cascade, the rule <c>HeadlessRunLauncher.CancelAsync</c> already follows.
    /// </summary>
    void FireCancel(Guid runId);

    /// <summary>Take the pause request, if any. Removes it: a request is honoured EXACTLY once.</summary>
    bool TryConsumePauseRequest(Guid runId);

    /// <summary>
    /// Drop a request WITHOUT honouring it — the terminal-intent cancel paths (Stop, clear conversation, chat
    /// delete, a superseded fan-out generation, a parent's terminal cascade) and the loop's clear-on-entry.
    /// </summary>
    void RevokePauseRequest(Guid runId);
}
