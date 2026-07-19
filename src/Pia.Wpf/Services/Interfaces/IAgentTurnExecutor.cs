using Microsoft.Extensions.AI;
using Pia.Models;

namespace Pia.Services;

/// <summary>
/// Generous, terminal budget envelope for a run (§5/§13.8). Both the step-count and the
/// wall-clock checks live in the orchestrator loop; exceeding either ends the run as
/// <c>Completed</c>+<c>truncated</c> (§16 R5), never a silent clean Completed.
/// </summary>
public sealed record RunProfile(int MaxSteps, int MaxReplans, TimeSpan WallClock)
{
    public static readonly RunProfile Interactive = new(24, 2, TimeSpan.FromMinutes(20));
    public static readonly RunProfile Scheduled = new(24, 2, TimeSpan.FromMinutes(45));

    // Bounds a user-configured budget (Assistant settings) into a sane envelope. A zero/negative
    // step or wall-clock would terminate a run immediately (never a clean run), so clamp to floors.
    public const int MinSteps = 1, MaxStepsCap = 48, MinReplans = 0, MaxReplansCap = 5, MinWallClockMinutes = 1, MaxWallClockMinutes = 120;

    /// <summary>Build an interactive profile from user-configured budget values, clamped to safe bounds.</summary>
    public static RunProfile FromBudget(int maxSteps, int maxReplans, int wallClockMinutes) => new(
        Math.Clamp(maxSteps, MinSteps, MaxStepsCap),
        Math.Clamp(maxReplans, MinReplans, MaxReplansCap),
        TimeSpan.FromMinutes(Math.Clamp(wallClockMinutes, MinWallClockMinutes, MaxWallClockMinutes)));
}

/// <summary>
/// The fully-resolved inputs for a single act step-turn. Built by the executor from the
/// active persona/provider/turn-setup + the step's intent. The step instruction is derived
/// from <see cref="Intent"/>/<see cref="ExpectedArtifact"/> (or the goal verbatim when
/// <see cref="UseGoalVerbatim"/> is set for the planner-degrade fallback) and is ephemeral —
/// never added to the transcript / persisted (§13.7).
/// </summary>
public sealed record StepTurnSpec(
    Guid RunId,
    int Ordinal,
    string Intent,
    string? ExpectedArtifact,
    string SystemPrompt,
    PersonaAttribution Persona,
    AiProvider Provider,
    IList<AITool>? Tools,
    bool SupportsTools,
    bool WebSearchActive,
    bool TokenizationEnabled,
    bool UseGoalVerbatim = false);

/// <summary>
/// The outcome of one act step-turn. Exceptions inside a step become
/// <c>Succeeded=false, Error=…</c> (never <c>ChatState.Error</c> / a RunFailed snackbar — §16 R4).
/// <see cref="FirstMessageId"/>/<see cref="LastMessageId"/> delimit the step's transcript slice
/// by STABLE message Id (§16 R3).
/// </summary>
public sealed record StepTurnResult(
    bool Succeeded,
    bool Cancelled,
    string? Error,
    string VisibleText,
    UsageDetails? Usage,
    Guid FirstMessageId,
    Guid LastMessageId);

/// <summary>
/// Runs one act step-turn in its environment. Two impls: <c>LiveTurnExecutor</c> (bound to a
/// <c>ChatSession</c>, owns UI-thread marshaling) and <c>HeadlessTurnExecutor</c> (off-thread,
/// wraps the background exchange engine). The orchestrator is thread-agnostic; each executor
/// owns its own threading (§13.1).
/// </summary>
public interface IAgentTurnExecutor
{
    /// <summary>Run-start bracket (Live: normalize the transcript; Headless: seed system+goal + TaskAmbient).</summary>
    Task BeginRunAsync(AgentRun run, RunContext ctx, CancellationToken ct);

    /// <summary>Execute one planned step; returns its result for the orchestrator to record + replan on.</summary>
    Task<StepTurnResult> ExecuteStepAsync(AgentRun run, AgentStep step, RunContext ctx, CancellationToken ct);

    /// <summary>Planner-degrade fallback (§16 R10): run the goal as one ordinary turn, no degenerate plan recorded.</summary>
    Task<StepTurnResult> RunSingleTurnFallbackAsync(AgentRun run, RunContext ctx, CancellationToken ct);

    /// <summary>
    /// Run-end bracket (Live: per-run terminal finalize mirror; Headless: persist the accumulated chat once).
    /// <paramref name="failed"/> lets the live executor distinguish a genuinely-successful run from one whose
    /// last assistant message merely carries a step catch-handler's error text — so a Failed Planned run never
    /// settles <c>ChatState.Completed</c> / raises <c>TurnCompleted(Succeeded=true)</c> (§13.5.2/§16 R4).
    /// </summary>
    Task EndRunAsync(AgentRun run, RunContext ctx, bool cancelled, bool failed, CancellationToken ct);
}
