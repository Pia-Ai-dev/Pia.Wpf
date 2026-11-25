namespace Pia.Services.Consent;

/// <summary>
/// Proof of one speaker's spoken consent, written once at the grant. It holds the sentence itself, so it may be
/// persisted DPAPI-protected but must never reach a log line, an audit event, or a UI surface outside DEBUG.
/// </summary>
/// <param name="SpeakerLabel">The label the speaker was detected under; it names the evidence file.</param>
/// <param name="ExtractedName">The name the speaker introduced themselves with, or <c>null</c>.</param>
/// <param name="ConsentSentence">The verbatim recognised utterance that constitutes the consent.</param>
/// <param name="Language">The language whose lexicon matched: <c>"en"</c>, <c>"de"</c> or <c>"fr"</c>.</param>
/// <param name="Confidence">Classifier confidence in <c>[0,1]</c> at the moment of the grant.</param>
/// <param name="SttModelId">Identifier of the speech-to-text model that produced the sentence.</param>
/// <param name="NoticeVersion">The <see cref="ConsentNotice"/> version the user had accepted.</param>
/// <param name="NoticeLanguage">The language that notice was shown in, not the one the speaker used.</param>
public sealed record ConsentEvidence(
    string SpeakerLabel,
    string? ExtractedName,
    string ConsentSentence,
    string Language,
    float Confidence,
    DateTimeOffset GrantedAt,
    string SttModelId,
    int NoticeVersion,
    IReadOnlyList<string> NoticePurposes,
    string NoticeLanguage);
