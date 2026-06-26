using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Default <see cref="IScheduledResearchProviderResolver"/> implementation.
/// </summary>
/// <remarks>
/// Note: <see cref="ISettingsService"/> exposes the providers through the
/// dedicated <see cref="IProviderService"/> rather than directly. We use that
/// service's <c>GetProviderAsync(id)</c> for the pinned lookup and
/// <c>GetDefaultProviderForModeAsync(WindowMode.Assistant)</c> for the fallback
/// (background runs are now assistant chats), both of which already honour the
/// <see cref="AppSettings.UseSameProviderForAllModes"/> flag and the policy-enforced default.
/// </remarks>
public class ScheduledResearchProviderResolver : IScheduledResearchProviderResolver
{
    private readonly IProviderService _providers;

    public ScheduledResearchProviderResolver(IProviderService providers)
    {
        _providers = providers;
    }

    public async Task<AiProvider?> ResolveAsync(Guid? pinnedProviderId)
    {
        if (pinnedProviderId.HasValue)
        {
            var pinned = await _providers.GetProviderAsync(pinnedProviderId.Value);
            if (pinned is not null)
            {
                return pinned;
            }
        }

        return await _providers.GetDefaultProviderForModeAsync(WindowMode.Assistant);
    }
}
