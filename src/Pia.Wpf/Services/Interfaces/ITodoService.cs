using Pia.Models;

namespace Pia.Services.Interfaces;

public interface ITodoService
{
    event EventHandler? TodoChanged;
    Task<TodoItem> CreateAsync(string title, TodoPriority priority = TodoPriority.Medium,
                               string? notes = null, DateTime? dueDate = null, Guid? columnId = null);
    Task<TodoItem?> GetAsync(Guid id);
    Task<IReadOnlyList<TodoItem>> GetAllAsync();
    Task<IReadOnlyList<TodoItem>> GetPendingAsync();
    Task<IReadOnlyList<TodoItem>> GetCompletedAsync();
    Task<IReadOnlyList<TodoItem>> GetCompletedTodayAsync();
    Task<int> GetPendingCountAsync();
    Task UpdateAsync(TodoItem item);
    Task UpdateSortOrderAsync(IReadOnlyList<(Guid Id, int SortOrder)> updates);
    Task ImportAsync(TodoItem item);
    Task CompleteAsync(Guid id);
    Task UncompleteAsync(Guid id);
    Task DeleteAsync(Guid id);
    Task LinkReminderAsync(Guid todoId, Guid reminderId);
    Task UnlinkReminderAsync(Guid todoId);
    Task CleanupOldCompletedAsync(int olderThanDays = 30);
    Task<IReadOnlyList<TodoItem>> GetByColumnAsync(Guid columnId);
    Task MoveToColumnAsync(Guid todoId, Guid targetColumnId);
}
