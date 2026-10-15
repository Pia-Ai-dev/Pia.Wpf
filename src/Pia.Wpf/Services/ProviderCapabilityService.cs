using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>Probe-once, cache-per-provider tool-calling capability; mirrors <see cref="CloudCapabilityService"/>.</summary>
public sealed class ProviderCapabilityService : IProviderCapabilityService
{
    // Every non-planned turn asks; a failing probe must not be paid for on each one.
    internal static readonly TimeSpan UnknownExpiry = TimeSpan.FromMinutes(15);

    private readonly IAiClientService _aiClient;
    private readonly ILogger<ProviderCapabilityService> _logger;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, (PlanningCapability Capability, DateTimeOffset? ExpiresAt)> _cache = new();

    public ProviderCapabilityService(
        IAiClientService aiClient, ILogger<ProviderCapabilityService> logger, TimeProvider? clock = null)
    {
        _aiClient = aiClient;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<PlanningCapability> GetPlanningCapabilityAsync(AiProvider provider, CancellationToken ct = default)
    {
        // R10: a provider with tool calling disabled is Weak without probing (never Capable).
        if (!provider.SupportsToolCalling)
            return PlanningCapability.Weak;

        if (TryGetCached(provider.Id, out var hit))
            return hit;

        await _gate.WaitAsync(ct);
        try
        {
            if (TryGetCached(provider.Id, out hit))
                return hit;

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
                result = PlanningCapability.Unknown;
            }

            lock (_cache)
            {
                _cache[provider.Id] = (result,
                    result == PlanningCapability.Unknown ? _clock.GetUtcNow() + UnknownExpiry : null);
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

    private bool TryGetCached(Guid providerId, out PlanningCapability capability)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(providerId, out var entry)
                && (entry.ExpiresAt is not { } expiresAt || _clock.GetUtcNow() < expiresAt))
            {
                capability = entry.Capability;
                return true;
            }
        }

        capability = default;
        return false;
    }
}
