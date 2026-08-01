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

    public StringBuilder Scratchpad { get; } = new();

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
            step.ExpectedArtifact));
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
}
