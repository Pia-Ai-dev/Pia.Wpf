using Microsoft.Extensions.Logging;

namespace Pia.Services.Consent.Revocation;

/// <summary>
/// Implements spec §4.7 revocation workflow:
/// 1. Mark Revoked. 2. Add to blocklist. 3. Redact persisted transcript segments.
/// 4. Delete cached summary. 5. Issue provider-specific deletion calls (or audit
/// OUTSTANDING_PROVIDER_DELETION). 6. Append REVOCATION audit. 7. Keep original
/// ConsentEvidence + add RevocationEvidence.
/// </summary>
public sealed class RevocationService : IRevocationService
{
    private readonly IConsentStateManager _consentMgr;
    private readonly IBlocklistFilter _blocklistFilter;
    private readonly IPersistedTranscriptStore _transcriptStore;
    private readonly ICachedSummaryStore _summaryStore;
    private readonly IReadOnlyList<IProviderDeletionClient> _providerClients;
    private readonly IConsentAuditLog _auditLog;
    private readonly TimeProvider _clock;
    private readonly ILogger<RevocationService> _logger;

    public RevocationService(
        IConsentStateManager consentMgr,
        IBlocklistFilter blocklistFilter,
        IPersistedTranscriptStore transcriptStore,
        ICachedSummaryStore summaryStore,
        IEnumerable<IProviderDeletionClient> providerClients,
        IConsentAuditLog auditLog,
        TimeProvider clock,
        ILogger<RevocationService> logger)
    {
        _consentMgr = consentMgr;
        _blocklistFilter = blocklistFilter;
        _transcriptStore = transcriptStore;
        _summaryStore = summaryStore;
        _providerClients = providerClients.ToList();
        _auditLog = auditLog;
        _clock = clock;
        _logger = logger;
    }

    public async Task<RevocationEvidence> RevokeAsync(string speakerLabel, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(speakerLabel))
            throw new ArgumentException("Speaker label required.", nameof(speakerLabel));

        _logger.LogInformation("Revoking consent for {Label}", speakerLabel);

        // 1. Mark Revoked.
        _consentMgr.Revoke(speakerLabel);

        // 2. Add embedding to blocklist.
        _blocklistFilter.BlockSpeaker(speakerLabel);

        // 3. Redact persisted transcripts.
        bool transcriptRedacted;
        try
        {
            transcriptRedacted = await _transcriptStore.RedactSpeakerAsync(speakerLabel, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transcript redaction failed for {Label}", speakerLabel);
            transcriptRedacted = false;
        }

        // 4. Delete cached summary.
        bool summaryDeleted;
        try
        {
            summaryDeleted = await _summaryStore.DeleteCurrentSummaryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Summary deletion failed");
            summaryDeleted = false;
        }

        // 5. Provider deletion calls.
        var requested = new List<string>();
        var outstanding = new List<string>();
        foreach (var client in _providerClients)
        {
            if (!client.SupportsDeletion)
            {
                outstanding.Add(client.ProviderId);
                _auditLog.Append(new AuditEvent(
                    Guid.NewGuid(), _clock.GetUtcNow(), "OUTSTANDING_PROVIDER_DELETION",
                    speakerLabel,
                    new Dictionary<string, object?> { ["provider"] = client.ProviderId }));
                continue;
            }
            try
            {
                await client.RequestDeletionAsync(speakerLabel, ct).ConfigureAwait(false);
                requested.Add(client.ProviderId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Provider {Provider} deletion failed", client.ProviderId);
                outstanding.Add(client.ProviderId);
                _auditLog.Append(new AuditEvent(
                    Guid.NewGuid(), _clock.GetUtcNow(), "OUTSTANDING_PROVIDER_DELETION",
                    speakerLabel,
                    new Dictionary<string, object?>
                    {
                        ["provider"] = client.ProviderId,
                        ["reason"] = "request_failed",
                    }));
            }
        }

        // 6. REVOCATION audit event.
        var evidence = new RevocationEvidence(
            speakerLabel, _clock.GetUtcNow(),
            transcriptRedacted, summaryDeleted,
            requested, outstanding);

        _auditLog.Append(new AuditEvent(
            Guid.NewGuid(), _clock.GetUtcNow(), "REVOCATION",
            speakerLabel,
            new Dictionary<string, object?>
            {
                ["transcriptRedacted"] = transcriptRedacted,
                ["summaryDeleted"] = summaryDeleted,
                ["providersRequested"] = requested.Count,
                ["providersOutstanding"] = outstanding.Count,
            }));

        return evidence;
    }
}
