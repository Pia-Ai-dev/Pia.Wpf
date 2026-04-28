namespace Pia.Services.Consent.Biometric;

/// <summary>
/// Evidence record produced when a speaker grants biometric (cross-session) consent.
/// Sits alongside the regular <see cref="ConsentEvidence"/> — biometric persistence is
/// a separate Art. 9 DSGVO scope (spec §2.4) requiring its own grant moment.
/// </summary>
public sealed record BiometricConsentEvidence(
    Guid EntryId,
    string TranscriptText,
    float ClassificationConfidence,
    DateTimeOffset GrantedAt,
    DateTimeOffset ExpiresAt,
    string PromptVersionHash,
    string PromptTextPlayed,
    string SttModelId);
