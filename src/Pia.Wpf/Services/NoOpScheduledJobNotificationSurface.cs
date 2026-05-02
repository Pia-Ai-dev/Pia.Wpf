using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

// TODO: replaced by ScheduledJobNotificationSurface in Task 13.
/// <summary>
/// No-op fallback registered until the real toast/dialog surface (Task 13)
/// lands. Lets DI validation succeed and the background service run end-to-end
/// in tests / dev builds without a UI.
/// </summary>
internal sealed class NoOpScheduledJobNotificationSurface : IScheduledJobNotificationSurface
{
    public void NotifySuccess(ScheduledJob job, ResearchHistoryEntry entry)
    {
    }

    public void NotifyFailure(ScheduledJob job, Guid resultEntryId, string reason)
    {
    }

    public Task<bool?> AskUserToRunMissedAsync(ScheduledJob job, DateTime scheduledFireAt)
    {
        return Task.FromResult<bool?>(null);
    }
}
