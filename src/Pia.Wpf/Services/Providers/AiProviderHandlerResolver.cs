using Pia.Models;

namespace Pia.Services.Providers;

public sealed class AiProviderHandlerResolver
{
    private readonly Dictionary<AiProviderType, IAiProviderHandler> _handlers;

    public AiProviderHandlerResolver(IEnumerable<IAiProviderHandler> handlers)
    {
        _handlers = handlers.ToDictionary(h => h.ProviderType);
    }

    public IAiProviderHandler Get(AiProviderType providerType)
    {
        if (_handlers.TryGetValue(providerType, out var handler))
            return handler;

        throw new NotSupportedException($"Provider type {providerType} is not supported");
    }
}
