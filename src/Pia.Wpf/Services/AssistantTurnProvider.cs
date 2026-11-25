using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>The provider an Assistant turn runs on; the drop-time attachment budget must read the same one.</summary>
public static class AssistantTurnProvider
{
    public static async Task<AiProvider?> ResolveAsync(Persona? persona, IProviderService providers)
    {
        var provider = persona?.PreferredProviderId is { } id ? await providers.GetProviderAsync(id) : null;
        return provider ?? await providers.GetDefaultProviderForModeAsync(WindowMode.Assistant);
    }
}
