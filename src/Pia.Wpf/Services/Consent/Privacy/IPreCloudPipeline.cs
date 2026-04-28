using Pia.Services.Consent.Cloud;

namespace Pia.Services.Consent.Privacy;

public sealed class CloudCallContext
{
    public required CloudProviderDescriptor Provider { get; init; }
    public required ConsentScope Scope { get; init; }
    public required PseudonymisationMap Map { get; init; }
    public required string PseudonymisedPayload { get; init; }
}

public sealed class CloudCallNotPermittedException : Exception
{
    public string ProviderId { get; }
    public CloudJurisdiction Jurisdiction { get; }

    public CloudCallNotPermittedException(string providerId, CloudJurisdiction jurisdiction, string reason)
        : base(reason)
    {
        ProviderId = providerId;
        Jurisdiction = jurisdiction;
    }
}

/// <summary>
/// Mandatory pre-processing pipeline for every outbound LLM call that carries meeting
/// content. Enforces consent-scope gating, applies reversible PII pseudonymisation, and
/// reverses placeholders on the response. Spec §5.
/// </summary>
public interface IPreCloudPipeline
{
    Task<CloudCallContext> PrepareAsync(
        string transcript,
        ConsentScope scope,
        CloudProviderDescriptor provider,
        CancellationToken ct);

    Task<string> PostProcessAsync(
        string cloudResponse,
        CloudCallContext ctx,
        CancellationToken ct);
}
