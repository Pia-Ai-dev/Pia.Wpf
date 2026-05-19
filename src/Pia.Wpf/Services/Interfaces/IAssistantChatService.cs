using Pia.Shared.Models;

namespace Pia.Services.Interfaces;

public interface IAssistantChatService
{
    event EventHandler? ChatsChanged;

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

    Task<int> EvictOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default);

    Task<int> DeleteAllAsync(CancellationToken ct = default);
}
