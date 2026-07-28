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
    /// <see cref="SaveAsync"/> for an APPEND-ONLY writer: reads the stored message rows and merges back
    /// every row <paramref name="chat"/> does not carry (matched by <c>Id</c>, ordered by <c>Timestamp</c>)
    /// before the replace — all under ONE hold of the store's write gate.
    /// <para>
    /// W2b: the atomicity is the point. Read-then-save from the caller (<see cref="GetAsync"/> →
    /// <see cref="SaveAsync"/>) releases the gate between the read and the write, so a writer that commits
    /// in that gap still has its rows DELETEd by the replace — the window is narrowed, not closed. Here the
    /// read and the write cannot be interleaved by another caller of this service.
    /// </para>
    /// <para>
    /// ONLY for a writer whose transcript grows monotonically (the headless run executor). A writer that
    /// legitimately REMOVES messages — the live session persist behind Regenerate/Delete, which replays the
    /// user's truncation — must keep using <see cref="SaveAsync"/>, or a deleted message would be merged
    /// straight back in.
    /// </para>
    /// <returns>How many stored rows were absorbed, for the caller's log. 0 means the caller's payload
    /// already covered every row (the ordinary case).</returns>
    /// </summary>
    Task<int> SaveMergedAsync(SyncAssistantChat chat, CancellationToken ct = default);

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
