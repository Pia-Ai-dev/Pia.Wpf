using Pia.Models;

namespace Pia.Services.Interfaces;

public interface IReminderService
{
    Task<Reminder> CreateAsync(string description, RecurrenceType recurrence, TimeOnly timeOfDay,
        DayOfWeek? dayOfWeek = null, int? dayOfMonth = null, int? month = null, DateTime? specificDate = null);
    Task<IReadOnlyList<Reminder>> GetAllAsync();
    Task<IReadOnlyList<Reminder>> GetActiveAsync();
    Task<Reminder?> GetAsync(Guid id);
    Task<IReadOnlyList<Reminder>> GetDueRemindersAsync();
    Task UpdateAsync(Guid id, string? description = null, RecurrenceType? recurrence = null,
        TimeOnly? timeOfDay = null, DayOfWeek? dayOfWeek = null, int? dayOfMonth = null, int? month = null);
    Task DeleteAsync(Guid id);
    Task SnoozeAsync(Guid id, TimeSpan duration);
    Task DismissAsync(Guid id);
    Task DisableAsync(Guid id);
    Task EnableAsync(Guid id);
    Task CleanupCompletedAsync(TimeSpan olderThan);
}
