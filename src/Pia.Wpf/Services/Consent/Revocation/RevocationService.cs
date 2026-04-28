using Microsoft.Extensions.Logging;
using Pia.Services.Consent.Biometric;

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
    private readonly IBiometricConsentStore? _biometricStore;
    private readonly IBiometricMatcher? _biometricMatcher;

    public RevocationService(
        IConsentStateManager consentMgr,
        IBlocklistFilter blocklistFilter,
        IPersistedTranscriptStore transcriptStore,
        ICachedSummaryStore summaryStore,
        IEnumerable<IProviderDeletionClient> providerClients,
        IConsentAuditLog auditLog,
        TimeProvider clock,
        ILogger<RevocationService> logger,
        IBiometricConsentStore? biometricStore = null,
        IBiometricMatcher? biometricMatcher = null)
    {
        _consentMgr = consentMgr;
        _blocklistFilter = blocklistFilter;
        _transcriptStore = transcriptStore;
        _summaryStore = summaryStore;
        _providerClients = providerClients.ToList();
        _auditLog = auditLog;
        _clock = clock;
        _logger = logger;
        _biometricStore = biometricStore;
        _biometricMatcher = biometricMatcher;
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

        // Phase 5: revocation extends across sessions. Remove any persisted biometric
        // entry whose embedding matches the revoked speaker's session embedding.
        await RemoveBiometricEntriesAsync(speakerLabel, ct).ConfigureAwait(false);

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

    private async Task RemoveBiometricEntriesAsync(string speakerLabel, CancellationToken ct)
    {
        if (_biometricStore is null || _biometricMatcher is null) return;
        if (!_consentMgr.TryGet(speakerLabel, out var entry) || entry.Embedding is not { } emb)
        {
            _logger.LogDebug("No session embedding for {Label}; skipping biometric removal", speakerLabel);
            return;
        }
        // Use a permissive threshold so we err on the side of deletion — DSGVO Art. 17
        // makes a stronger case than the false-positive risk of removing a different
        // speaker's profile (the user always has explicit revoke for that as well).
        const float removalThreshold = 0.80f;
        var match = await _biometricMatcher
            .MatchAsync(emb, removalThreshold, ct)
            .ConfigureAwait(false);
        if (match is null) return;

        // Remove the matched entry, plus any other entries close enough to also
        // belong to the same speaker. Loop until no more matches above threshold.
        var removed = new HashSet<Guid>();
        while (match is not null && removed.Add(match.Entry.Id))
        {
            await _biometricStore.RemoveAsync(match.Entry.Id, ct).ConfigureAwait(false);
            _auditLog.Append(new AuditEvent(
                Guid.NewGuid(), _clock.GetUtcNow(), "BIOMETRIC_ENTRY_REVOKED", speakerLabel,
                new Dictionary<string, object?>
                {
                    ["entryId"] = match.Entry.Id,
                    ["similarity"] = match.Similarity,
                }));
            match = await _biometricMatcher
                .MatchAsync(emb, removalThreshold, ct)
                .ConfigureAwait(false);
        }
    }
}
