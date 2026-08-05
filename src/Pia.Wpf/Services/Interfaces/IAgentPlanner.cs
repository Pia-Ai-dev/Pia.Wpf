using Microsoft.Extensions.AI;
using Pia.Models;

namespace Pia.Services.Interfaces;

/// <summary>
/// The result of a plan/replan. <b>THREE outcomes since 18 D1 layer 2</b>, not two: an ordered set of steps to
/// execute; a signal to fall back to the <c>SingleTurn</c> path (§16 R10) — a degrade the orchestrator handles
/// by running the goal as one ordinary turn rather than recording a degenerate 1-step Planned run; or a
/// <b>DECLINE</b> (<see cref="CannotGroundGoal"/>), where the model called <c>emit_plan</c> and used it to say
/// it cannot ground the goal at all.
/// <see cref="Usage"/> carries the summed provider usage of the planning turn(s) so the orchestrator
/// accrues it run-level — a plan turn costs ≥2 provider rounds (§16 R6), doubled by the firm retry,
/// and those tokens are spent on EVERY path including the degrade and the decline (I1).
/// <para>
/// <b>Why the decline is not the R10 degrade</b> (18 spec §4.2, stated here because this record is where the
/// two would be conflated): the degrade runs the goal as ONE ORDINARY CHAT TURN and makes whatever comes back
/// the run's result. For a goal nobody could ground that is the worst branch available — it is what turned the
/// observed <c>"ggg"</c> repro into a completed run. The two outcomes are therefore mutually exclusive:
/// <see cref="FallBackToSingleTurn"/> and <see cref="CannotGroundGoal"/> are never both true, and
/// <see cref="Decline"/> is the only thing that sets the second.
/// </para>
/// </summary>
public sealed record PlanResult(
    IReadOnlyList<AgentStep> Steps,
    bool FallBackToSingleTurn,
    UsageDetails? Usage = null,
    // TRAILING and DEFAULTED, like every member this branch adds to a widely-constructed type: PlanResult is
    // built POSITIONALLY at ~30 test sites and in two planner methods, and a non-defaulted member would edit
    // all of them to say "no, this is not a decline".
    bool CannotGroundGoal = false,
    // The model's question — WHAT IT WOULD NEED TO KNOW to plan the goal. Model-generated text derived from the
    // user's own input, so it is PAYLOAD under CLAUDE.md: every consumer logs it through
    // Pia.Logging.LoggingExtensions.SensitiveDebug (or a Sensitive* sibling) and never as an argument to
    // LogInformation/LogWarning/LogDebug. Nullable on purpose — see Decline for why a decline with no wording
    // is still a decline.
    string? ClarificationQuestion = null)
{
    // A single shared instance — the fallback carries no per-call state (empty steps + the flag), so
    // re-allocating it on every access would be pure churn on the planner degrade path. A degrade that
    // DID spend tokens returns `Fallback with { Usage = … }` (records are immutable — the shared
    // instance is never mutated), mirroring VerdictResult.Accept.
    public static readonly PlanResult Fallback = new(Array.Empty<AgentStep>(), true);

    /// <summary>
    /// 18 D1 layer 2 — the THIRD outcome: the plan turn declined the goal. The orchestrator parks the run at
    /// <c>WaitingForInput</c> with the <c>needs-goal</c> reason and creates NO steps; it must never route this
    /// into <c>RunSingleTurnFallbackAsync</c> (§4.2, and the record doc above).
    /// <para>
    /// A FACTORY rather than a shared instance like <see cref="Fallback"/>, and that asymmetry is the reason
    /// stated rather than an inconsistency: the fallback carries no per-call state, a decline carries the
    /// question. The <c>with { Usage = … }</c> pattern is unchanged — callers still spend tokens on this path
    /// and still accrue them (I1).
    /// </para>
    /// <para>
    /// <paramref name="question"/> may be <c>null</c>: the FLAG is the discriminator, not the text. A model that
    /// declares it cannot ground the goal but words no question has still declared it, and treating that as "no
    /// plan" would drop it back into the R10 degrade — the exact branch this outcome exists to avoid. The
    /// surfaces cope: 18 G3's card body is a token-keyed localized string either way.
    /// </para>
    /// <para>
    /// <b>Plan turns only.</b> <see cref="IAgentPlanner.ReplanAsync"/> never returns this: its prompt does not
    /// offer the decline (18 G2 scoped layer 2 to the plan turn — a replan already has a goal the model planned
    /// once, plus the completed steps to go with it), so a caller of <c>ReplanAsync</c> may keep reading two
    /// outcomes.
    /// </para>
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
