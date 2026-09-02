using Microsoft.Extensions.Logging.Abstractions;
using Pia.Localization;
using Pia.Models;
using Pia.Services.Consent;
using Pia.Tests;
using Xunit;

namespace Pia.Tests.Consent;

public sealed class NamedConsentClassifierTests
{
    private readonly NamedConsentClassifier _sut = new(NullLogger<NamedConsentClassifier>.Instance);

    public static TheoryData<string, string, TargetSpeechLanguage, string, string> AcceptCrispCases => new()
    {
        { "EN verbatim", "my name is John Doe and I accept that this meeting gets recorded by Pia", TargetSpeechLanguage.EN, "John Doe", "en" },
        { "EN 'i am' + agree + call", "I am John Doe and I agree that this call is being recorded by Pia", TargetSpeechLanguage.EN, "John Doe", "en" },
        { "EN 'this is' + consent", "This is John Doe, I consent to this conversation being recorded by Pia", TargetSpeechLanguage.EN, "John Doe", "en" },
        { "EN 'i'm' + i'm ok with + taped", "I'm John Doe and I'm ok with this being taped by Pia", TargetSpeechLanguage.EN, "John Doe", "en" },

        { "DE verbatim", "mein Name ist John Doe und ich bin einverstanden, dass dieses Gespräch von Pia aufgezeichnet wird", TargetSpeechLanguage.DE, "John Doe", "de" },
        { "DE 'ich heisse' + akzeptiere + aufgenommen", "Ich heiße John Doe und ich akzeptiere, dass diese Aufnahme von Pia aufgenommen wird", TargetSpeechLanguage.DE, "John Doe", "de" },
        { "DE 'ich bin' + stimme zu", "Ich bin John Doe und ich stimme zu, dass diese Besprechung von Pia aufgezeichnet wird", TargetSpeechLanguage.DE, "John Doe", "de" },
        { "DE willige ein + mitschnitt", "Mein Name ist John Doe und ich willige ein, dass dieser Mitschnitt von Pia gemacht wird", TargetSpeechLanguage.DE, "John Doe", "de" },

        { "FR verbatim", "je m'appelle John Doe et j'accepte que cette réunion soit enregistrée par Pia", TargetSpeechLanguage.FR, "John Doe", "fr" },
        { "FR 'mon nom est' + consens", "Mon nom est John Doe et je consens à cet appel enregistré par Pia", TargetSpeechLanguage.FR, "John Doe", "fr" },
        { "FR 'je suis' + suis d'accord", "Je suis John Doe et je suis d'accord que cet enregistrement soit fait par Pia", TargetSpeechLanguage.FR, "John Doe", "fr" },
        { "FR 'je suis' + j'accepte", "Je suis John Doe et j'accepte que cet appel soit enregistré par Pia", TargetSpeechLanguage.FR, "John Doe", "fr" },
    };

    [Theory]
    [MemberData(nameof(AcceptCrispCases))]
    public void AcceptCrisp_AllFourComponentsVerbatim_GrantsAtCrispConfidence(
        string label, string text, TargetSpeechLanguage hint, string expectedName, string expectedLanguage)
    {
        var result = _sut.Classify(text, hint);

        Assert.True(result.IsConsent, $"{label}: expected consent to be recognised");
        Assert.Equal(expectedName, result.ExtractedName);
        Assert.Equal(expectedLanguage, result.Language);
        Assert.Equal(NamedConsentClassifier.CrispConfidence, result.Confidence);
    }

    public static TheoryData<string, string, TargetSpeechLanguage, string?, string> AcceptRepairedCases => new()
    {
        { "EN Pia -> pea", "my name is john doe and i accept that this meeting gets recorded by pea", TargetSpeechLanguage.EN, "John Doe", "en" },
        { "EN Pia -> peer", "my name is john doe and i accept that this meeting gets recorded by peer", TargetSpeechLanguage.EN, "John Doe", "en" },
        // Observed from Parakeet in a live session. 3 edits from "pia", so rule (b) never reaches it —
        // only the curated alias list can, and without the alias the sentence could not be accepted at all.
        { "EN Pia -> pieer (Parakeet, observed live)", "my name is john doe and i accept this recording by pieer", TargetSpeechLanguage.EN, "John Doe", "en" },
        { "EN Pia -> letter-spelled p i a", "my name is john doe and i accept that this meeting gets recorded by p i a", TargetSpeechLanguage.EN, "John Doe", "en" },
        { "EN acceptance typo (acept)", "my name is John Doe and I acept that this meeting gets recorded by Pia", TargetSpeechLanguage.EN, "John Doe", "en" },
        // Also covers a raw STT shape: no punctuation and no capitalisation anywhere.
        { "DE Pia -> pija", "mein name ist john doe und ich bin einverstanden dass dieses gesprach von pija aufgezeichnet wird", TargetSpeechLanguage.DE, "John Doe", "de" },
        { "FR three repairs at once (mappelle/jaccepte/pya)", "je mappelle jean dupont et jaccepte que cette reunion soit enregistree par pya", TargetSpeechLanguage.FR, null, "fr" },
    };

