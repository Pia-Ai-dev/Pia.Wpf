using Pia.Models;

namespace Pia.Services.Interfaces;

/// <summary>
/// UI hub for scheduled-job notifications.
/// </summary>
/// <remarks>
/// Concrete implementation arrives in Task 13. The background service is
/// agnostic to whether the surface uses toasts, in-app dialogs, or both.
/// </remarks>
public interface IScheduledJobNotificationSurface
{
    /// <summary>
    /// Notify the user that a scheduled research run completed successfully.
    /// </summary>
    void NotifySuccess(ScheduledJob job, ResearchHistoryEntry entry);

    /// <summary>
    /// Notify the user that a scheduled research run failed. <paramref name="resultEntryId"/>
    /// references the persisted "Failed" entry so the UI can navigate to it.
    /// </summary>
    void NotifyFailure(ScheduledJob job, Guid resultEntryId, string reason);

    /// <summary>
    /// Ask the user whether to run a missed scheduled job. Returns
    /// <c>true</c> for run-now, <c>false</c> for skip, and <c>null</c> if the
    /// dialog was closed/dismissed without an answer (used by Task 12 to dedup).
    /// </summary>
    Task<bool?> AskUserToRunMissedAsync(ScheduledJob job, DateTime scheduledFireAt);
}
