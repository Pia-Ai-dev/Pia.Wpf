namespace Pia.Services.Consent;

/// <summary>
/// Persists consent evidence so a grant can still be proven after the session ends. Write-only; every method
/// throws on failure, and the caller audits it (<see cref="ConsentAuditEventTypes.EvidenceWriteFailed"/>).
/// </summary>
public interface IConsentEvidenceStore
{
    /// <summary>Writes a grant's evidence file, and the session's marker with the session's first grant.</summary>
    /// <exception cref="Exception">Any encryption or I/O failure propagates — nothing is swallowed.</exception>
    Task SaveGrantAsync(ConsentSessionMarker session, ConsentEvidence evidence, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a revocation record BESIDE the grant evidence. The grant evidence is never modified, and it is
    /// only ever removed by the retention window: withdrawing consent ends the processing, it does not erase
    /// the proof that consent existed.
    /// </summary>
    /// <param name="sessionId">Session the revoked grant belongs to.</param>
    /// <param name="speakerLabel">The label whose consent was withdrawn.</param>
    /// <param name="revokedAt">When the withdrawal happened.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="Exception">Any encryption or I/O failure propagates — nothing is swallowed.</exception>
    Task SaveRevocationAsync(string sessionId, string speakerLabel, DateTimeOffset revokedAt, CancellationToken cancellationToken = default);
}
