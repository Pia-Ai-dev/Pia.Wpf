namespace Pia.Services.Consent;

/// <summary>
/// Immutable SNAPSHOT of one speaker's consent state. Never handed out live: the manager has two
/// writers in v1 (the background forward loop and a UI-thread revoke), so a mutable entry escaping
/// the manager's lock would be an unsynchronised shared object no lock could protect.
/// </summary>
/// <param name="SpeakerLabel">The diarizer label this entry is keyed by, as of the snapshot.</param>
/// <param name="FirstDetected">When the label was first observed in this session.</param>
/// <param name="State">Consent state as of the snapshot.</param>
/// <param name="ExtractedName">
/// The name captured from the consent sentence, or <c>null</c> when never granted or not capturable.
/// </param>
/// <param name="Evidence">
/// The grant evidence, or <c>null</c> when never granted. Preserved across a revocation.
/// </param>
public sealed record SpeakerConsentEntry(
    string SpeakerLabel,
    DateTimeOffset FirstDetected,
    ConsentState State,
    string? ExtractedName,
    ConsentEvidence? Evidence);
