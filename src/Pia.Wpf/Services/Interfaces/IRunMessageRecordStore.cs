namespace Pia.Services.Interfaces;

/// <summary>A chat message a job wrote, and the step it was working on then. Device-local, never synced.</summary>
public sealed record RunMessageRecord(Guid MessageId, Guid RunId, Guid? StepId, int? StepOrdinal, DateTime CreatedAt);

public interface IRunMessageRecordStore
{
    /// <summary>Raised after a record is stored, on the writer's thread.</summary>
    event EventHandler<RunMessageRecord>? Recorded;

    /// <summary>Never throws: a lost record only means the reply shows as an ordinary bubble.</summary>
    Task RecordAsync(RunMessageRecord record, CancellationToken ct = default);

    Task<IReadOnlyList<RunMessageRecord>> GetByChatAsync(Guid chatId, CancellationToken ct = default);
}
