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
    /// runs <c>RunAsync(resume: true)</c>, which skips planning entirely, so a run paused mid-plan would come
    /// back with no plan, drain zero steps and settle <c>Completed</c> having done nothing.
    /// <c>WaitingForChildren</c> — a delegating parent — is refused for now and becomes a CASCADE over the
    /// children in D6/G5; it must never simply fire the parent's own token, which the fan-out reads as a
    /// genuine cancel and settles the run terminally.
    /// </para>
    /// </summary>
    Task<bool> PauseAsync(Guid runId, CancellationToken ct = default);
}
