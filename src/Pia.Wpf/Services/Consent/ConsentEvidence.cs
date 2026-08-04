namespace Pia.Services.Consent;

/// <summary>
/// Immutable proof that one speaker gave spoken consent (Art. 7 GDPR Nachweispflicht).
/// Written once, at the moment of the grant, and never modified — a later revocation is recorded
/// BESIDE this record, not over it.
///
/// <para>This record carries the consent sentence itself, so it is sensitive by definition: it may be
/// persisted (DPAPI-protected) but must never reach a log line, an audit event, or a UI surface
/// outside DEBUG.</para>
/// </summary>
/// <param name="SpeakerLabel">The diarizer label that spoke the sentence, at the time of the grant.</param>
/// <param name="ExtractedName">
/// The name the speaker introduced themselves with, or <c>null</c> when it could not be captured.
/// </param>
/// <param name="ConsentSentence">The verbatim recognised utterance that constitutes the consent.</param>
/// <param name="Language">The language whose lexicon matched: <c>"en"</c>, <c>"de"</c> or <c>"fr"</c>.</param>
/// <param name="Confidence">Classifier confidence in <c>[0,1]</c> at the moment of the grant.</param>
/// <param name="GrantedAt">When the grant was recorded.</param>
/// <param name="SttModelId">Identifier of the speech-to-text model that produced the sentence.</param>
public sealed record ConsentEvidence(
    string SpeakerLabel,
    string? ExtractedName,
    string ConsentSentence,
    string Language,
    float Confidence,
    DateTimeOffset GrantedAt,
    string SttModelId);
