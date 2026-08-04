namespace Pia.Services.Consent;

/// <summary>
/// One line of the append-only consent audit trail. METADATA ONLY — an audit event must never carry
/// transcript text, a consent sentence, or an extracted name; the proof of consent lives in
/// <see cref="ConsentEvidence"/>, which is protected separately.
///
/// <para>The old branch's <c>PreviousEventHash</c>/<c>Signature</c> fields are deliberately absent: the
/// hash chain and its verifier are v2, and two permanently-null columns are not "the mechanism ported".</para>
/// </summary>
/// <param name="EventId">Unique id of this event.</param>
/// <param name="Timestamp">When the event was raised.</param>
/// <param name="EventType">One of the <see cref="ConsentAuditEventTypes"/> constants.</param>
/// <param name="SpeakerLabel">The label the event concerns, or <c>null</c> for session-level events.</param>
/// <param name="Details">
/// Optional non-sensitive detail bag (counts, durations, language codes, model ids). Must be
/// JSON-serialisable.
/// </param>
public sealed record AuditEvent(
    Guid EventId,
    DateTimeOffset Timestamp,
    string EventType,
    string? SpeakerLabel,
    IReadOnlyDictionary<string, object?>? Details);

/// <summary>
/// The frozen vocabulary of <see cref="AuditEvent.EventType"/> values. Constants (not an enum) because
/// the audit trail is a stable on-disk format that must round-trip an unknown future event type as text.
/// </summary>
public static class ConsentAuditEventTypes
{
    /// <summary>A transcription session began; a new session id was issued.</summary>
    public const string SessionStarted = "SESSION_STARTED";

    /// <summary>A transcription session ended.</summary>
    public const string SessionStopped = "SESSION_STOPPED";

    /// <summary>The diarizer registered a previously unseen speaker label.</summary>
    public const string SpeakerDetected = "SPEAKER_DETECTED";

    /// <summary>A speaker's spoken consent was recognised and recorded.</summary>
    public const string ConsentGranted = "CONSENT_GRANTED";

    /// <summary>A speaker's consent was withdrawn in-session.</summary>
    public const string ConsentRevoked = "CONSENT_REVOKED";

    /// <summary>Persisting the consent evidence failed; the grant itself still stands in-session.</summary>
    public const string EvidenceWriteFailed = "EVIDENCE_WRITE_FAILED";

    /// <summary>System-audio speech was dropped because it carried no diarizer label (unattributable).</summary>
    public const string DroppedUnlabeledLoopback = "DROPPED_UNLABELED_LOOPBACK";

    /// <summary>Speech was dropped because its speaker had not consented.</summary>
    public const string DroppedUnconsented = "DROPPED_UNCONSENTED";
}
