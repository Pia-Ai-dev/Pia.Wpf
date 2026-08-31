using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Adds up the provider usage of the extra (non-step) turns the run loop spends — the plan/replan
/// and verify turns, each of which can run twice (the firm retry). Shared by
/// <see cref="AgentPlanner"/> and <see cref="AgentVerifier"/> so their accrual can never drift.
/// Only input/output tokens are summed: those are the only fields the run ledger reads
/// (<c>AgentRunService.AddUsageAsync</c>).
/// </summary>
internal static class AgentTurnUsage
{
    public static UsageDetails? Sum(UsageDetails? a, UsageDetails? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return new UsageDetails
        {
            InputTokenCount = (a.InputTokenCount ?? 0) + (b.InputTokenCount ?? 0),
            OutputTokenCount = (a.OutputTokenCount ?? 0) + (b.OutputTokenCount ?? 0),
        };
    }
}

/// <summary>
/// Runtime-only (not persisted) run context: the goal, the completed-step summaries, a free-form
/// scratchpad, and the budget accessors (§13.1/§B.2). <see cref="StepBudgetExceeded"/> uses
/// <see cref="StepsExecuted"/> (accrued via <see cref="RecordStep"/>) so it fires BEFORE dispatching
/// the (MaxSteps+1)th step — see the orchestrator loop ordering (§16 R5).
/// </summary>
public sealed class RunContext
{
    private readonly List<CompletedStepSummary> _completed = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly RunProfile _profile;

    public RunContext(string goal, RunProfile profile)
    {
        Goal = goal;
        _profile = profile;
    }

    public string Goal { get; }

    /// <summary>
    /// The chat working subpath the run's file tools narrow their sandbox to (<c>TaskContext.WorkingSubpath</c>),
    /// or null when the run writes at the base root. Set ONCE by the executor in <c>BeginRunAsync</c>: the
    /// per-step ambient that carries it is restored in the step's <c>finally</c> (and, for the live path,
    /// only ever set inside the UI <c>Post</c>), so by verify time on the orchestrator thread it is gone —
    /// yet the verifier's artifact probe must resolve declared artifacts against the root the steps actually
    /// wrote into, or it reports confident false NOT FOUNDs for a run that delivered everything.
    /// </summary>
    public string? WorkingSubpath { get; set; }

    /// <summary>
    /// The isolated per-run workspace root this run's file tools resolve against
    /// (<c>TaskContext.WorkspaceRoot</c>), or null when the run writes at the configured assistant-files
    /// folder. Set ONCE by the executor in <c>BeginRunAsync</c>, for the same reason
    /// <see cref="WorkingSubpath"/> is: the per-step ambient that carries it is restored in the step's
    /// <c>finally</c>, so by verify time — which runs on the ORCHESTRATOR thread, outside any step flow —
    /// it is gone. Without this the artifact probe stats the settings folder for every declared artifact of
    /// a run that wrote into its workspace, reports confident false NOT FOUNDs, burns the shared replan
    /// budget and terminates the run Completed+"unverified" — on every run (Batch 06 B3).
    /// </summary>
    public string? WorkspaceRoot { get; set; }

    /// <summary>
    /// This run is somebody's delegate. Set by the orchestrator from <c>run.ParentRunId</c> before the plan
    /// turn: a child has no surface that could show a question, so its planner is given no way to ask one —
    /// a fully-specified child goal that got declined dead-ended the whole fan-out until a person noticed.
    /// </summary>
    public bool IsDelegated { get; set; }

    // hermes #9: `public StringBuilder Scratchpad { get; } = new();` used to sit here. It was declared in the
    // original run-context sketch as the free-form carrier for "what the steps learned", and in the whole
    // repo it had no writer, no reader and no test — the one hit for the name was its own declaration.
    // It is DELETED rather than wired because this unit gives its job to a typed carrier: a step's outcome
    // now travels as StepTurnResult.Outcome -> CompletedStepSummary.Outcome, and CompletedSteps already has
    // two readers that shape the run (AgentPlanner's replan prompt and AgentVerifier's critic prompt). Wiring
    // the StringBuilder as well would mean two carriers for one fact, the untyped one being uncapped, unowned
    // and — since it would hold model prose about the user's work — the one with no privacy story.

