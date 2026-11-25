using System.Globalization;
using System.Text;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>The job block appended to a conversation turn's last user message. Model-facing, so English.</summary>
public static class JobTurnBlockBuilder
{
    public static string Handle(Guid stepId) => stepId.ToString("N")[..8];

    public static string Build(
        AgentRun run, IReadOnlyList<JobPlanChange> queued, IReadOnlySet<Guid> changed, bool pauseAfterStep,
        RunToolQuestion? pendingAsk, string? userInputQuestion, int droppedChanges, DateTime utcNow)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- Job block (the user's running job; current as of this message) ---");
        sb.Append("State: ").AppendLine(DescribeState(run));
        if (DescribeWait(run, pendingAsk, userInputQuestion) is { } wait)
            sb.Append("Waiting for: ").AppendLine(wait);
        if (pauseAfterStep)
            sb.AppendLine("A pause after the running step is requested.");

        var settled = run.Plan.Where(s => s.Status != AgentStepStatus.Pending).OrderBy(s => s.Ordinal).ToList();
        var (tail, touched, _) = JobPlanChanges.Apply(run.Id, JobPlanChanges.PendingTail(run), queued);
        var marked = new HashSet<Guid>(changed);
        marked.UnionWith(touched);

        sb.AppendLine("Steps:");
        var n = 1;
        foreach (var step in settled.Concat(tail))
        {
            sb.Append(CultureInfo.InvariantCulture, $"{n++}. [{Handle(step.Id)}] {StatusWord(step.Status)}: {step.Title}");
            if (step.Status == AgentStepStatus.Running)
            {
                var minutes = Math.Max(0, (utcNow - step.UpdatedAt).TotalMinutes);
                sb.Append(CultureInfo.InvariantCulture, $" (running for {minutes:0.#} min)");
            }
            if (step.Status == AgentStepStatus.Pending && marked.Contains(step.Id))
                sb.Append(" (changed from this chat)");
            sb.AppendLine();
        }

        if (queued.Count > 0)
            sb.AppendLine("Plan changes from this chat are queued; the job applies them before its next step.");
        if (droppedChanges > 0)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"{droppedChanges} plan change(s) could not be applied because their step had already started.");
        sb.Append("--- end of job block ---");
        return sb.ToString();
    }

    private static string DescribeState(AgentRun run) => run.State switch
    {
        AgentRunState.Planning => "planning",
        AgentRunState.Running => "working",
        AgentRunState.Verifying => "checking the result",
        AgentRunState.WaitingForChildren => "waiting for its sub-tasks",
        AgentRunState.Paused => "paused",
        AgentRunState.WaitingForInput => "stopped, waiting",
        _ => run.State.ToString().ToLowerInvariant(),
    };

    private static string? DescribeWait(AgentRun run, RunToolQuestion? pendingAsk, string? userInputQuestion)
    {
        if (pendingAsk is not null)
            return $"the user to allow or deny the tool {pendingAsk.ToolName} on the job block";
        if (!AgentRunStates.IsParked(run.State))
            return null;

        var reason = RunPauseEnvelope.ReadReason(run);
        return reason switch
        {
            AgentRunOrchestrator.PlanApprovalReason => "the user to start the plan with Start on the job block",
            AgentRunOrchestrator.ToolApprovalReason =>
                $"the user to decide on the tool {RunPauseEnvelope.ReadApprovalTool(run) ?? "call"} on the job block",
            AgentRunOrchestrator.NeedsInputReason =>
                "an answer to the job's question" + (userInputQuestion is { } q ? $": {q}" : string.Empty) + " (use job_answer)",
            AgentRunOrchestrator.NeedsGoalReason => "the user to clarify the goal",
            "step-cap" or "wall-clock" => "a continue: it stopped at its budget (job_continue)",
            AgentRunService.UserPausedReason => "a continue: the user paused it (job_continue)",
            _ when AgentRunOrchestrator.IsInterruptionReason(reason) || reason == HeadlessRunLauncher.ResumeInterruptedReason =>
                "a continue: it was stopped by an interruption (job_continue)",
            _ => "a continue (job_continue)",
        };
    }

    private static string StatusWord(AgentStepStatus status) => status switch
    {
        AgentStepStatus.Pending => "to do",
        AgentStepStatus.Running => "running",
        AgentStepStatus.Done => "done",
        AgentStepStatus.Failed => "failed",
        AgentStepStatus.Skipped => "skipped",
        _ => status.ToString(),
    };
}
