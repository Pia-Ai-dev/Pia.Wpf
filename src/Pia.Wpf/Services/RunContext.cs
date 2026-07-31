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