    /// <summary>
    /// Batch 08 D4: a transient, SCOPE-TO-DISPATCH user steering note — never persisted. A fresh
    /// <see cref="RunContext"/> is built for every <c>AgentRunOrchestrator.RunAsync</c> dispatch (this run's
    /// launch, and every later resume), and this is set once from that dispatch's own optional nudge
    /// argument via <see cref="SetNudge"/>. There is no shared or process-level carrier and no persisted
    /// column: a resume that supplies no nudge starts a run whose <see cref="Nudge"/> is null again, which is
    /// the whole of "scope to the dispatch" (§1 D4's delegated sub-choice — the spec's own restore seam,
    /// <c>TryBeginResumeAsync</c>'s <c>ExtraJson=NULL</c>, runs before it could ever be read back).
    /// </summary>
    public string? Nudge { get; private set; }

    /// <summary>
    /// Cap on the flattened nudge text, matching <c>AgentPlanner.MaxAnalysisChars</c>'s head-kept shape
    /// (<c>text[..cap] + "…"</c>) — pinned from both ends by <c>RunContextNudgeTests</c>.
    /// </summary>
    private const int MaxNudgeChars = 1000;

    private const string NudgeFenceOpen = "--- Steering note from the user (follow it for the remaining steps) ---";
    private const string NudgeFenceClose = "--- end of steering note ---";

