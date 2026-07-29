using Microsoft.Extensions.AI;
using Pia.Models;

namespace Pia.Services.Interfaces;

/// <summary>
/// The terminal critic verdict (§13.x). Either an ACCEPT (the run achieved its goal) or a FAIL
/// with a reason + the concrete missing items, which feeds the shared failure-only replan loop.
/// Degrade-safe: a no-call / invalid / crashed verify yields <see cref="Accept"/> (mirrors the
/// planner's <c>PlanResult.Fallback</c> degrade, but the verifier's safe default is ACCEPT).
/// <see cref="Usage"/> carries the summed provider usage so the orchestrator accrues it run-level.
/// </summary>
public sealed record VerdictResult(
    bool Passed,
    string? Reason,
    IReadOnlyList<string> Missing,
    UsageDetails? Usage)
{
    // Shared zero-state accept — re-alloc per degrade would be churn (mirrors PlanResult.Fallback).
    public static readonly VerdictResult Accept = new(true, null, Array.Empty<string>(), null);
}

/// <summary>
/// Terminal critic (§13.x): judges whether a completed run achieved its goal / expected artifacts,
/// reusing the run's resolved persona+provider (like <see cref="IAgentPlanner"/>). Reads the
/// <see cref="RunContext"/> (Goal + CompletedSteps, i.e. the run's self-reported results) and — since
/// H1 — one piece of mechanical evidence: each completed step's declared <c>ExpectedArtifact</c> is
/// probed (metadata only, inside the file sandbox) so the verdict is not a self-critique over
/// self-summaries. Never reads the run store. Executor-agnostic; no UI, no gate — a verdict is
/// internal metadata, not chat text.
/// </summary>
public interface IAgentVerifier
{
    Task<VerdictResult> VerifyAsync(RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct);
}
