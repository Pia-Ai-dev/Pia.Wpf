using Microsoft.Extensions.Logging;

namespace Pia.ViewModels;

public partial class AssistantViewModel
{
    // A discarded plan posts a note into the chat, which the caller has to remove with the rest.
    private async Task<(bool Proceed, IReadOnlyList<Guid> DiscardedRuns)> ClearJobsForRegenerateAsync(int cut)
    {
        var (placed, trailing) = PlaceRunBlocks();
        var shown = placed.Select(p => ((RunProgressViewModel)p.Block, p.Slot))
            .Concat(trailing.OfType<RunProgressViewModel>().Select(b => (b, (RunBlockSlot?)null)))
            .ToList();
        var decision = RunRegenerateGate.Evaluate(Messages, cut, shown.Select(s => new RegenerateJob(
            s.Item1.RunId, s.Item1.State, s.Item1.IsPlanApprovalPause, s.Item1.HasProjected, s.Item2,
            JobMessageIds(s.Item1))));

        // The session knows of a waiting plan this view has no block for; nothing here can discard it.
        if (_subscribedSession?.PlanApprovalParkActive == true && !shown.Any(s => s.Item1.HasProjected && s.Item1.IsPlanApprovalPause))
            return (false, []);

        switch (decision.Verdict)
        {
            case RegenerateVerdict.BlockedRunning:
                return (false, []);
            case RegenerateVerdict.BlockedUntilStopped:
                _snackbarService.Show(
                    _localizationService["RunRegenerate_BlockedTitle"],
                    _localizationService["RunRegenerate_BlockedPaused"],
                    Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(5));
                return (false, []);
        }

        foreach (var runId in decision.DiscardFirst)
        {
            try
            {
                if (!await _resumeService.RejectPlanAsync(runId))
                    return (false, []);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Run {RunId}: discarding the waiting plan before a regenerate failed", runId);
                return (false, []);
            }
            _regenerateEmptiedRuns.Add(runId);
            _logger.LogInformation("Run {RunId}: waiting plan discarded for a regenerate", runId);
        }

        return (true, [.. decision.DiscardFirst]);
    }

    private HashSet<Guid> JobMessageIds(RunProgressViewModel block)
    {
        var ids = _replyRecords.Values.Where(r => r.RunId == block.RunId).Select(r => r.MessageId).ToHashSet();
        if (block.FirstMessageId is { } first) ids.Add(first);
        if (block.LastMessageId is { } last) ids.Add(last);
        return ids;
    }

    // A discard's note the transcript has not pulled yet would land below the new answer; other rows are not ours.
    private async Task<IReadOnlyList<Guid>> UnpulledDiscardNotesAsync(Guid chatId, IReadOnlyList<Guid> discardedRuns)
    {
        if (discardedRuns.Count == 0 || _runMessageRecords is null)
            return [];
        try
        {
            var runMessages = (await _runMessageRecords.GetByChatAsync(chatId))
                .Where(r => discardedRuns.Contains(r.RunId))
                .Select(r => r.MessageId)
                .ToHashSet();
            if (runMessages.Count == 0)
                return [];
            var stored = await _chatService.GetAsync(chatId);
            if (stored is null)
                return [];
            var known = Messages.Select(m => m.Id).ToHashSet();
            return [.. stored.Messages.Select(m => m.Id).Where(id => !known.Contains(id) && runMessages.Contains(id))];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to re-read chat {ChatId} before a regenerate", chatId);
            return [];
        }
    }
}
