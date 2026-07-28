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

    /// <summary>
    /// Set a chat's title (and <c>UpdatedAt</c>) WITHOUT touching its message rows, refreshing the FTS row so
    /// history search does not keep matching the old title. No-op when the chat row is gone.
    /// <para>
    /// W2: this exists so the auto-title rename stops being a full-chat writer. The old shape —
    /// <see cref="GetAsync"/> → mutate <c>Title</c> → <see cref="SaveAsync"/> — is a fire-and-forget
    /// read-modify-write whose DB snapshot is routinely stale by the time it writes, so it could revert
    /// message rows a headless step appended in between. A title update has no business carrying a message
    /// payload.
    /// </para>
    /// <returns>
    /// <c>true</c> when a row was updated; <c>false</c> when the chat had already been deleted/evicted. The
    /// caller (the auto-title path) turns <c>false</c> into its existing "chat disappeared before rename"
    /// warning — this service owns no logger, so the signal has to come back as a value.
    /// </returns>
    /// </summary>
    Task<bool> SetTitleAsync(Guid chatId, string title, CancellationToken ct = default);

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
