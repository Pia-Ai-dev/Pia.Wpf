using Pia.Models;

namespace Pia.Services.Interfaces;

/// <summary>Tool-calling capability of a provider for Agent planning (R10).</summary>
public enum PlanningCapability
{
    /// <summary>The provider accepts a tools schema AND emits an actual tool call in the probe.</summary>
    Capable,

    /// <summary>The provider does not support tool calling, or the strengthened probe did not emit a call.</summary>
    Weak,

    /// <summary>The probe failed transiently (network/timeout). Not cached — a later call re-probes.</summary>
    Unknown,
}

/// <summary>
/// Probe-once + cache-per-provider tool-calling capability for the Agent lever/suggestion (R10).
/// Never hard-blocks: local providers stay usable; capability only gates whether the suggestion chip
/// appears and whether the Weak-provider warning shows. Mirrors <see cref="ICloudCapabilityService"/>.
/// </summary>
public interface IProviderCapabilityService
{
    Task<PlanningCapability> GetPlanningCapabilityAsync(AiProvider provider, CancellationToken ct = default);

    /// <summary>Drops the cached result for one provider (e.g. after its settings change).</summary>
    void Invalidate(Guid providerId);
}
