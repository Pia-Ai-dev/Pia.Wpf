using Microsoft.Extensions.AI;
using Pia.Models;

namespace Pia.Services.Interfaces;

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
    bool UseGoalVerbatim = false,

    /// <summary>
    /// The run's autonomy policy (Batch 04), or null ⇒ no per-run policy, i.e. today's behaviour: every write
    /// the allowlist and the standing grants do not cover shows an action card. Appended and defaulted so the
    /// interactive single-turn path and every existing construction stay unchanged.
    /// </summary>
    RunAutonomyPolicy? Policy = null,

    /// <summary>
    /// The audit-timeline sink for this step (Batch 03), which carries the step id itself
    /// (<c>AgentTimelineScope.StepId</c>). Null ⇒ emit nothing, which is what every non-run turn passes.
    /// Appended and defaulted for the same reason <see cref="Policy"/> was: both construction sites use named
    /// arguments and nothing asserts spec equality, so the ordinary interactive path and every existing test
    /// stay unchanged.
    /// <para>
    /// There is deliberately NO separate <c>StepId</c> on this record. One existed, was written by
    /// <c>LiveTurnExecutor.BuildSpec</c> and read by nobody, and attribution came from the scope — two sources
    /// of truth for one fact, the dead one being the documented one. A later executor that set the field and
    /// built a run-level scope would have persisted <c>StepId = NULL</c> for every row with nothing failing.
    /// </para>
    /// </summary>
    AgentTimelineScope? Timeline = null);

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

/// <summary>Summary of a completed step, carried forward as context for later steps + replanning.</summary>
/// <param name="ExpectedArtifact">
/// The deliverable the planner declared for this step (free text; SENSITIVE user content — a prompt may
/// carry it, a log may not). Carried here so the verifier can probe it against the filesystem instead of
/// judging the model's self-summary alone (H1); the loop already holds the step, so this is strictly
/// cheaper than re-reading the persisted plan at verify time.
/// </param>
/// <param name="FromEarlierSegment">
/// True for a step seeded from persistence on RESUME — it ran in an earlier segment of this same run,
/// before the budget pause (E2). Its <paramref name="VisibleText"/> is not recoverable from the run
/// context today, so prompts must say the result text is unavailable rather than imply the step never
/// happened.
/// </param>
public sealed record CompletedStepSummary(
    int Ordinal, string Title, string Intent, bool Succeeded, string VisibleText,
    string? ExpectedArtifact = null, bool FromEarlierSegment = false)
{
    /// <summary>
    /// What every prompt puts where a <see cref="FromEarlierSegment"/> step's result text would go. One
    /// shared string so the critic and the replan judge are told the same thing: the step RAN, its text
    /// just is not in this context — the alternative (an empty result) reads like a step that did nothing.
    /// </summary>
    public const string EarlierSegmentNote =
        "(completed before this run was paused for budget; its result text is not available in this context — treat it as executed, not as missing)";
}

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

    /// <summary>
    /// Non-terminal budget-pause hook (guardrail 5): the orchestrator calls this on the pause exit
    /// INSTEAD of <see cref="EndRunAsync"/> when a run parks into <c>WaitingForInput</c>. Unlike a
    /// terminal end, a pause must NOT settle <c>ChatState.Completed</c>/<c>Error</c> or raise
    /// <c>TurnCompleted</c>. Live: release the live session (dispose the CTS + settle
    /// <c>ChatState.Idle</c>) so <c>IsStreaming</c> clears and Send/RunInBackground re-enable while the
    /// run sits parked. Headless: no-op (nothing to release; the persisted chat/steps/ledger already
    /// carry the state, and finalizing here would erase pre-existing rows).
    /// </summary>
    Task OnPausedAsync(AgentRun run, RunContext ctx, CancellationToken ct);
}
