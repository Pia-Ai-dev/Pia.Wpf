using Pia.Models;

namespace Pia.ViewModels;

public enum RegenerateVerdict
{
    Allowed,
    BlockedRunning,
    BlockedUntilStopped,
}

/// <param name="Slot">Null means the block trails the transcript.</param>
/// <param name="MessageIds">Every message the job is known to have written.</param>
internal readonly record struct RegenerateJob(
    Guid RunId, RunProgressState State, bool IsPlanApprovalPause, bool HasProjected, RunBlockSlot? Slot,
    IReadOnlyCollection<Guid> MessageIds);

internal sealed record RegenerateDecision(RegenerateVerdict Verdict, IReadOnlyList<Guid> DiscardFirst);

/// <summary>Whether removing the transcript from <c>cut</c> on may go ahead, given the jobs shown in it.</summary>
internal static class RunRegenerateGate
{
    internal static RegenerateDecision Evaluate(
        IReadOnlyList<AssistantMessage> messages, int cut, IEnumerable<RegenerateJob> jobs)
    {
        var discard = new List<Guid>();
        var needsStop = false;
        foreach (var job in jobs)
        {
            var touched = IsTouched(messages, cut, job);
            if (!job.HasProjected)
            {
                if (touched)
                    return new RegenerateDecision(RegenerateVerdict.BlockedRunning, []);
                continue;
            }

            switch (job.State)
            {
                case RunProgressState.Planning or RunProgressState.Running or RunProgressState.WaitingForChildren:
                    return new RegenerateDecision(RegenerateVerdict.BlockedRunning, []);
                case RunProgressState.WaitingForInput when touched && job.IsPlanApprovalPause:
                    discard.Add(job.RunId);
                    break;
                case RunProgressState.WaitingForInput or RunProgressState.Paused when touched:
                    needsStop = true;
                    break;
            }
        }

        return needsStop
            ? new RegenerateDecision(RegenerateVerdict.BlockedUntilStopped, [])
            : new RegenerateDecision(RegenerateVerdict.Allowed, discard);
    }

    // At or below the cut: the block sits after the message before it, or the cut removes one of the job's messages.
    private static bool IsTouched(IReadOnlyList<AssistantMessage> messages, int cut, RegenerateJob job)
    {
        for (var i = cut; i < messages.Count; i++)
        {
            if (job.MessageIds.Contains(messages[i].Id))
                return true;
        }

        if (job.Slot is not { } slot)
            return true;

        var anchor = -1;
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Id == slot.MessageId) { anchor = i; break; }
        }
        if (anchor < 0)
            return false;
        return slot.Before ? anchor > cut : anchor >= cut;
    }
}
