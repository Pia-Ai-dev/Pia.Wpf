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
    /// <para>
    /// <b>THE OWNERSHIP RULE (Batch 08 F3), and this call is where it is enforced: a pause request belongs to
    /// the dispatch whose sink was registered when the request was recorded — the sink the request actually
    /// fired — so registering a NEW sink here drops the previous one's unconsumed request, and nothing else
    /// may drop it.</b>
    /// </para>
    /// <para>
    /// WHY HERE. The rule used to be approximated by the run loop revoking any request for its own run id on
    /// entry, which is a BLIND revoke: it cannot tell a request left behind by the dispatch it superseded from
    /// one recorded against ITSELF a moment earlier. A pause landing in the resume ramp-up — after
    /// <see cref="RegisterDispatch"/>, before the loop starts — therefore fired the NEW dispatch's token and was
    /// then thrown away by that same dispatch, leaving a cancelled token with no request: the step came back
    /// cancelled, nothing consumed it, and the run settled TERMINALLY <c>Cancelled</c> with <c>CompletedAt</c>
    /// stamped, after <c>PauseAsync</c> had already told the user the pause succeeded. Registration is the exact
    /// instant ownership changes hands, so the revoke belongs here and the window closes by construction.
    /// </para>
    /// <para>
    /// ORDER: install the new sink FIRST, then drop the superseded request. The reverse order can leave a
    /// request recorded against the OLD sink standing for the new dispatch to consume — a SPURIOUS pause, the
    /// direction FAILURE DIRECTION above calls unrecoverable. This order can only lose a request, which is the
    /// direction the user can simply repeat.
    /// </para>
    /// </summary>
    void RegisterDispatch(Guid runId, Action cancel);

    /// <summary>
    /// Drop this dispatch AND any pause request it never consumed. Ownership-guarded: removes only when the
    /// stored delegate is the caller's own, mirroring <c>HeadlessRunLauncher.RemoveInflight</c>. The other half
    /// of <see cref="RegisterDispatch"/>'s ownership rule: between the two, an unconsumed request dies with the
    /// dispatch that owned it, whether that dispatch was superseded or simply finished.
    /// </summary>
    void ReleaseDispatch(Guid runId, Action ownCancel);

    /// <summary>
    /// Record a user pause request. <c>false</c> ⇒ the pause is REFUSED rather than silently dropped, for one
    /// of two reasons: no dispatch of this run is registered in this process (a run parked by a previous
    /// process has no loop here to interrupt), or this dispatch has already been given TERMINAL intent by
    /// <see cref="RevokePauseRequest"/> (Batch 08 F10 — see that method).
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
    /// Batch 08 F2: mark this run as INSIDE ITS FAN-OUT — from the moment the orchestrator commits to
    /// dispatching a parallel group until that fan-out returns. That covers the dispatch PROLOGUE (supersede
    /// the previous generation, then N × <c>LaunchChildAsync</c>: a stub chat row and workspace provisioning
    /// each, hundreds of ms to seconds) as well as the child wait itself.
    /// <para>
    /// WHY IT EXISTS. D6's rule — never fire a fan-out parent's own token — was keyed on the PERSISTED ROW
    /// reading <see cref="Pia.Models.AgentRunState.WaitingForChildren"/>, and the row does not say that until
    /// AFTER the launch loop. For the whole prologue it reads <c>Running</c>, so a pause landing there took the
    /// ordinary branch and fired the parent's CTS: the fan-out read the cancelled token, reported the run
    /// cancelled, and the caller settled it TERMINALLY with <c>CompletedAt</c> stamped and no claim path back.
    /// The dispatch knows where it is; the row does not. This flag is the dispatch telling the pause command.
    /// </para>
    /// <para>
    /// ORDER IS THE GUARANTEE, and it is a two-flag handshake rather than a lock: the pause command RECORDS
    /// its request and THEN reads this flag, while the fan-out SETS this flag and THEN reads the request. At
    /// least one of the two always sees the other, so a pause can neither be outrun into a fired parent token
    /// nor be started around. Implementations must therefore make both writes visible to the other thread —
    /// which the <c>ConcurrentDictionary</c> pair already guarantees.
    /// </para>
    /// </summary>
    void BeginFanOut(Guid runId);

    /// <summary>
    /// Clear the <see cref="BeginFanOut"/> mark. Must run on EVERY exit of the fan-out including a faulted
    /// one, because a leaked mark would make every later pause of that run cascade instead of firing its
    /// cancel — i.e. a pause on an ordinary step that never interrupts anything.
    /// </summary>
    void EndFanOut(Guid runId);

    /// <summary>True while <paramref name="runId"/> is inside its fan-out — see <see cref="BeginFanOut"/>.</summary>
    bool IsFanningOut(Guid runId);

    /// <summary>
    /// Drop a request WITHOUT honouring it — the TERMINAL-INTENT cancel paths, and only those: Stop, clear
    /// conversation, chat delete, a superseded fan-out generation, a parent's terminal cascade. The run loop
    /// deliberately does NOT call this on entry any more; that blind clear was Batch 08 F3, and the dispatch
    /// boundary it was approximating is enforced by <see cref="RegisterDispatch"/> instead.
    /// <para>
    /// <b>Batch 08 F10: this is STICKY for the rest of the dispatch, not a one-shot.</b> Terminal intent
    /// outranks a pause, and it has to keep outranking it while the cancel it accompanies UNWINDS. A one-shot
    /// revoke lost that ordering: the user presses Stop, the step takes a second to come apart, the row still
    /// reads <c>Running</c> so the panel's Pause button is still live, and a Pause pressed in that window
    /// re-armed the request — the unwinding loop then consumed it and PARKED the run instead of settling it.
    /// The run the user asked to terminate came back <c>Paused</c> with a Continue button, which is exactly the
    /// direction FAILURE DIRECTION above calls unrecoverable. It is not "last click wins": the pause command
    /// reads only the persisted row and cannot see that the dispatch behind it is already dying.
    /// </para>
    /// <para>
    /// The mark is scoped to the DISPATCH, never to the run: <see cref="ReleaseDispatch"/> clears it when this
    /// dispatch ends, and <see cref="RegisterDispatch"/> clears it when a new one takes over. A run that was
    /// Stopped and is later re-launched or resumed is therefore fully pausable again — the intent belonged to
    /// the dispatch that was cancelled, not to the run id.
    /// </para>
    /// <para>
    /// Deliberately folded into this method rather than exposed as a separate <c>MarkTerminating</c>: all five
    /// call sites are already terminal intent (they are listed above, and each one revokes precisely because
    /// it is about to cancel with no intention of coming back), so a second call would be a second thing to
    /// forget. The refusal surfaces to the user through <c>PauseAsync</c> returning <c>false</c>, which the run
    /// panel now reports rather than discards (Batch 08 F6).
    /// </para>
    /// </summary>
    void RevokePauseRequest(Guid runId);
}
