using Microsoft.Extensions.Logging;

namespace Pia.Services.Consent.Privacy;

public sealed class PreCloudPipeline : IPreCloudPipeline
{
    private readonly Pseudonymiser _pseudonymiser;
    private readonly IConsentAuditLog _auditLog;
    private readonly ILogger<PreCloudPipeline> _logger;

    public PreCloudPipeline(
        Pseudonymiser pseudonymiser,
        IConsentAuditLog auditLog,
        ILogger<PreCloudPipeline> logger)
    {
        _pseudonymiser = pseudonymiser;
        _auditLog = auditLog;
        _logger = logger;
    }

    public Task<CloudCallContext> PrepareAsync(
        string transcript,
        ConsentScope scope,
        Cloud.CloudProviderDescriptor provider,
        CancellationToken ct)
    {
        if (!scope.AllowsCloud(provider.Jurisdiction))
        {
            _auditLog.Append(new AuditEvent(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                "CLOUD_CALL_BLOCKED",
                SpeakerLabel: null,
                Details: new Dictionary<string, object?>
                {
                    ["provider"] = provider.Id,
                    ["jurisdiction"] = provider.Jurisdiction.ToString(),
                    ["reason"] = "scope_disallows",
                }));
            throw new CloudCallNotPermittedException(
                provider.Id,
                provider.Jurisdiction,
                $"Consent scope does not permit {provider.Jurisdiction} provider '{provider.Id}'.");
        }

        var map = new PseudonymisationMap();
        var pseudonymised = _pseudonymiser.Apply(transcript ?? string.Empty, map);

        _auditLog.Append(new AuditEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "CLOUD_CALL_PREPARED",
            SpeakerLabel: null,
            Details: new Dictionary<string, object?>
            {
                ["provider"] = provider.Id,
                ["jurisdiction"] = provider.Jurisdiction.ToString(),
                ["pseudonymCount"] = map.Count,
            }));

        _logger.LogInformation(
            "PreCloudPipeline prepared call to {Provider} ({Jurisdiction}) — {Count} placeholders",
            provider.Id, provider.Jurisdiction, map.Count);

        return Task.FromResult(new CloudCallContext
        {
            Provider = provider,
            Scope = scope,
            Map = map,
            PseudonymisedPayload = pseudonymised,
        });
    }

    public Task<string> PostProcessAsync(string cloudResponse, CloudCallContext ctx, CancellationToken ct)
    {
        var reversed = _pseudonymiser.Reverse(cloudResponse ?? string.Empty, ctx.Map);
        return Task.FromResult(reversed);
    }
}