    [Theory]
    [MemberData(nameof(AcceptRepairedCases))]
    public void AcceptRepaired_AnyComponentFuzzyMatched_GrantsAtRepairedConfidence(
        string label, string text, TargetSpeechLanguage hint, string? expectedName, string expectedLanguage)
    {
        var result = _sut.Classify(text, hint);

        Assert.True(result.IsConsent, $"{label}: expected consent to be recognised despite STT mangling");
        if (expectedName is not null)
            Assert.Equal(expectedName, result.ExtractedName);
        Assert.Equal(expectedLanguage, result.Language);
        Assert.Equal(NamedConsentClassifier.RepairedConfidence, result.Confidence);
    }

    public static TheoryData<string, string, TargetSpeechLanguage> RejectCases => new()
    {
        { "name only", "my name is John Doe", TargetSpeechLanguage.EN },
        { "acceptance only", "I accept", TargetSpeechLanguage.EN },
        { "name + acceptance, no recording word", "my name is John Doe and I accept", TargetSpeechLanguage.EN },

        { "EN: no Pia reference (recorded by the system)", "my name is John Doe and I accept that this meeting is recorded by the system", TargetSpeechLanguage.EN },
        { "DE: no Pia reference (vom System)", "mein Name ist John Doe und ich bin einverstanden dass dieses Gespräch vom System aufgezeichnet wird", TargetSpeechLanguage.DE },
        { "FR: no Pia reference (par le système)", "je m'appelle John Doe et j'accepte que cette réunion soit enregistrée par le système", TargetSpeechLanguage.FR },

        { "empty name (marker with nothing after it)", "my name is and I accept that this is recorded by Pia", TargetSpeechLanguage.EN },
        { "name capture of 5+ tokens is rejected, not truncated", "my name is John Jacob Jingleheimer Schmidt Doe and I accept that this is recorded by Pia", TargetSpeechLanguage.EN },
        { "name that is itself a Pia reference", "my name is Pia and I accept that this is recorded by Pia", TargetSpeechLanguage.EN },
        { "name that is itself a recording-lexicon word", "my name is Recorded and I accept that this is recorded by Pia", TargetSpeechLanguage.EN },

        // An incidental "this is" is prose, not a self-introduction: it once captured "recorded by pia" as a name.
        { "acceptance + recording + Pia but no name introduction", "I accept that this is recorded by Pia", TargetSpeechLanguage.EN },
        { "same, with a leading filler clause", "well anyway I accept that this is recorded by Pia", TargetSpeechLanguage.EN },

        { "no acceptance verb; 'content' must not repair into 'consent'", "My name is John Doe and the content of this meeting is recorded by Pia", TargetSpeechLanguage.EN },
        { "no acceptance verb; 'accent' must not repair into 'accept'", "My name is John Doe and the accent in this recording by Pia is strong", TargetSpeechLanguage.EN },
    };

    [Theory]
    [MemberData(nameof(RejectCases))]
    public void Reject_MissingOrInvalidComponent_ReturnsNoConsent(string label, string text, TargetSpeechLanguage hint)
    {
        var result = _sut.Classify(text, hint);

        Assert.False(result.IsConsent, $"{label}: expected no consent");
        Assert.Null(result.ExtractedName);
        Assert.Equal(0f, result.Confidence);
    }

    public static TheoryData<string, string, TargetSpeechLanguage, ConsentComponent> MissingComponentCases => new()
    {
        { "no name introduction", "I accept that this is recorded by Pia", TargetSpeechLanguage.EN, ConsentComponent.NameIntroduction },
        { "no acceptance verb", "My name is John Doe and this meeting is recorded by Pia", TargetSpeechLanguage.EN, ConsentComponent.Acceptance },
        { "no recording reference", "My name is John Doe and I accept that Pia is here", TargetSpeechLanguage.EN, ConsentComponent.RecordingReference },
        { "no Pia reference", "My name is John Doe and I accept that this meeting is recorded", TargetSpeechLanguage.EN, ConsentComponent.PiaReference },
        { "DE no Pia reference", "Mein Name ist John Doe und ich bin einverstanden, dass dieses Gespräch aufgezeichnet wird", TargetSpeechLanguage.DE, ConsentComponent.PiaReference },
    };

