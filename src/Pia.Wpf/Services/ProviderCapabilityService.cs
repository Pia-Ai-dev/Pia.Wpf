using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Probe-once + cache-per-provider tool-calling capability (R10). Mirrors the double-checked async gate
/// of <see cref="CloudCapabilityService"/>, keyed per provider id. Never blocks: a probe failure caches
/// nothing and returns <see cref="PlanningCapability.Unknown"/> so a retry can re-probe.
/// </summary>
public sealed class ProviderCapabilityService : IProviderCapabilityService
{
    private readonly IAiClientService _aiClient;
    private readonly ILogger<ProviderCapabilityService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, PlanningCapability> _cache = new();

    public ProviderCapabilityService(IAiClientService aiClient, ILogger<ProviderCapabilityService> logger)
    {
        _aiClient = aiClient;
        _logger = logger;
    }

    public async Task<PlanningCapability> GetPlanningCapabilityAsync(AiProvider provider, CancellationToken ct = default)
    {
        // R10: a provider with tool calling disabled is Weak without probing (never Capable).
        if (!provider.SupportsToolCalling)
            return PlanningCapability.Weak;

        lock (_cache)
        {
            if (_cache.TryGetValue(provider.Id, out var hit))
                return hit;
        }

        await _gate.WaitAsync(ct);
        try
        {
            lock (_cache)
            {
                if (_cache.TryGetValue(provider.Id, out var hit))
                    return hit;
            }

            PlanningCapability result;
            try
            {
                var emitted = await _aiClient.TestToolCallEmittedAsync(provider, ct);
                result = emitted ? PlanningCapability.Capable : PlanningCapability.Weak;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // No URL/content — only the exception type name (mirrors CloudCapabilityService.ProbeAsync).
                _logger.LogInformation("Provider capability probe failed: {Error}", ex.GetType().Name);
                result = PlanningCapability.Unknown; // never cached; never blocks
            }

            if (result != PlanningCapability.Unknown)
            {
                lock (_cache)
                {
                    _cache[provider.Id] = result;
                }
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate(Guid providerId)
    {
        lock (_cache)
        {
            _cache.Remove(providerId);
        }
    }
}
