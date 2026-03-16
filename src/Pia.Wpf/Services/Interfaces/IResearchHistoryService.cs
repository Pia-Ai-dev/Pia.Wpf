using Pia.Models;

namespace Pia.Services.Interfaces;

public interface IResearchHistoryService
{
    event EventHandler? SessionsChanged;
    Task AddEntryAsync(ResearchHistoryEntry entry);
    Task<IReadOnlyList<ResearchHistoryEntry>> SearchEntriesAsync(
        string? searchText = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        int offset = 0,
        int limit = 50);
    Task<ResearchHistoryEntry?> GetEntryAsync(Guid id);
    Task DeleteEntryAsync(Guid id);
    Task<int> GetEntryCountAsync(
        string? searchText = null,
        DateTime? fromDate = null,
        DateTime? toDate = null);
}
