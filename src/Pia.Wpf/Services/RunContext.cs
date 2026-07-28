using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;
using Pia.Models;

namespace Pia.Services;

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