    /// <summary>
    /// Flatten CR/LF/TAB → space, trim, then cap keeping the HEAD (the same shape
    /// <c>AgentRunService.NormalizeStepText</c> uses for an edited step's title/intent) — a newline in the
    /// user's nudge text must not be able to forge extra prompt lines downstream. Blank/whitespace-only ⇒
    /// <see cref="Nudge"/> is null, never an empty-but-present string (which would otherwise render an empty
    /// fence via <see cref="AppendNudge"/>).
    /// </summary>
    public void SetNudge(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Nudge = null;
            return;
        }

        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        Nudge = flat.Length <= MaxNudgeChars ? flat : flat[..MaxNudgeChars] + "…";
    }

    /// <summary>
    /// Appends the fenced nudge to a USER-role message's text. This is the ONLY way the note reaches a
    /// provider (§1 D4 item 7): every call site wraps a <c>ChatRole.User</c> message, never a System prompt —
    /// <c>TokenizingAiClientService.TokenizeMessages</c> rewrites only <c>ChatRole.User</c> text, so a nudge
    /// folded into a System message would ship the user's raw keystrokes past the tokenizer, detokenized,
    /// even while tokenization is ON. Returns <paramref name="userText"/> unchanged when there is no nudge —
    /// callers never need to branch on <see cref="Nudge"/> themselves.
    /// </summary>
    public string AppendNudge(string userText) =>
        Nudge is null ? userText : $"{userText}\n\n{NudgeFenceOpen}\n{Nudge}\n{NudgeFenceClose}";

    public IReadOnlyList<CompletedStepSummary> CompletedSteps => _completed;

    public int StepsExecuted { get; private set; }

    public int MaxSteps => _profile.MaxSteps;

    public int MaxReplans => _profile.MaxReplans;

    public TimeSpan Elapsed => _clock.Elapsed;

    public bool StepBudgetExceeded => StepsExecuted >= _profile.MaxSteps;

    public bool WallClockExceeded => _clock.Elapsed >= _profile.WallClock;

    public void RecordStep(AgentStep step, StepTurnResult result)
    {
        StepsExecuted++;
        _completed.Add(new CompletedStepSummary(
            step.Ordinal, step.Title, step.Intent ?? string.Empty, result.Succeeded, result.VisibleText,
            step.ExpectedArtifact,
            // hermes #9: carry the step's own declaration forward, not just the boolean it produced. Without
            // it the critic cannot tell "the step said it succeeded" from "the step said SOMETHING", which is
            // the precise confusion that let a failure record as Done.
            Outcome: result.Outcome));
    }

    /// <summary>
    /// E2: seeds the steps that completed BEFORE a budget pause into a resumed run's fresh context, so
    /// the verifier and any replan judge the whole run instead of only the post-resume slice. Inserted at
    /// the front — an earlier segment always precedes this segment's steps.
    /// <para>
    /// Deliberately does NOT touch <see cref="StepsExecuted"/>: a resume is granted a FRESH step budget
    /// (that is what makes it a resume and not a continuation of the exhausted one), so counting the
    /// earlier segment against it would re-park the run immediately.
    /// </para>
    /// </summary>
    public void SeedCompletedSteps(IEnumerable<CompletedStepSummary> earlier)
        => _completed.InsertRange(0, earlier);

    /// <summary>
    /// Batch 08 F16: the titles of steps the USER removed from the plan with the panel's "Skip step" verb.
    /// <para>
    /// W13 delivered the row half of D3's promise — <c>KeepDoneAsync</c> keeps <c>Done or Skipped</c>, so a
    /// skipped row survives a replan and its <c>ExpectedArtifact</c> is never probed. But the PROMPT half was
    /// missing: <c>BuildReplanMessages</c> lists only <see cref="CompletedSteps"/> (Done-only), so nothing
    /// told the replanner a step had been removed and nothing forbade regenerating it. <c>PlanStepEdit.Skip</c>
    /// documents the intent as "a replan must not quietly re-add work they removed"; this is what makes the
    /// model aware of it at all.
    /// </para>
    /// <para>
    /// Empty until the orchestrator seeds it, which it does immediately before each replan turn from the
    /// PERSISTED plan rather than from an event: the user can skip a step at any point while the run is
    /// paused, and the DB is the only reader-independent record of what they removed.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> SkippedTitles { get; private set; } = [];

    /// <summary>Replaces (never appends to) <see cref="SkippedTitles"/> — each seed is a fresh read of the
    /// persisted plan, so accumulating would keep listing rows a later mutation dropped.</summary>
    public void SetSkippedTitles(IReadOnlyList<string> titles) => SkippedTitles = titles;

    public IReadOnlyList<PlannedStepArtifact> PlannedArtifacts { get; private set; } = [];

    /// <summary>Replaces (never appends to) <see cref="PlannedArtifacts"/> — each seed is a fresh read of the
    /// persisted plan, so accumulating would keep listing steps a later mutation dropped.</summary>
    public void SetPlannedArtifacts(IReadOnlyList<PlannedStepArtifact> artifacts) => PlannedArtifacts = artifacts;

    /// <summary>What the user answered when this run parked and asked, oldest-first. Not folded into <see cref="Goal"/> (would rewrite the user's own text) or <see cref="Nudge"/> (never persisted, so a repeat park would lose it).</summary>
    public IReadOnlyList<string> Clarifications { get; private set; } = [];

    /// <summary>Replaces (never appends to) <see cref="Clarifications"/> — the persisted column already accumulates, so appending here would double-list.</summary>
    public void SetClarifications(IReadOnlyList<string> answers) => Clarifications = answers;

    private const string ClarificationFenceOpen =
        "--- The user has since answered your clarifying question(s); plan with these as given ---";

    private const string ClarificationFenceClose = "--- end of the user's clarifications ---";

    /// <summary>Appends fenced clarification answers to a user-role message's text — user role matters because the tokenizer only rewrites <c>ChatRole.User</c> text. No-op when nothing is recorded.</summary>
    public string AppendClarifications(string userText)
    {
        if (Clarifications.Count == 0)
            return userText;

        var sb = new StringBuilder();
        sb.Append(userText).Append("\n\n").Append(ClarificationFenceOpen);
        foreach (var answer in Clarifications)
            sb.Append("\n- ").Append(answer);
        sb.Append('\n').Append(ClarificationFenceClose);
        return sb.ToString();
    }
}

public readonly record struct PlannedStepArtifact(int Ordinal, string Artifact);
