namespace Pia.Services.Consent;

/// <summary>
/// Persists consent evidence so a grant can still be proven after the session ends
/// (Art. 7 GDPR Nachweispflicht). Write-only in v1: there is no reader, no expiry stamp and no cleanup
/// worker — retention is a v2 concern.
///
/// <para>Both methods THROW on failure. That is the contract, not an oversight: the defect this store
/// exists to fix was a silent success path that persisted nothing at all. The caller audits the failure
/// (<see cref="ConsentAuditEventTypes.EvidenceWriteFailed"/>) and continues.</para>
/// </summary>
public interface IConsentEvidenceStore
{
    /// <summary>
    /// Writes one DPAPI-protected evidence file for a grant.
    /// </summary>
    /// <param name="sessionId">Session the grant belongs to; scopes the file name or folder.</param>
    /// <param name="evidence">The evidence to persist. Written once and never modified afterwards.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="Exception">Any encryption or I/O failure propagates — nothing is swallowed.</exception>
    Task SaveGrantAsync(string sessionId, ConsentEvidence evidence, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a revocation record BESIDE the grant evidence. The grant evidence is never modified or
    /// deleted: withdrawing consent ends the processing, it does not erase the proof that consent existed.
    /// </summary>
    /// <param name="sessionId">Session the revoked grant belongs to.</param>
    /// <param name="speakerLabel">The label whose consent was withdrawn.</param>
    /// <param name="revokedAt">When the withdrawal happened.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="Exception">Any encryption or I/O failure propagates — nothing is swallowed.</exception>
    Task SaveRevocationAsync(string sessionId, string speakerLabel, DateTimeOffset revokedAt, CancellationToken cancellationToken = default);
}
