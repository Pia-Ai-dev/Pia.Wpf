using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

public readonly record struct RunReply(Guid? StepId, AssistantMessage Message);

/// <summary>Which recorded job replies move into their run's block, and which one stays below it as the result.</summary>
internal static class RunReplyProjection
{
    /// <summary>A run's latest reply stays a bubble; a reply recorded without a step is never moved.</summary>
    internal static Dictionary<Guid, List<RunReply>> Project(
        IReadOnlyList<AssistantMessage> messages, IReadOnlyDictionary<Guid, RunMessageRecord> records,
        IReadOnlySet<Guid> shownRuns)
    {
        var byRun = new Dictionary<Guid, List<RunReply>>();
        var lastByRun = new Dictionary<Guid, Guid>();
        foreach (var message in messages)
        {
            if (records.TryGetValue(message.Id, out var record) && shownRuns.Contains(record.RunId))
                lastByRun[record.RunId] = message.Id;
        }

        foreach (var message in messages)
        {
            if (!records.TryGetValue(message.Id, out var record) || record.StepId is null
                || !lastByRun.TryGetValue(record.RunId, out var last) || last == message.Id)
                continue;

            if (!byRun.TryGetValue(record.RunId, out var replies))
                byRun[record.RunId] = replies = [];
            replies.Add(new RunReply(record.StepId, message));
        }

        return byRun;
    }
}