    /// <summary>
    /// The reported component is the one the HINTED language stopped on: the other two lexicons are tried
    /// only in case the speaker answered in their own language, so their verdict describes the wrong
    /// lexicon rather than the sentence.
    /// </summary>
    [Theory]
    [MemberData(nameof(MissingComponentCases))]
    public void Reject_NamesTheComponentThatWasMissing(
        string label, string text, TargetSpeechLanguage hint, ConsentComponent expected)
    {
        var result = _sut.Classify(text, hint);

        Assert.False(result.IsConsent, $"{label}: expected no consent");
        Assert.Equal(expected, result.MissingComponent);
    }

    [Fact]
    public void Accept_ReportsNoMissingComponent()
    {
        var result = _sut.Classify(
            "my name is John Doe and I accept that this meeting gets recorded by Pia", TargetSpeechLanguage.EN);

        Assert.True(result.IsConsent);
        Assert.Null(result.MissingComponent);
    }

    public static TheoryData<string, string, TargetSpeechLanguage> NegatedCases => new()
    {
        { "EN 'do not accept'", "my name is John Doe and I do not accept that this meeting is recorded by Pia", TargetSpeechLanguage.EN },
        { "DE 'nicht einverstanden'", "mein Name ist John Doe und ich bin nicht einverstanden dass dieses Gespräch von Pia aufgezeichnet wird", TargetSpeechLanguage.DE },
        { "FR 'ne accepte pas' (elision split by STT)", "je m'appelle Jean Dupont et je ne accepte pas que cette réunion soit enregistrée par Pia", TargetSpeechLanguage.FR },

        // Negation can sit well outside any fixed token window: verb-final in German, modifier-stacked in English.
        { "DE verb-final 'nicht' (Satzklammer)", "Mein Name ist Anna Müller und ich akzeptiere die Aufzeichnung durch Pia nicht", TargetSpeechLanguage.DE },
        { "EN 'do not really want to accept'", "my name is John Doe and I do not really want to accept that this meeting is recorded by Pia", TargetSpeechLanguage.EN },

        // Every acceptance occurrence must be negation-checked, not just the first in lexicon-table order.
        { "EN accepts something else but refuses the recording", "I'm Tom and I accept the invitation but I do not agree to be recorded by Pia", TargetSpeechLanguage.EN },
        { "EN agrees to the agenda but not to the recording", "my name is John Doe and I agree to the agenda but I do not consent to this being recorded by Pia", TargetSpeechLanguage.EN },

        { "EN 'cannot accept'", "my name is John Doe and I cannot accept that this meeting is recorded by Pia", TargetSpeechLanguage.EN },
        { "EN 'decline'", "my name is John Doe and I decline to accept that this meeting is recorded by Pia", TargetSpeechLanguage.EN },
        { "DE 'nein'", "Mein Name ist John Doe und nein ich bin einverstanden dass Pia das Gespräch aufzeichnet", TargetSpeechLanguage.DE },

        // Negation needs the same fuzzy repair as the grant components, or noise biases the classifier to granting.
        { "DE mangled 'nich' still refuses", "mein name ist john doe und ich bin nich einverstanden dass dieses gesprach von pia aufgezeichnet wird", TargetSpeechLanguage.DE },

        // "n'accepte" is one edit from the lexicon's "j'accepte", and the elision leaves no standalone "ne" token.
        { "FR merged \"n'accepte\"", "mon nom est Jean Dupont et je n'accepte point que cette réunion soit enregistrée par Pia", TargetSpeechLanguage.FR },
    };

    [Theory]
    [MemberData(nameof(NegatedCases))]
    public void Reject_NegatedAcceptance_ReturnsNoConsent(string label, string text, TargetSpeechLanguage hint)
    {
        var result = _sut.Classify(text, hint);
        Assert.False(result.IsConsent, $"{label}: negated acceptance must not grant consent");
    }

    [Fact]
    public void NonVacuity_NegatedCases_ProbeMoreThanAdjacentNegation()
    {
        Assert.True(NegatedCases.Count >= 10, "non-vacuity: expected far-negation, multi-clause and mangled-negation rows");
    }

    private static string Personalize(string template)
        => template.Replace("[Name]", "Anna Schmidt", StringComparison.Ordinal)
                   .Replace("[Nom]", "Anna Schmidt", StringComparison.Ordinal);

