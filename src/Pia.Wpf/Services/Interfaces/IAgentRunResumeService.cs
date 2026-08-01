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
    /// <param name="nudge">Batch 08 D4: an optional transient steering note, scoped to THIS resume dispatch
    /// only — never persisted, and never present on a later resume unless that call supplies it again. Rides
    /// only <c>ChatRole.User</c> messages (the step instruction, the critic, the replan) and never a System
    /// prompt.</param>
    Task<bool> ResumeAsync(Guid runId, string? nudge = null, CancellationToken ct = default);
}
