using Microsoft.Extensions.AI;
using Pia.Models;

namespace Pia.Services.Interfaces;

/// <summary>
/// The result of a plan/replan: an ordered set of steps to execute, a signal to fall back to the
/// <c>SingleTurn</c> path (§16 R10) — a degrade the orchestrator handles by running the goal as one ordinary
/// turn rather than recording a degenerate 1-step Planned run — or a decline
/// (<see cref="CannotGroundGoal"/>) meaning the model could not ground the goal at all.
/// <see cref="FallBackToSingleTurn"/> and <see cref="CannotGroundGoal"/> are never both true.
/// <see cref="Usage"/> carries the summed provider usage of the planning turn(s) so the orchestrator
/// accrues it run-level — a plan turn costs ≥2 provider rounds (§16 R6), doubled by the firm retry,
/// and those tokens are spent on EVERY path including the degrade and the decline.
/// </summary>
public sealed record PlanResult(
    IReadOnlyList<AgentStep> Steps,
    bool FallBackToSingleTurn,
    UsageDetails? Usage = null,
    // Defaulted: PlanResult is built positionally at many call sites, and a non-defaulted member would force
    // every one of them to explicitly opt out.
    bool CannotGroundGoal = false,
    // Model-generated text derived from the user's input — payload under CLAUDE.md, so log only via
    // SensitiveDebug/Sensitive*. Nullable: a decline may state no reason and is still a decline.
    string? ClarificationQuestion = null)
{
    // A single shared instance — the fallback carries no per-call state (empty steps + the flag), so
    // re-allocating it on every access would be pure churn on the planner degrade path. A degrade that
    // DID spend tokens returns `Fallback with { Usage = … }` (records are immutable — the shared
    // instance is never mutated), mirroring VerdictResult.Accept.
    public static readonly PlanResult Fallback = new(Array.Empty<AgentStep>(), true);

    /// <summary>
    /// The plan turn declined to ground the goal: parks the run at <c>WaitingForInput</c> with the
    /// <c>needs-goal</c> reason and creates no steps, rather than falling back to a single turn.
    /// <paramref name="question"/> may be null — the flag alone is the discriminator.
    /// <see cref="IAgentPlanner.ReplanAsync"/> never returns this.
    /// </summary>
    public static PlanResult Decline(string? question) =>
        new(Array.Empty<AgentStep>(), false, CannotGroundGoal: true, ClarificationQuestion: question);
}

/// <summary>
/// Decomposes a goal into an ordered plan (<see cref="PlanAsync"/>) and revises it on step
/// failure (<see cref="ReplanAsync"/>). Both call <c>GetChatCompletionWithToolsAsync</c> with
/// <c>tools=[emit_plan]</c> and an inline capture handler. Environment-agnostic; no UI, no gate,
/// no streaming — a plan is internal metadata, not chat text (§13.1).
/// </summary>
public interface IAgentPlanner
{
    Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct);

    Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct);
}