    private const string InstructedEn = "My name is [Name] and I accept this recording by Pia.";
    private const string InstructedDe = "Mein Name ist [Name] und ich akzeptiere diese Aufzeichnung durch Pia.";
    private const string InstructedFr = "Je m’appelle [Nom] et j’accepte cet enregistrement par Pia.";

    // Shortened 2026-09 so the sentence fits in one breath: the VAD closes a segment on 512 ms of
    // silence and §3.5 requires all four components in ONE utterance, so a mid-sentence pause used to
    // make the sentence unmatchable. These are the wordings the app asked for until then, and anyone
    // who learned one of them — or pasted it into a meeting invite — must still be able to consent.
    public static TheoryData<string, string, TargetSpeechLanguage, string> LegacyInstructedSentenceCases => new()
    {
        {
            "en (pre-2026-09 wording)",
            "My name is [Name] and I accept that Pia is recording this conversation.",
            TargetSpeechLanguage.EN, "en"
        },
        {
            "de (pre-2026-09 wording, verb-final 'aufzeichnet')",
            "Mein Name ist [Name] und ich bin einverstanden, dass Pia dieses Gespräch aufzeichnet.",
            TargetSpeechLanguage.DE, "de"
        },
        {
            "fr (pre-2026-09 wording)",
            "Je m’appelle [Nom] et j’accepte que Pia enregistre cette conversation.",
            TargetSpeechLanguage.FR, "fr"
        },
    };

    [Theory]
    [MemberData(nameof(LegacyInstructedSentenceCases))]
    public void LegacyInstructedConsentSentence_IsStillRecognised(
        string label, string template, TargetSpeechLanguage hint, string expectedLanguage)
    {
        var result = _sut.Classify(Personalize(template), hint);

        Assert.True(result.IsConsent, $"{label}: the old instructed sentence must still grant consent");
        Assert.Equal("Anna Schmidt", result.ExtractedName);
        Assert.Equal(expectedLanguage, result.Language);
    }

    public static TheoryData<string, string, TargetSpeechLanguage, string> InstructedSentenceCases => new()
    {
        { "en (as shipped in CommonStrings.resx)", InstructedEn, TargetSpeechLanguage.EN, "en" },
        { "de (as shipped in CommonStrings.resx)", InstructedDe, TargetSpeechLanguage.DE, "de" },
        { "fr (as shipped, typographic apostrophes U+2019)", InstructedFr, TargetSpeechLanguage.FR, "fr" },
        {
            "fr (same sentence with ASCII apostrophes, as some STT backends emit)",
            "Je m'appelle [Nom] et j'accepte cet enregistrement par Pia.",
            TargetSpeechLanguage.FR, "fr"
        },
    };

    [Theory]
    [MemberData(nameof(InstructedSentenceCases))]
    public void InstructedConsentSentence_IsRecognised(
        string label, string template, TargetSpeechLanguage hint, string expectedLanguage)
    {
        var text = Personalize(template);

        var result = _sut.Classify(text, hint);

        Assert.True(result.IsConsent, $"{label}: the instructed sentence must grant consent");
        Assert.Equal("Anna Schmidt", result.ExtractedName);
        Assert.Equal(expectedLanguage, result.Language);
        Assert.True(
            result.Confidence >= NamedConsentClassifier.GrantConfidenceThreshold,
            $"{label}: confidence {result.Confidence} must clear the grant threshold");
    }

    [Fact]
    public void InstructedConsentSentences_MatchTheShippedResourceValues()
    {
        var expectedByKey = new (string Key, string Sentence)[]
        {
            ("DirectTrans_Disclaimer_ConsentSentence_En", InstructedEn),
            ("DirectTrans_Disclaimer_ConsentSentence_De", InstructedDe),
            ("DirectTrans_Disclaimer_ConsentSentence_Fr", InstructedFr),
        };

        foreach (var (key, sentence) in expectedByKey)
        {
            var shipped = LocalizationSource.Instance[key];
            Assert.False(shipped.StartsWith('['), $"{key} must resolve to a real string, got '{shipped}'");
            Assert.Equal(sentence, shipped);
        }
    }

    [Fact]
    public void ConsentSplitAcrossTwoUtterances_NeitherHalfGrants()
    {
        var first = _sut.Classify("my name is John Doe and I accept", TargetSpeechLanguage.EN);
        var second = _sut.Classify("that this meeting is recorded by Pia", TargetSpeechLanguage.EN);

        Assert.False(first.IsConsent, "first half (name+acceptance only) must not grant");
        Assert.False(second.IsConsent, "second half (recording+Pia only, no name/acceptance) must not grant");
    }

