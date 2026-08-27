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
    /// Notify the user that a scheduled run completed successfully. <paramref name="chatId"/>
    /// references the produced assistant chat so the UI can open it.
    /// </summary>
    void NotifySuccess(ScheduledJob job, Guid chatId, string chatTitle);

    /// <summary>
    /// Notify the user that a scheduled run failed (no chat was produced).
    /// </summary>
    void NotifyFailure(ScheduledJob job, string reason);

    /// <summary>
    /// A scheduled meeting was attended and its transcript filed. Separate from <see cref="NotifySuccess"/>
    /// because a meeting produces a vault source rather than a chat, so there is no chat to deep-link to and
    /// an "Open chat" button would be dead. Honours quiet mode, like the other success path.
    /// </summary>
    void NotifyMeetingSaved(ScheduledJob job);

    /// <summary>
    /// Ask the user whether to run a missed scheduled job. Returns
    /// <c>true</c> for run-now, <c>false</c> for skip, and <c>null</c> if the
    /// dialog was closed/dismissed without an answer (used by Task 12 to dedup).
    /// </summary>
    Task<bool?> AskUserToRunMissedAsync(ScheduledJob job, DateTime scheduledFireAt);
}
