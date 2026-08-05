namespace Pia.Services.Interfaces;

/// <summary>
/// Resumes a budget-paused (<see cref="Pia.Models.AgentRunState.WaitingForInput"/>) agent run.
/// Callable from both origins — the interactive RunProgress panel's Continue command and a Flow
/// ContinueRun action — with identical semantics (guardrail 6: headless parity).
/// </summary>
public interface IAgentRunResumeService
{
    /// <summary>
    /// Resume a budget-paused (or user-paused, Batch 08) run. CAS-claims it via
    /// <see cref="IAgentRunService.TryBeginResumeAsync"/> / <see cref="IAgentRunService.TryResumeFromPauseAsync"/>
    /// first, by the row's own state (guardrail 2 — returns <c>false</c> and no-ops if the run is already
    /// claimed or not parked/paused), then re-launches it headless-style on the EXISTING run id: a FRESH
    /// <c>RunContext</c> budget grant, the persisted ledger preserved, and the run's slot + workspace
    /// re-acquired. Returns <c>true</c> iff THIS call started the resume.
    /// </summary>
    /// <param name="nudge">An optional steering note for THIS resume dispatch. Rides only
    /// <c>ChatRole.User</c> messages (the step instruction, the critic, the replan) and never a System prompt.
    /// Transient in general — not persisted, and not present on a later resume unless supplied again — except
    /// on a clarification park (<c>needs-goal</c>/<c>needs-input</c>), where it is the user's answer and IS
    /// persisted (appended to <c>AgentRuns.ClarificationsJson</c>, accumulating across parks) so later plan
    /// turns see it. On a clarification park this is durable user content — log only via
    /// <c>Sensitive*</c>.</param>
    Task<bool> ResumeAsync(Guid runId, string? nudge = null, CancellationToken ct = default);
}
