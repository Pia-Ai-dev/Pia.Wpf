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
    public const int MinSteps = 1, MaxStepsCap = 48, MinReplans = 0, MaxReplansCap = 5, MinWallClockMinutes = 1, MaxWallClockMinutes = 120, MinToolRounds = 1, MaxToolRoundsCap = 20;

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
    AgentTimelineScope? Timeline = null,

    /// <summary>
    /// The run's isolated workspace root (Batch 06 D4), or null ⇒ no isolation, i.e. this step's file tools
    /// resolve against the interactive assistant files folder exactly as they did before Batch 06.
    /// <para>
    /// WHAT IT IS FOR: <c>ChatSession.RunStepTurnAsync</c> hands this to <c>TaskContext.WorkspaceRoot</c>, and
    /// that ambient is the ONLY thing that confines an interactive <see cref="RunShape.Planned"/> run's
    /// reads and writes to <c>runs\&lt;runId&gt;</c>. <c>LiveTurnExecutor.BuildSpec</c> is its only producer:
    /// this member is trailing and defaulted (the precedent <see cref="Policy"/> and <see cref="Timeline"/>
    /// set, for the same reason — both construction sites use named arguments), so DROPPING it from
    /// <c>BuildSpec</c> still compiles and silently un-isolates every interactive step. Whoever rewrites the
    /// members around it must keep passing it.
    /// </para>
    /// <para>
    /// When it is set, the step's ambient working subpath is null: the workspace root already IS the narrowed
    /// root (B6), so narrowing a second time would probe <c>&lt;runRoot&gt;\&lt;subpath&gt;</c>.
    /// </para>
    /// </summary>
    string? WorkspaceRoot = null);

