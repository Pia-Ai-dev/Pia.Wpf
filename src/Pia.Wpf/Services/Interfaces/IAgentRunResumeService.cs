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
    /// <param name="nudge">Batch 08 D4: an optional steering note for THIS resume dispatch. Rides only
    /// <c>ChatRole.User</c> messages (the step instruction, the critic, the replan) and never a System prompt.
    /// <para>
    /// <b>Transient — EXCEPT on a clarification park (18 D2 / owner Q3), where it is the user's ANSWER and is
    /// persisted.</b> Batch 08's rule still holds everywhere else: the note is scoped to this dispatch and is
    /// not present on a later resume unless that call supplies it again. But when the run is parked with
    /// <c>needs-goal</c> or <c>needs-input</c>, this parameter carries the reply to the question the run
    /// asked, and an answer that only rode a transient note would be lost by the next park — which 18 D4
    /// permits any number of. So the implementation appends it to <c>AgentRuns.ClarificationsJson</c>
    /// (pre-claim, since the claim NULLs <c>ExtraJson</c> and with it the token that identifies the park), it
    /// accumulates across parks, and every later plan turn of that run sees it. The run's <c>Goal</c> is not
    /// modified.
    /// </para>
    /// <para>
    /// What a caller must take from that: on a clarification park this text is DURABLE USER CONTENT. Pass
    /// what the user actually typed, and treat it under the privacy rules that implies — never a log line
    /// outside <c>Sensitive*</c>. On every other park it is the transient note Batch 08 documented.
    /// </para>
    /// </param>
    Task<bool> ResumeAsync(Guid runId, string? nudge = null, CancellationToken ct = default);
}
