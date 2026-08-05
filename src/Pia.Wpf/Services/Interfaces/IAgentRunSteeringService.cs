namespace Pia.Services.Interfaces;

/// <summary>
/// Batch 08: the one entry point a UI has for STEERING a run that is executing here — currently the user
/// pause (D1/D5). It never writes a run row: every state transition belongs to the run's own loop, which
/// takes it through a CAS (<c>IAgentRunService.TryPauseUserAsync</c>). This type only records the intent and
/// fires the cancel that makes the loop notice it.
/// </summary>
public interface IAgentRunSteeringService
{
    /// <summary>
    /// Request a USER pause of a run this process is dispatching. Returns <c>false</c> when the run is not
    /// found, is not in a pausable state, or is not dispatched here (a run parked in a previous process has no
    /// loop to interrupt).
    /// <para>
    /// The pausable set is EXPLICIT and never a range (D7). <c>Planning</c> is excluded on purpose: a resume
    /// runs <c>RunAsync(resume: true)</c>, which skips planning, so a run paused mid-plan would come back with
    /// no plan, drain zero steps and settle <c>Completed</c> having done nothing.
    /// </para>
    /// <para>
    /// A resume now re-plans for one park reason — <c>needs-goal</c> with zero step rows — so "skips planning"
    /// is no longer true of every resume in general, but it stays true of every resume a USER PAUSE produces:
    /// this method's pause writes the <c>user-paused</c> token, which fails that guard's condition.
    /// </para>
    /// <para>
    /// D6: a <c>WaitingForChildren</c> parent CASCADES — its own request is recorded and every non-terminal
    /// child's cancel is fired, while the parent's own token is deliberately NEVER fired. The fan-out reads a
    /// fired parent token as a genuine cancel and settles the run terminally, which is the one way a pause
    /// turns into the thing it exists not to be.
    /// </para>
    /// </summary>
    Task<bool> PauseAsync(Guid runId, CancellationToken ct = default);
}