/// <summary>
/// The outcome a step DECLARED for itself by calling <c>emit_step_result</c> (hermes #9).
/// <para>
/// This exists because both executors used to infer step success from "the model produced non-empty text",
/// so a step that ran, failed, and then eloquently EXPLAINED its failure recorded
/// <c>AgentStepStatus.Done</c> and the run marched on with a false premise. A claim is the model's own
/// structured verdict and OVERRIDES the text heuristic in both directions: <see cref="Succeeded"/> false
/// with a page of prose is a Failed step, and true with no visible text at all is a Done one.
/// </para>
/// <para>
/// It lives here rather than beside <c>StepOutcomeStore</c> in the <c>Pia.Services</c> root because it is a
/// CONTRACT — <see cref="StepTurnResult"/> and <see cref="CompletedStepSummary"/> below both carry it, and
/// contracts belong with the interface they serve (the rule
/// <c>NamingConventionTests.RecordTypes_MustNotLiveInTheServicesRootNamespace</c> enforces).
/// </para>
/// <para>
/// SENSITIVITY: <see cref="Summary"/> and <see cref="ArtifactRef"/> are model-authored free text about the
/// user's work — prompt-safe, log-unsafe. They may only reach <c>SensitiveDebug</c> (CLAUDE.md). Both are
/// flattened and capped by <c>StepOutcomeStore</c> at parse time, because both are rendered into later
/// prompts as their own lines and a newline would otherwise let a step's self-report imitate a surrounding
/// fact line — the same guard <c>AgentVerifier.Flatten</c> and <c>RunContext.SetNudge</c> apply.
/// </para>
/// </summary>
public sealed record StepOutcomeClaim(bool Succeeded, string Summary, string? ArtifactRef);

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
    Guid LastMessageId,

    /// <summary>
    /// What the step DECLARED about itself via <c>emit_step_result</c> (hermes #9), or null when it declared
    /// nothing — in which case <see cref="Succeeded"/> above was inferred from the old non-empty-text
    /// heuristic and is UNCONFIRMED.
    /// <para>
    /// This is the record of HOW the verdict was reached, not the verdict: both executors have already folded
    /// a present claim into <see cref="Succeeded"/>/<see cref="Error"/>, so the orchestrator's
    /// <c>Done : Failed</c> mapping needs no change. Trailing and defaulted — the precedent every other
    /// appended member on these records set — so the fake executors in the suite stay source-compatible.
    /// </para>
    /// </summary>
    StepOutcomeClaim? Outcome = null,

    /// <summary>
    /// hermes #16. The tool this step stopped on because it needs a human decision, or null (every other
    /// step, and every step of every LIVE run — the interactive gate has a card and never parks).
    /// <para>
    /// Non-null means the step DID NOT FINISH and must not be recorded: the orchestrator puts its row back to
    /// <c>Pending</c>, bills the tokens run-level, and parks the run at <c>WaitingForInput</c> naming this
    /// tool. It is deliberately NOT folded into <see cref="Succeeded"/>/<see cref="Cancelled"/> the way
    /// <see cref="Outcome"/> was folded into <see cref="Succeeded"/>: a parked step usually ALSO reports
    /// <c>Succeeded:false</c> (the model was denied a tool and often says so through
    /// <c>emit_step_result</c>), and reading that as an ordinary failure would burn a replan on a step that
    /// is merely waiting.
    /// </para>
    /// <para>
    /// The NAME only. The pending call itself cannot survive a park — a park outlives the process — so what
    /// the human approves is the capability, and the resumed step re-issues the call itself.
    /// </para>
    /// </summary>
    string? ApprovalRequiredTool = null,

    /// <summary>
    /// The question this step asked through <c>request_user_input</c>, or null. Deliberately not folded into
    /// <see cref="Succeeded"/>/<see cref="Cancelled"/>: a step that stopped to ask often also reports failure,
    /// and treating that as an ordinary failure would burn a replan on a step that is merely waiting.
    /// Model-generated content — <c>SensitiveDebug</c> only.
    /// </summary>
    string? UserInputQuestion = null);

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
/// <param name="Outcome">
/// The step's own <c>emit_step_result</c> declaration (hermes #9), or null when it never made one. This is
/// what carries the structured signal ACROSS steps: <c>AgentVerifier</c> renders it so the critic can tell a
/// self-declared "ok" from an "ok" that only means the step emitted some text, and a step that declared
/// failure now shows up as <c>[failed]</c> in the replan prompt instead of as clean work.
/// </param>
public sealed record CompletedStepSummary(
    int Ordinal, string Title, string Intent, bool Succeeded, string VisibleText,
    string? ExpectedArtifact = null, bool FromEarlierSegment = false, StepOutcomeClaim? Outcome = null)
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

    /// <summary>
    /// T2-18 — the GRACE TURN: one final, TOOL-FREE turn spent just before a BUDGET park, so the run leaves a
    /// readable "here is where I got to" instead of stopping mid-plan with no closing word. Returns the turn's
    /// result so the orchestrator can bill it and extend the run's transcript range, or <see langword="null"/>
    /// for "this executor spends no grace turn".
    /// <para>
    /// It spends a provider round AFTER the budget was exceeded, and that is the deliberate trade rather than an
    /// oversight: exactly one round, tool-free (so it cannot write files or call anything past the cap), bounded
    /// by its own short timeout at the call site, and it must never prevent the park — the orchestrator wraps it
    /// so a fault, a timeout or an already-cancelled token still parks the run.
    /// </para>
    /// <para>
    /// DEFAULTED to "no grace turn", which was the first default-interface member in this codebase (see
    /// <see cref="MirrorClarificationQuestionAsync"/> below for the second) and is here for the reason every
    /// trailing-and-defaulted constructor parameter on this spine gives: twelve types implement this interface
    /// (two production, ten hand-written test fakes), the correct behaviour for all but one of them is to do
    /// nothing, and a required member would edit a dozen files to write <c>return null</c> in each. This is an
    /// optional enhancement, not an authority question — the members that MUST be answered out loud (see
    /// <c>ToolGateInput.CanPark</c>) are required precisely because they are not this.
    /// </para>
    /// <para>
    /// <c>LiveTurnExecutor</c> keeps the default on purpose: an interactive run's transcript is on screen and the
    /// person watched it stop, so a wrap-up buys nothing there — and posting a model turn through the UI at pause
    /// time would race the session release <see cref="OnPausedAsync"/> exists to do.
    /// </para>
    /// </summary>
    Task<StepTurnResult?> RunGraceTurnAsync(AgentRun run, RunContext ctx, CancellationToken ct)
        => Task.FromResult<StepTurnResult?>(null);

    /// <summary>
    /// Mirrors a clarification question into this executor's own live transcript copy, if it has one — the
    /// durable write already happened before this hook runs. Headless has no live copy and keeps the default
    /// no-op; <c>LiveTurnExecutor</c> overrides it so the question renders immediately instead of waiting for
    /// the session's next persist.
    /// </summary>
    /// <param name="messageId">The row id the durable write already used; implementations must reuse it rather
    /// than minting a new one.</param>
    Task MirrorClarificationQuestionAsync(
        AgentRun run, RunContext ctx, Persona persona, Guid messageId, string question, CancellationToken ct)
        => Task.CompletedTask;
}
