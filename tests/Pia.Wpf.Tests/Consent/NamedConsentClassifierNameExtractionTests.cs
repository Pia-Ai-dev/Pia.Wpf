using Microsoft.Extensions.Logging.Abstractions;
using Pia.Models;
using Pia.Services.Consent;
using Xunit;

namespace Pia.Tests.Consent;

/// <summary>
/// Focused coverage of the name-capture rule inside <see cref="NamedConsentClassifier"/>: 1-4 tokens,
/// letters/hyphens/apostrophes only, title-cased on output regardless of the input's original casing,
/// and rejected outright when the capture is entirely lexicon vocabulary rather than a real name.
/// </summary>
public sealed class NamedConsentClassifierNameExtractionTests
{
    private readonly NamedConsentClassifier _sut = new(NullLogger<NamedConsentClassifier>.Instance);

    private static string Sentence(string name) =>
        $"my name is {name} and I accept that this is recorded by Pia";

    [Fact]
    public void HyphenatedName_KeepsHyphenAndTitleCasesBothHalves()
    {
        var result = _sut.Classify(Sentence("Anne-Marie Dupont"), TargetSpeechLanguage.EN);

        Assert.True(result.IsConsent);
        Assert.Equal("Anne-Marie Dupont", result.ExtractedName);
    }

    [Fact]
    public void SingleTokenName_IsCaptured()
    {
        var result = _sut.Classify(Sentence("Bob"), TargetSpeechLanguage.EN);

        Assert.True(result.IsConsent);
        Assert.Equal("Bob", result.ExtractedName);
    }

    [Fact]
    public void FourTokenName_IsCapturedAtTheCap_NotTruncatedOrRejected()
    {
        // Exactly 4 tokens is the documented maximum for a legitimate capture — distinct from the
        // 5+-token case in NamedConsentClassifierTests.RejectCases, which must be REJECTED rather than
        // truncated down to 4.
        var result = _sut.Classify(Sentence("John Jacob Jingleheimer Schmidt"), TargetSpeechLanguage.EN);

        Assert.True(result.IsConsent);
        Assert.Equal("John Jacob Jingleheimer Schmidt", result.ExtractedName);
    }

    [Fact]
    public void ApostropheName_IsCapturedAndTitleCasedAfterTheApostrophe()
    {
        var result = _sut.Classify(Sentence("O'Brien"), TargetSpeechLanguage.EN);

        Assert.True(result.IsConsent);
        Assert.Equal("O'Brien", result.ExtractedName);
    }

    [Theory]
    [InlineData("bob")]
    [InlineData("BOB")]
    [InlineData("bOb")]
    public void LowercaseOrMixedCaseInput_IsAlwaysTitleCasedOnOutput(string rawName)
    {
        var result = _sut.Classify(Sentence(rawName), TargetSpeechLanguage.EN);

        Assert.True(result.IsConsent);
        Assert.Equal("Bob", result.ExtractedName);
    }

    [Fact]
    public void HyphenatedNameFromAnAllLowercaseUtterance_IsTitleCasedOnBothSidesOfTheHyphen()
    {
        var result = _sut.Classify(
            "my name is anne-marie dupont and i accept that this is recorded by pia",
            TargetSpeechLanguage.EN);

        Assert.True(result.IsConsent);
        Assert.Equal("Anne-Marie Dupont", result.ExtractedName);
    }

    /// <summary>
    /// Every category of word that can never be part of a name. Each of these is a STOP token, so the
    /// capture ends before it and the count-0 guard rejects the sentence — which is stronger than the old
    /// "reject only if the capture is ENTIRELY lexicon vocabulary" rule, because that one was defeated by
    /// a single filler token ("Recorded By Pia" was accepted as a person's name, since "by" was in no
    /// table). The table deliberately spans all five categories.
    /// </summary>
    public static TheoryData<string, TargetSpeechLanguage> NameIsALexiconWordCases => new()
    {
        { Sentence("Pia"), TargetSpeechLanguage.EN },      // the required Pia reference itself
        { Sentence("Recorded"), TargetSpeechLanguage.EN }, // a recording-lexicon word
        { Sentence("Accept"), TargetSpeechLanguage.EN },   // an acceptance-lexicon word
        { Sentence("Meeting"), TargetSpeechLanguage.EN },  // a booster word
        { Sentence("Not"), TargetSpeechLanguage.EN },      // a negation word
        { Sentence("The"), TargetSpeechLanguage.EN },      // a grammatical function word
    };

    [Theory]
    [MemberData(nameof(NameIsALexiconWordCases))]
    public void NameThatIsAlsoALexiconWord_IsRejected(string text, TargetSpeechLanguage hint)
    {
        var result = _sut.Classify(text, hint);

        Assert.False(result.IsConsent, $"a name capture that is lexicon vocabulary must be rejected: [{text}]");
        Assert.Null(result.ExtractedName);
    }

    [Fact]
    public void NonVacuity_LexiconWordNameTable_CoversEveryRejectedWordCategory()
    {
        Assert.True(
            NameIsALexiconWordCases.Count >= 6,
            "non-vacuity: expected Pia/recording/acceptance/booster/negation/function-word coverage");
    }

    [Fact]
    public void NameCapture_StopsAtAFunctionWord_RatherThanSwallowingIt()
    {
        // "from Acme" is not part of the name. Terminating the capture (rather than rejecting the whole
        // sentence, and rather than capturing four tokens) keeps a legitimate grant while putting only the
        // actual name into the DPAPI-protected evidence.
        var result = _sut.Classify(
            "I am John Doe from Acme and I accept that this meeting is recorded by Pia",
            TargetSpeechLanguage.EN);

        Assert.True(result.IsConsent);
        Assert.Equal("John Doe", result.ExtractedName);
    }

    [Fact]
    public void GenuineIntroductionWins_OverAnIncidentalEarlierPhrase()
    {
        // An incidental "this is" opening the utterance must not beat the real self-introduction later in
        // it. Returning on the first marker MATCH (rather than the first VALID capture, in preference
        // order) captured "important" and filed it as the consenting person's name.
        var result = _sut.Classify(
            "this is important, I am John Doe and I accept that this is recorded by Pia",
            TargetSpeechLanguage.EN);

        Assert.True(result.IsConsent);
        Assert.Equal("John Doe", result.ExtractedName);
    }

    [Fact]
    public void ThisIsMarker_OnlyCountsAtTheStartOfTheUtterance()
    {
        // Both halves of the rule in one place: "This is <name>" opening an utterance IS a self
        // introduction, the same words mid-sentence are ordinary prose and must not satisfy component 1.
        var atStart = _sut.Classify(
            "This is John Doe, I consent to this conversation being recorded by Pia",
            TargetSpeechLanguage.EN);
        var midSentence = _sut.Classify(
            "I accept that this is recorded by Pia",
            TargetSpeechLanguage.EN);

        Assert.True(atStart.IsConsent);
        Assert.Equal("John Doe", atStart.ExtractedName);

        Assert.False(midSentence.IsConsent, "an utterance with no name introduction must not grant");
        Assert.Null(midSentence.ExtractedName);
    }
}
