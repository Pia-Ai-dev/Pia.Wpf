using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

internal static class JobPlanChanges
{
    private const int MaxNoteChars = 1000;

    /// <summary>Applies changes to copies of the pending tail; a change whose step is not in it is dropped.</summary>
    internal static (List<AgentStep> Tail, IReadOnlyList<Guid> Touched, int Dropped) Apply(
        Guid runId, IReadOnlyList<AgentStep> pendingTail, IReadOnlyList<JobPlanChange> changes)
    {
        var tail = pendingTail.Select(Clone).ToList();
        var touched = new List<Guid>();
        var dropped = 0;

        foreach (var change in changes)
        {
            var index = tail.FindIndex(s => s.Id == change.StepId);
            switch (change.Kind)
            {
                case JobPlanChangeKind.Edit when index >= 0:
                    var title = Normalize(change.Title, AgentRunService.MaxStepTitleChars);
                    var intent = Normalize(change.Intent, AgentRunService.MaxStepIntentChars);
                    if (title.Length > 0)
                    {
                        tail[index].Title = title;
                        if (intent.Length == 0)
                            tail[index].Intent = title;
                    }
                    if (intent.Length > 0)
                        tail[index].Intent = intent;
                    touched.Add(change.StepId);
                    break;

                case JobPlanChangeKind.Insert when index < 0:
                    var newTitle = Normalize(change.Title, AgentRunService.MaxStepTitleChars);
                    if (newTitle.Length == 0)
                    {
                        dropped++;
                        break;
                    }
                    var newIntent = Normalize(change.Intent, AgentRunService.MaxStepIntentChars);
                    tail.Insert(Placement(tail, change), new AgentStep
                    {
                        Id = change.StepId,
                        RunId = runId,
                        Title = newTitle,
                        Intent = newIntent.Length > 0 ? newIntent : newTitle,
                        Status = AgentStepStatus.Pending,
                        ReRunnable = true,
                    });
                    touched.Add(change.StepId);
                    break;

                case JobPlanChangeKind.Remove when index >= 0:
                    tail.RemoveAt(index);
                    touched.Remove(change.StepId);
                    break;

                case JobPlanChangeKind.Move when index >= 0:
                    var moved = tail[index];
                    tail.RemoveAt(index);
                    tail.Insert(Placement(tail, change), moved);
                    touched.Add(change.StepId);
                    break;

                default:
                    dropped++;
                    break;
            }
        }

        return (tail, touched.Distinct().ToList(), dropped);
    }

    /// <summary>The settled prefix in its order, then the new tail, ordinals contiguous from zero.</summary>
    internal static List<AgentStep> Compose(IReadOnlyList<AgentStep> plan, IReadOnlyList<AgentStep> tail)
    {
        var rows = plan.Where(s => s.Status != AgentStepStatus.Pending).OrderBy(s => s.Ordinal).ToList();
        rows.AddRange(tail);
        for (var i = 0; i < rows.Count; i++)
            rows[i].Ordinal = i;
        return rows;
    }

    internal static List<AgentStep> PendingTail(AgentRun run) =>
        run.Plan.Where(s => s.Status == AgentStepStatus.Pending).OrderBy(s => s.Ordinal).ToList();

    /// <summary>Newest note first, so the cap cuts the oldest; the dispatch's own nudge goes last.</summary>
    internal static string? ComposeNote(string? dispatchNudge, IReadOnlyList<string> notes)
    {
        var parts = notes.Reverse().Append(dispatchNudge).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (parts.Count == 0)
            return null;
        var joined = string.Join(" / ", parts);
        return joined.Length <= MaxNoteChars ? joined : joined[..MaxNoteChars];
    }

    internal static string Normalize(string? text, int cap)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        return flat.Length <= cap ? flat : flat[..cap] + "…";
    }

    private static int Placement(List<AgentStep> tail, JobPlanChange change)
    {
        if (change.AtStart)
            return 0;
        if (change.AfterStepId is not { } after)
            return tail.Count;
        var anchor = tail.FindIndex(s => s.Id == after);
        return anchor < 0 ? 0 : anchor + 1;
    }

    private static AgentStep Clone(AgentStep s) => new()
    {
        Id = s.Id,
        RunId = s.RunId,
        Ordinal = s.Ordinal,
        Title = s.Title,
        Intent = s.Intent,
        Status = s.Status,
        ExpectedArtifact = s.ExpectedArtifact,
        AssignedPersonaId = s.AssignedPersonaId,
        DependsOnJson = s.DependsOnJson,
        ReRunnable = s.ReRunnable,
        FirstMessageId = s.FirstMessageId,
        LastMessageId = s.LastMessageId,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
        ExtraJson = s.ExtraJson,
    };
}
