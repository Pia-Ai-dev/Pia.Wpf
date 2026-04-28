using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services.Consent.Biometric;

public sealed class BiometricConsentService : IBiometricConsentService
{
    private readonly IBiometricConsentStore _store;
    private readonly IBiometricMatcher _matcher;
    private readonly IConsentClassifier _classifier;
    private readonly IConsentStateManager _consentMgr;
    private readonly ISecurityModeProvider _securityMode;
    private readonly IConsentAuditLog _auditLog;
    private readonly ITtsService _tts;
    private readonly TimeProvider _clock;
    private readonly ILogger<BiometricConsentService> _logger;
    private readonly Func<string, CancellationToken, Task<string>>? _captureReplyAsync;

    public BiometricConsentService(
        IBiometricConsentStore store,
        IBiometricMatcher matcher,
        IConsentClassifier classifier,
        IConsentStateManager consentMgr,
        ISecurityModeProvider securityMode,
        IConsentAuditLog auditLog,
        ITtsService tts,
        TimeProvider clock,
        ILogger<BiometricConsentService> logger,
        Func<string, CancellationToken, Task<string>>? captureReplyAsync = null)
    {
        _store = store;
        _matcher = matcher;
        _classifier = classifier;
        _consentMgr = consentMgr;
        _securityMode = securityMode;
        _auditLog = auditLog;
        _tts = tts;
        _clock = clock;
        _logger = logger;
        _captureReplyAsync = captureReplyAsync;
    }

    public async Task<BiometricMatchOutcome> TryMatchExistingAsync(
        string speakerLabel, float[] embedding, CancellationToken ct = default)
    {
        var match = await _matcher.MatchAsync(embedding, ct: ct).ConfigureAwait(false);
        if (match is null) return BiometricMatchOutcome.NoMatch;

        // Freshness check (spec §4.6): expired entries get auto-deleted, fall through.
        var now = _clock.GetUtcNow();
        if (match.Entry.ExpiresAt <= now)
        {
            await _store.RemoveAsync(match.Entry.Id, ct).ConfigureAwait(false);
            _auditLog.Append(new AuditEvent(
                Guid.NewGuid(), now, "BIOMETRIC_ENTRY_EXPIRED", speakerLabel,
                new Dictionary<string, object?>
                {
                    ["entryId"] = match.Entry.Id,
                    ["reason"] = "match_attempt",
                }));
            return BiometricMatchOutcome.MatchedButExpired;
        }

        // Reuse the stored consent: drive the speaker straight to Granted, attach evidence
        // referencing the stored entry so the audit trail captures the reuse.
        var evidence = new ConsentEvidence(
            TranscriptText: $"[reused biometric grant {match.Entry.Id}]",
            ClassificationConfidence: match.Similarity,
            Timestamp: now,
            PromptVersionHash: match.Entry.PromptVersionHash,
            PromptTextPlayed: string.Empty,
            SttModelId: "biometric-reuse");
        var classification = new ConsentClassification(ConsentDecision.Grant, match.Similarity);
        _consentMgr.RecordClassification(
            speakerLabel, classification,
            evidence.TranscriptText, evidence.PromptVersionHash, evidence.PromptTextPlayed, evidence.SttModelId);
        if (_consentMgr.TryGet(speakerLabel, out var entry))
        {
            entry.BiometricMatchSource = match.Entry.Id;
        }

        _auditLog.Append(new AuditEvent(
            Guid.NewGuid(), now, "BIOMETRIC_MATCH_REUSED_CONSENT", speakerLabel,
            new Dictionary<string, object?>
            {
                ["entryId"] = match.Entry.Id,
                ["similarity"] = match.Similarity,
                ["expiresAt"] = match.Entry.ExpiresAt,
            }));

        return BiometricMatchOutcome.MatchedAndReused;
    }

    public async Task<BiometricOptInOutcome> OfferOptInAsync(
        string speakerLabel, float[] embedding, string consentEvidencePath, CancellationToken ct = default)
    {
        var profile = _securityMode.Current;
        if (!profile.AllowBiometricPersistenceByDefault)
        {
            _logger.LogDebug("Biometric opt-in skipped for {Label}: profile flag off", speakerLabel);
            return BiometricOptInOutcome.Skipped;
        }
        if (embedding is null || embedding.Length == 0)
        {
            _logger.LogWarning("Biometric opt-in skipped for {Label}: no embedding available", speakerLabel);
            return BiometricOptInOutcome.Skipped;
        }

        var prompt = ConsentPromptTemplates.BiometricOptInDe;
        try { await _tts.SpeakAsync(prompt.Text).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "TTS playback failed for biometric prompt"); }

        _auditLog.Append(new AuditEvent(
            Guid.NewGuid(), _clock.GetUtcNow(), "BIOMETRIC_PROMPTED", speakerLabel,
            new Dictionary<string, object?>
            {
                ["prompt_id"] = prompt.Id,
                ["prompt_hash"] = prompt.VersionHash,
                ["language"] = prompt.Language,
            }));

        if (_captureReplyAsync is null)
        {
            _logger.LogDebug("No reply-capture wired; biometric opt-in left pending for {Label}", speakerLabel);
            return BiometricOptInOutcome.Skipped;
        }

        string reply;
        try { reply = await _captureReplyAsync(speakerLabel, ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reply capture threw for biometric opt-in {Label}", speakerLabel);
            return BiometricOptInOutcome.Ambiguous;
        }

        var classification = await _classifier.ClassifyAsync(reply, prompt.Text, ct).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        switch (classification.Decision)
        {
            case ConsentDecision.Grant:
            {
                var expiresAt = now.AddMonths(profile.BiometricRetentionMonths);
                var entry = await _store.AddAsync(
                    speakerLabel, embedding, now, expiresAt, consentEvidencePath, prompt.VersionHash, ct)
                    .ConfigureAwait(false);
                _auditLog.Append(new AuditEvent(
                    Guid.NewGuid(), now, "BIOMETRIC_CONSENT_GRANTED", speakerLabel,
                    new Dictionary<string, object?>
                    {
                        ["entryId"] = entry.Id,
                        ["expiresAt"] = expiresAt,
                        ["promptHash"] = prompt.VersionHash,
                    }));
                return BiometricOptInOutcome.Granted;
            }
            case ConsentDecision.Deny:
                _auditLog.Append(new AuditEvent(
                    Guid.NewGuid(), now, "BIOMETRIC_CONSENT_DENIED", speakerLabel,
                    new Dictionary<string, object?> { ["promptHash"] = prompt.VersionHash }));
                return BiometricOptInOutcome.Denied;
            default:
                _auditLog.Append(new AuditEvent(
                    Guid.NewGuid(), now, "BIOMETRIC_CONSENT_AMBIGUOUS", speakerLabel,
                    new Dictionary<string, object?> { ["promptHash"] = prompt.VersionHash }));
                return BiometricOptInOutcome.Ambiguous;
        }
    }
}
