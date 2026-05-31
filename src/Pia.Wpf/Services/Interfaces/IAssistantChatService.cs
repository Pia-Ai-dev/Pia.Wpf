using Pia.Shared.Models;

namespace Pia.Services.Interfaces;

public enum AssistantChatChangeKind
{
    Upserted,
    Deleted,
}

public sealed class AssistantChatChangedEventArgs : EventArgs
{
    public required Guid Id { get; init; }
    public required AssistantChatChangeKind Kind { get; init; }
}

public interface IAssistantChatService
{
    event EventHandler<AssistantChatChangedEventArgs>? ChatsChanged;

    Task SaveAsync(SyncAssistantChat chat, CancellationToken ct = default);

    /// <summary>
    /// Apply a chat received from a remote pull WITHOUT raising <see cref="ChatsChanged"/>.
    /// Prevents the cloud-sync worker from re-enqueuing the merge as a local edit.
    /// </summary>
    Task SaveFromRemoteAsync(SyncAssistantChat chat, CancellationToken ct = default);

    Task<SyncAssistantChat?> GetAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<SyncAssistantChat>> SearchAsync(
        string? searchText = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        Guid? providerId = null,
        int offset = 0,
        int limit = 50,
        CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Apply a deletion received from a remote pull WITHOUT raising <see cref="ChatsChanged"/>.
    /// Prevents the cloud-sync worker from re-enqueuing the delete as a local edit.
    /// </summary>
    Task DeleteFromRemoteAsync(Guid id, CancellationToken ct = default);

    Task TouchLastAccessedAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> EvictOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> DeleteAllAsync(CancellationToken ct = default);

    Task<DateTime?> GetMaxUpdatedAtAsync(CancellationToken ct = default);

    /// <summary>
    /// All locally stored chat IDs. Used by the cloud-sync worker's one-time
    /// startup backfill to push chats that predate cloud sign-in.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetAllIdsAsync(CancellationToken ct = default);
}
