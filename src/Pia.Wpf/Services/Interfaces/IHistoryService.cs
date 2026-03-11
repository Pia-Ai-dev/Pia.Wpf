using Pia.Models;

namespace Pia.Services.Interfaces;

public interface IHistoryService
{
    event EventHandler? SessionsChanged;
    Task AddSessionAsync(OptimizationSession session);
    Task<IReadOnlyList<OptimizationSession>> GetSessionsAsync(int offset = 0, int limit = 50);
    Task<IReadOnlyList<OptimizationSession>> SearchSessionsAsync(
        string? searchText = null,
        Guid? templateId = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        int offset = 0,
        int limit = 50);
    Task<OptimizationSession?> GetSessionAsync(Guid id);
    Task DeleteSessionAsync(Guid id);
    Task<int> GetSessionCountAsync();
    Task<int> GetSessionCountAsync(
        string? searchText = null,
        Guid? templateId = null,
        DateTime? fromDate = null,
        DateTime? toDate = null);
}