    public static TheoryData<string> PiaFalseFriendWords => new()
    {
        "pita", "pisa", "pima", "pika", // blocklisted 4+ char false friends
        "via", "pie", "pin", "pit", "pig", "pip", // 3-char words the length>=4 gate excludes on its own
    };

    [Theory]
    [MemberData(nameof(PiaFalseFriendWords))]
    public void PiaFuzzyBoundary_FalseFriendWord_DoesNotSatisfyPiaComponent(string word)
    {
        var text = $"my name is John Doe and I accept that this meeting is recorded by {word}";
        var result = _sut.Classify(text, TargetSpeechLanguage.EN);

        Assert.False(result.IsConsent, $"'{word}' must not be accepted as a Pia reference");
    }

    [Theory]
    [InlineData("no problem", "en")]
    [InlineData("kein Problem", "de")]
    [InlineData("pas de problème", "fr")]
    public void D5Regression_NoProblemStandalone_IsNotConsentAndIsNotReportedAsRefusal(string text, string hintCode)
    {
        var hint = hintCode switch
        {
            "de" => TargetSpeechLanguage.DE,
            "fr" => TargetSpeechLanguage.FR,
            _ => TargetSpeechLanguage.EN,
        };

        var result = _sut.Classify(text, hint);

        // There is no deny decision in this design: absence of consent already means dropped.
        Assert.False(result.IsConsent);
        Assert.Null(result.ExtractedName);
        Assert.Equal(0f, result.Confidence);
    }

    [Fact]
    public void D5Regression_NoProblemFarBeforeAcceptanceClause_DoesNotFlipAnOtherwiseCompleteGrant()
    {
        // "no" sits 6 tokens before "accept", outside the negation guard's 3-tokens-before window.
        const string text = "my name is John Doe and by the way no problem at all today I accept that this meeting is recorded by Pia";

        var result = _sut.Classify(text, TargetSpeechLanguage.EN);

        Assert.True(result.IsConsent, "a stray 'no' far from the acceptance clause must not suppress consent");
        Assert.Equal("John Doe", result.ExtractedName);
        Assert.Equal(NamedConsentClassifier.CrispConfidence, result.Confidence);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...,,,!!!???")]
    public void Robustness_DegenerateInput_NeverThrowsAndFailsClosed(string? text)
    {
        var result = _sut.Classify(text!, TargetSpeechLanguage.EN);

        Assert.False(result.IsConsent);
        Assert.Null(result.ExtractedName);
        Assert.Equal(0f, result.Confidence);
    }

    [Fact]
    public void Robustness_VeryLongGarbageInput_NeverThrowsAndFailsClosed()
    {
        var text = new string('a', 10_000);
        var result = _sut.Classify(text, TargetSpeechLanguage.EN);

        Assert.False(result.IsConsent);
    }

    [Fact]
    public void LanguageFallback_EnglishHintWithGermanSentence_StillGrantsAndReportsGerman()
    {
        const string text = "mein Name ist John Doe und ich bin einverstanden, dass dieses Gespräch von Pia aufgezeichnet wird";

        var result = _sut.Classify(text, TargetSpeechLanguage.EN);

        Assert.True(result.IsConsent);
        Assert.Equal("de", result.Language);
    }

    [Fact]
    public void FirstLanguageTried_IsReportedOnANoConsentResult()
    {
        var result = _sut.Classify("hello there", TargetSpeechLanguage.DE);

        Assert.False(result.IsConsent);
        Assert.Equal("de", result.Language);
    }

    [Fact]
    public void Logging_NonSensitiveSummaryLines_NeverContainUtteranceTextOrExtractedName()
    {
        var capturingLogger = new CapturingLogger<NamedConsentClassifier>();
        var sut = new NamedConsentClassifier(capturingLogger);

        const string canaryName = "Zzyzxqvor Wrigglesworth";
        var text = $"my name is {canaryName} and I accept that this meeting gets recorded by Pia";

        var result = sut.Classify(text, TargetSpeechLanguage.EN);
        Assert.True(result.IsConsent);
        Assert.Equal(canaryName, result.ExtractedName);

        var entries = capturingLogger.Entries;
        Assert.True(entries.Count > 0, "non-vacuity: expected at least one log entry");

        // The SensitiveDebug line is the one sanctioned channel for the name, so it is excluded by message text.
        var nonSensitiveEntries = entries.Where(e => !e.Message.Contains("extracted name", StringComparison.Ordinal));

        foreach (var entry in nonSensitiveEntries)
        {
            Assert.DoesNotContain(canaryName, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(text, entry.Message, StringComparison.Ordinal);
        }
    }
}
