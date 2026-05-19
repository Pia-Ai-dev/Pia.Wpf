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

    Task TouchLastAccessedAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> EvictOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> DeleteAllAsync(CancellationToken ct = default);

    Task<DateTime?> GetMaxUpdatedAtAsync(CancellationToken ct = default);
}
