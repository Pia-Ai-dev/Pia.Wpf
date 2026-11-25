namespace Pia.Services.Consent;

/// <summary>
/// Persists consent evidence so it can still be proven after the session ends, plus a log of the copies the
/// session's transcript went into. The evidence writers throw on failure; the copies log never throws.
/// </summary>
public interface IConsentEvidenceStore
{
    /// <summary>Writes a grant's evidence file, and the session's marker with the session's first grant.</summary>
    /// <exception cref="Exception">Any encryption or I/O failure propagates — nothing is swallowed.</exception>
    Task SaveGrantAsync(ConsentSessionMarker session, ConsentEvidence evidence, CancellationToken cancellationToken = default);

    /// <summary>Writes a meeting host's acknowledgement, which stands in for per-speaker grants, and the session's marker.</summary>
    /// <exception cref="Exception">Any encryption or I/O failure propagates — nothing is swallowed.</exception>
    Task SaveHostAcknowledgementAsync(
        ConsentSessionMarker session, DateTimeOffset acknowledgedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a revocation record BESIDE the grant evidence. The grant evidence is never modified: withdrawing
    /// consent ends the processing, it does not erase the proof that consent existed.
    /// </summary>
    /// <exception cref="Exception">Any encryption or I/O failure propagates — nothing is swallowed.</exception>
    Task SaveRevocationAsync(string sessionId, string speakerLabel, DateTimeOffset revokedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends <paramref name="copy"/> to the session's copies log. Skipped for a session nobody consented in,
    /// which has no evidence folder; a failed write is logged, never thrown, so it cannot fail a save.
    /// </summary>
    Task AppendCopyAsync(string sessionId, ConsentCopy copy, CancellationToken cancellationToken = default);

    /// <summary>The session's logged copies, oldest first; empty when there are none, <c>null</c> when the log cannot be read.</summary>
    Task<IReadOnlyList<ConsentCopy>?> ReadCopiesAsync(string sessionId, CancellationToken cancellationToken = default);
}
