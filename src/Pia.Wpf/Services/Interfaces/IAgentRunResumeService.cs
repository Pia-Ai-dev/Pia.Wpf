namespace Pia.Services.Interfaces;

/// <summary>
/// Resumes a budget-paused (<see cref="Pia.Models.AgentRunState.WaitingForInput"/>) agent run.
/// Callable from both origins — the interactive RunProgress panel's Continue command and a Flow
/// ContinueRun action — with identical semantics (guardrail 6: headless parity).
/// </summary>
public interface IAgentRunResumeService
{
    /// <summary>
    /// Resume a budget-paused run. CAS-claims it via
    /// <see cref="IAgentRunService.TryBeginResumeAsync"/> first (guardrail 2 — returns <c>false</c> and
    /// no-ops if the run is already claimed or not parked), then re-launches it headless-style on the
    /// EXISTING run id: a FRESH <c>RunContext</c> budget grant, the persisted ledger preserved, and the
    /// run's slot + workspace re-acquired. Returns <c>true</c> iff THIS call started the resume.
    /// </summary>
    Task<bool> ResumeAsync(Guid runId, CancellationToken ct = default);
}
