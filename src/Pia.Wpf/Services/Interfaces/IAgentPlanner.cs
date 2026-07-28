using Microsoft.Extensions.AI;
using Pia.Models;

namespace Pia.Services;

/// <summary>
/// The result of a plan/replan. Either an ordered set of steps to execute, or a signal to
/// fall back to the <c>SingleTurn</c> path (§16 R10) — a degrade the orchestrator handles by
/// running the goal as one ordinary turn rather than recording a degenerate 1-step Planned run.
/// <see cref="Usage"/> carries the summed provider usage of the planning turn(s) so the orchestrator
/// accrues it run-level — a plan turn costs ≥2 provider rounds (§16 R6), doubled by the firm retry,
/// and those tokens are spent on EVERY path including the degrade (I1).
/// </summary>
public sealed record PlanResult(
    IReadOnlyList<AgentStep> Steps,
    bool FallBackToSingleTurn,
    UsageDetails? Usage = null)
{
    // A single shared instance — the fallback carries no per-call state (empty steps + the flag), so
    // re-allocating it on every access would be pure churn on the planner degrade path. A degrade that
    // DID spend tokens returns `Fallback with { Usage = … }` (records are immutable — the shared
    // instance is never mutated), mirroring VerdictResult.Accept.
    public static readonly PlanResult Fallback = new(Array.Empty<AgentStep>(), true);
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
