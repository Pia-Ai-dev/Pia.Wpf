using Pia.Services.Interfaces;

namespace Pia.ViewModels;

public partial class AssistantViewModel
{
    private IRunMessageRecordStore? _runMessageRecords;
    private readonly Dictionary<Guid, RunMessageRecord> _replyRecords = [];
    private Guid? _replyRecordsChatId;

    private void InitRunReplies(IRunMessageRecordStore? store)
    {
        _runMessageRecords = store;
        if (store is not null)
            store.Recorded += OnRunMessageRecorded;
    }

    private void DisposeRunReplies()
    {
        if (_runMessageRecords is not null)
            _runMessageRecords.Recorded -= OnRunMessageRecorded;
    }

    private void OnRunMessageRecorded(object? sender, RunMessageRecord record) =>
        _uiDispatcher.Post(() =>
        {
            _replyRecords[record.MessageId] = record;
            SyncTranscriptItems();
        });

    internal async Task LoadReplyRecordsAsync(Guid? chatId)
    {
        _replyRecords.Clear();
        _replyRecordsChatId = chatId;
        if (_runMessageRecords is null || chatId is not { } id)
            return;

        var records = await _runMessageRecords.GetByChatAsync(id);
        if (_replyRecordsChatId != id)
            return;
        foreach (var record in records)
            _replyRecords[record.MessageId] = record;
        SyncTranscriptItems();
    }

    /// <summary>Hands each shown block its step replies and returns the messages that leave the bubble list.</summary>
    private HashSet<Guid> ApplyRunReplies(IEnumerable<RunProgressViewModel> shownBlocks)
    {
        var hidden = new HashSet<Guid>();
        // A block that has not loaded its steps yet could not show the replies, so they stay bubbles until it has.
        var shown = shownBlocks.Where(b => b.HasProjected).ToDictionary(b => b.RunId);
        if (shown.Count == 0)
            return hidden;

        var projected = RunReplyProjection.Project(Messages, _replyRecords, shown.Keys.ToHashSet());
        foreach (var (runId, block) in shown)
        {
            IReadOnlyList<RunReply> replies = projected.TryGetValue(runId, out var found) ? found : [];
            block.SetReplies(replies);
            foreach (var reply in replies)
                hidden.Add(reply.Message.Id);
        }
        return hidden;
    }
}
