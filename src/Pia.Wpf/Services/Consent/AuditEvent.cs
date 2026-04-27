namespace Pia.Services.Consent;

/// <summary>
/// Append-only audit log event. <see cref="Details"/> is opaque metadata only — never include
/// raw transcript text in this dictionary. Free-text utterance content must never be persisted
/// to the audit log; per the spec, only reason codes and identifiers are allowed.
/// </summary>
public sealed record AuditEvent(
    Guid EventId,
    DateTimeOffset Timestamp,
    string EventType,
    string? SpeakerLabel,
    IReadOnlyDictionary<string, object?>? Details);
