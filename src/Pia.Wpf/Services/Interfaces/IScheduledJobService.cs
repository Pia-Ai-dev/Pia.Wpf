using Pia.Models;

namespace Pia.Services.Interfaces;

public interface IScheduledJobService
{
    Task<ScheduledJob> CreateAsync(string name, string query, RecurrenceType recurrence, TimeOnly timeOfDay,
        DayOfWeek? dayOfWeek = null, int? dayOfMonth = null, int? month = null, DateTime? specificDate = null,
        Guid? providerId = null, IReadOnlyCollection<string>? grantedTools = null,
        ScheduledJobKind kind = ScheduledJobKind.Research);

    Task<IReadOnlyList<ScheduledJob>> GetAllAsync();
    Task<IReadOnlyList<ScheduledJob>> GetActiveAsync();
    Task<ScheduledJob?> GetAsync(Guid id);
    Task<IReadOnlyList<ScheduledJob>> GetDueJobsAsync();
    Task<IReadOnlyList<ScheduledJob>> GetModifiedSinceAsync(DateTime since);

    Task UpdateAsync(Guid id, string? name = null, string? query = null,
        RecurrenceType? recurrence = null, TimeOnly? timeOfDay = null,
        DayOfWeek? dayOfWeek = null, int? dayOfMonth = null, int? month = null,
        Guid? providerId = null, IReadOnlyCollection<string>? grantedTools = null);

    Task DeleteAsync(Guid id);

    Task DisableAsync(Guid id);
    Task EnableAsync(Guid id);

    Task MarkRunCompleteAsync(Guid id, Guid resultEntryId);
    Task MarkRunFailedAsync(Guid id, string reason);

    /// <summary>
    /// Advances <c>NextFireAt</c> for a job whose missed-run prompt was answered "Skip"
    /// without touching the failure counter. Skipping a missed run is a user choice,
    /// not a job-health signal.
    /// </summary>
    Task AdvanceMissedRunAsync(Guid id);

    /// <summary>
    /// Inserts a new job (no execution state) or updates the synced config of an existing one.
    /// Leaves NextFireAt/LastFiredAt/LastResultEntryId/ConsecutiveFailures untouched on update,
    /// since those are device-local execution state.
    /// </summary>
    Task UpsertFromSyncAsync(ScheduledJob job);
}
