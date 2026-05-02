using Pia.Models;

namespace Pia.Services.Interfaces;

/// <summary>
/// Resolves the <see cref="AiProvider"/> to use for a scheduled research run.
/// Pinned provider (job.ProviderId) wins; otherwise falls back to the
/// research-mode default configured by the user.
/// </summary>
public interface IScheduledResearchProviderResolver
{
    Task<AiProvider?> ResolveAsync(Guid? pinnedProviderId);
}
