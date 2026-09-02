using Pia.Models;

namespace Pia.Services.Consent;

/// <summary>
/// The four components a consent sentence must carry, in the order they are checked. Names a component
/// only, never any transcribed text, so it is safe in a release log.
/// </summary>
public enum ConsentComponent
{
    /// <summary>A self-introduction marker followed by a capturable name.</summary>
    NameIntroduction,

    /// <summary>An acceptance verb with no negation scoped to its clause.</summary>
    Acceptance,

    /// <summary>A reference to the recording itself.</summary>
    RecordingReference,

    /// <summary>A reference to Pia (fuzzy-matched, but a hard requirement).</summary>
    PiaReference,
}

/// <summary>
/// Result of classifying one utterance as a spoken consent sentence.
/// </summary>
/// <param name="IsConsent">
/// <c>true</c> only when ALL FOUR required components matched — a name introduction with a capturable
/// name, an acceptance verb, a recording reference, and a reference to Pia — and no negation was found.
/// </param>
/// <param name="ExtractedName">
/// The name the speaker introduced themselves with, or <c>null</c> when <paramref name="IsConsent"/> is
/// <c>false</c>. Sensitive — never log it unguarded.
/// </param>
/// <param name="Language">
/// <c>"en"</c> | <c>"de"</c> | <c>"fr"</c> — the language whose lexicon matched. Always set, including
/// on a no-consent result, so the caller can record which lexicon was consulted.
/// </param>
/// <param name="Confidence">
/// Confidence in <c>[0,1]</c>. 0 when not a consent sentence. When it is one, the value is either
/// <c>NamedConsentClassifier.CrispConfidence</c> (all four components matched verbatim) or
/// <c>NamedConsentClassifier.RepairedConfidence</c> (at least one component was repaired by a fuzzy match).
/// </param>
public sealed record NamedConsentResult(
    bool IsConsent,
    string? ExtractedName,
    string Language,
    float Confidence)
{
    /// <summary>
    /// The first of the four components that was not found, or <c>null</c> when the sentence was
    /// recognised (or was empty). Diagnostic only — nothing branches on it. It exists because a
    /// participant who has to repeat the sentence several times has no way to learn what was missing,
    /// and neither did anyone reading their support log.
    /// </summary>
    public ConsentComponent? MissingComponent { get; init; }

    /// <summary>The canonical negative result for a given language.</summary>
    public static NamedConsentResult NoConsent(string language) => new(false, null, language, 0f);

    /// <summary>Negative result that also names the component that stopped it.</summary>
    public static NamedConsentResult NoConsent(string language, ConsentComponent missing)
        => new(false, null, language, 0f) { MissingComponent = missing };
}

/// <summary>
/// Recognises a speaker-initiated spoken consent sentence in a single transcribed utterance.
/// Nothing prompts the speaker: the classifier only ever inspects text that already exists.
/// </summary>
public interface INamedConsentClassifier
{
    /// <summary>
    /// Pure, synchronous, allocation-light. Must never throw for any input, including <c>null</c>, empty,
    /// or garbage text — it runs inside the forward loop, where a throw would cost an utterance.
    /// </summary>
    /// <param name="utteranceText">The transcribed utterance to inspect.</param>
    /// <param name="languageHint">
    /// The session's configured speech language. <see cref="TargetSpeechLanguage.Auto"/> means the
    /// implementation tries every supported lexicon and reports the one that matched.
    /// </param>
    NamedConsentResult Classify(string utteranceText, TargetSpeechLanguage languageHint);
}
