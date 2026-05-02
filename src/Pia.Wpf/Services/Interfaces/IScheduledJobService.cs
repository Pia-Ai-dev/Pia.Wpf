using Pia.Models;

namespace Pia.Services.Interfaces;

public interface IScheduledJobService
{
    Task<ScheduledJob> CreateAsync(string name, string query, RecurrenceType recurrence, TimeOnly timeOfDay,
        DayOfWeek? dayOfWeek = null, int? dayOfMonth = null, int? month = null, DateTime? specificDate = null,
        ResearchAnswerLength answerLength = ResearchAnswerLength.Balanced, Guid? providerId = null);

    Task<IReadOnlyList<ScheduledJob>> GetAllAsync();
    Task<IReadOnlyList<ScheduledJob>> GetActiveAsync();
    Task<ScheduledJob?> GetAsync(Guid id);
    Task<IReadOnlyList<ScheduledJob>> GetDueJobsAsync();

    Task UpdateAsync(Guid id, string? name = null, string? query = null,
        RecurrenceType? recurrence = null, TimeOnly? timeOfDay = null,
        DayOfWeek? dayOfWeek = null, int? dayOfMonth = null, int? month = null,
        ResearchAnswerLength? answerLength = null, Guid? providerId = null);

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
}
