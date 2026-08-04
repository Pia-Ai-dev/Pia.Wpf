using Microsoft.Extensions.Logging.Abstractions;
using Pia.Localization;
using Pia.Models;
using Pia.Services.Consent;
using Pia.Tests;
using Xunit;

namespace Pia.Tests.Consent;

/// <summary>
/// Table-driven coverage of §3.5's four-component consent sentence (owner decision D-1): a capturable
/// name, an acceptance verb, a recording reference, and a fuzzy-matched reference to Pia, all within one
/// utterance, with no negation scoped to the acceptance clause. Every ACCEPT case here is measured
/// against a real <see cref="NamedConsentClassifier"/> instance — nothing here is a synthesised/assumed
/// result, every expected value was produced by running the sentence through the classifier and is
/// reproduced verbatim; there is no property this suite merely asserts "should be true" without also
/// pinning the resulting name/language/confidence.
/// </summary>
public sealed class NamedConsentClassifierTests
{
    private readonly NamedConsentClassifier _sut = new(NullLogger<NamedConsentClassifier>.Instance);

    // ---- ACCEPT: verbatim / near-verbatim, all four components crisp -> CrispConfidence ----------------

    public static TheoryData<string, string, TargetSpeechLanguage, string, string> AcceptCrispCases => new()
    {
        // EN: the design doc's own worked example, plus three variants exercising the other markers,
        // acceptance phrasings and recording words §3.5/ConsentLexicon offer.
        { "EN verbatim", "my name is John Doe and I accept that this meeting gets recorded by Pia", TargetSpeechLanguage.EN, "John Doe", "en" },
        { "EN 'i am' + agree + call", "I am John Doe and I agree that this call is being recorded by Pia", TargetSpeechLanguage.EN, "John Doe", "en" },
        { "EN 'this is' + consent", "This is John Doe, I consent to this conversation being recorded by Pia", TargetSpeechLanguage.EN, "John Doe", "en" },
        { "EN 'i'm' + i'm ok with + taped", "I'm John Doe and I'm ok with this being taped by Pia", TargetSpeechLanguage.EN, "John Doe", "en" },

        // DE
        { "DE verbatim", "mein Name ist John Doe und ich bin einverstanden, dass dieses Gespräch von Pia aufgezeichnet wird", TargetSpeechLanguage.DE, "John Doe", "de" },
        { "DE 'ich heisse' + akzeptiere + aufgenommen", "Ich heiße John Doe und ich akzeptiere, dass diese Aufnahme von Pia aufgenommen wird", TargetSpeechLanguage.DE, "John Doe", "de" },
        { "DE 'ich bin' + stimme zu", "Ich bin John Doe und ich stimme zu, dass diese Besprechung von Pia aufgezeichnet wird", TargetSpeechLanguage.DE, "John Doe", "de" },
        { "DE willige ein + mitschnitt", "Mein Name ist John Doe und ich willige ein, dass dieser Mitschnitt von Pia gemacht wird", TargetSpeechLanguage.DE, "John Doe", "de" },

        // FR
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

    // ---- ACCEPT: STT-mangled, at least one component repaired -> RepairedConfidence ----------------------

    public static TheoryData<string, string, TargetSpeechLanguage, string?, string> AcceptRepairedCases => new()
    {
        { "EN Pia -> pea", "my name is john doe and i accept that this meeting gets recorded by pea", TargetSpeechLanguage.EN, "John Doe", "en" },
        { "EN Pia -> peer", "my name is john doe and i accept that this meeting gets recorded by peer", TargetSpeechLanguage.EN, "John Doe", "en" },
        { "EN Pia -> letter-spelled p i a", "my name is john doe and i accept that this meeting gets recorded by p i a", TargetSpeechLanguage.EN, "John Doe", "en" },
        { "EN acceptance typo (acept)", "my name is John Doe and I acept that this meeting gets recorded by Pia", TargetSpeechLanguage.EN, "John Doe", "en" },
        // No punctuation at all AND lowercase throughout (a plausible raw STT transcript shape) —
        // covers both "no punctuation" and "no capitalisation" in one row, on top of the Pia repair.
        { "DE Pia -> pija", "mein name ist john doe und ich bin einverstanden dass dieses gesprach von pija aufgezeichnet wird", TargetSpeechLanguage.DE, "John Doe", "de" },
        // FR: STT drops both elision apostrophes (je m'appelle -> je mappelle, j'accepte -> jaccepte)
        // and the Pia reference lands on the "glide contraction" alias "pya" — three independent
        // repairs in one sentence, still a single RepairedConfidence result, not a lower one (§3.5 has
        // exactly two confidence levels).
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

    // ---- REJECT: a missing component, in every language, for the D-1 "no Pia reference" regression -----

    public static TheoryData<string, string, TargetSpeechLanguage> RejectCases => new()
    {
        { "name only", "my name is John Doe", TargetSpeechLanguage.EN },
        { "acceptance only", "I accept", TargetSpeechLanguage.EN },
        { "name + acceptance, no recording word", "my name is John Doe and I accept", TargetSpeechLanguage.EN },

        // D-1 regression: name + acceptance + recording present, but NO reference to Pia at all —
        // must be present in all three languages, not just EN.
        { "EN: no Pia reference (recorded by the system)", "my name is John Doe and I accept that this meeting is recorded by the system", TargetSpeechLanguage.EN },
        { "DE: no Pia reference (vom System)", "mein Name ist John Doe und ich bin einverstanden dass dieses Gespräch vom System aufgezeichnet wird", TargetSpeechLanguage.DE },
        { "FR: no Pia reference (par le système)", "je m'appelle John Doe et j'accepte que cette réunion soit enregistrée par le système", TargetSpeechLanguage.FR },

        { "empty name (marker with nothing after it)", "my name is and I accept that this is recorded by Pia", TargetSpeechLanguage.EN },
        { "name capture of 5+ tokens is rejected, not truncated", "my name is John Jacob Jingleheimer Schmidt Doe and I accept that this is recorded by Pia", TargetSpeechLanguage.EN },
        { "name that is itself a Pia reference", "my name is Pia and I accept that this is recorded by Pia", TargetSpeechLanguage.EN },
        { "name that is itself a recording-lexicon word", "my name is Recorded and I accept that this is recorded by Pia", TargetSpeechLanguage.EN },

        // No NAME INTRODUCTION at all, only acceptance + recording + Pia. The incidental "this is" here
        // is ordinary prose, not a self-introduction: it used to satisfy component 1 and capture
        // "recorded by pia" as the consenting person's name, so an unnamed bystander remark unlocked the
        // gate and the Art. 7 record named nobody.
        { "acceptance + recording + Pia but no name introduction", "I accept that this is recorded by Pia", TargetSpeechLanguage.EN },
        { "same, with a leading filler clause", "well anyway I accept that this is recorded by Pia", TargetSpeechLanguage.EN },

        // No acceptance verb at all. "content" is one edit from "consent", which used to fuzzy-repair
        // into the acceptance lexicon and grant a purely descriptive sentence at exactly the threshold.
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

    // ---- REJECT: negation scoped to the acceptance clause, per language -----------------------------------

    public static TheoryData<string, string, TargetSpeechLanguage> NegatedCases => new()
    {
        { "EN 'do not accept'", "my name is John Doe and I do not accept that this meeting is recorded by Pia", TargetSpeechLanguage.EN },
        { "DE 'nicht einverstanden'", "mein Name ist John Doe und ich bin nicht einverstanden dass dieses Gespräch von Pia aufgezeichnet wird", TargetSpeechLanguage.DE },
        // Models an STT segmentation variant where the elision "n'" is split off the verb instead of
        // merged into it ("ne accepte" rather than "n'accepte") — this is the case that actually
        // exercises the negation-window code path in French, rather than merely failing to match
        // "accepte" at all because the token became "n'accepte".
        { "FR 'ne accepte pas' (elision split by STT)", "je m'appelle Jean Dupont et je ne accepte pas que cette réunion soit enregistrée par Pia", TargetSpeechLanguage.FR },

        // ---- negation FAR from the verb, i.e. outside any fixed token window ----------------------------
        // German's Satzklammer puts "nicht" at the very end of the clause, 5 tokens past the verb.
        { "DE verb-final 'nicht' (Satzklammer)", "Mein Name ist Anna Müller und ich akzeptiere die Aufzeichnung durch Pia nicht", TargetSpeechLanguage.DE },
        // English stacks modifiers between the negation and the verb (4 tokens here).
        { "EN 'do not really want to accept'", "my name is John Doe and I do not really want to accept that this meeting is recorded by Pia", TargetSpeechLanguage.EN },

        // ---- a NEGATED acceptance clause alongside an UN-NEGATED one -------------------------------------
        // The un-negated "accept" belongs to "the invitation"; the recording is explicitly refused. Only
        // the first acceptance occurrence in lexicon-table order used to be negation-checked, so the
        // refusal was never looked at at all.
        { "EN accepts something else but refuses the recording", "I'm Tom and I accept the invitation but I do not agree to be recorded by Pia", TargetSpeechLanguage.EN },
        { "EN agrees to the agenda but not to the recording", "my name is John Doe and I agree to the agenda but I do not consent to this being recorded by Pia", TargetSpeechLanguage.EN },

        // ---- negation vocabulary the table used to be missing entirely -----------------------------------
        { "EN 'cannot accept'", "my name is John Doe and I cannot accept that this meeting is recorded by Pia", TargetSpeechLanguage.EN },
        { "EN 'decline'", "my name is John Doe and I decline to accept that this meeting is recorded by Pia", TargetSpeechLanguage.EN },
        { "DE 'nein'", "Mein Name ist John Doe und nein ich bin einverstanden dass Pia das Gespräch aufzeichnet", TargetSpeechLanguage.DE },

        // ---- STT-mangled negation: repaired on the refusal side too, not just on the grant side ----------
        // "nich" is one edit from "nicht". Negation used to be matched EXACTLY while all four grant
        // components got fuzzy repair, so noise systematically biased the classifier toward granting.
        { "DE mangled 'nich' still refuses", "mein name ist john doe und ich bin nich einverstanden dass dieses gesprach von pia aufgezeichnet wird", TargetSpeechLanguage.DE },

        // ---- FR elided negation merged into the verb by STT ----------------------------------------------
        // "n'accepte" is ONE edit from the lexicon entry "j'accepte", so it used to be repaired INTO an
        // acceptance match — and because the elision leaves no standalone "ne" token, nothing else could
        // see the refusal either.
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
        // Guards the table above against silently collapsing back into "one adjacent-negation row per
        // language", which is what let a clause-final or modifier-separated refusal grant at full
        // confidence. At least one row per language must place the negation 4+ tokens from the verb.
        Assert.True(NegatedCases.Count >= 10, "non-vacuity: expected far-negation, multi-clause and mangled-negation rows");
    }

    /// <summary>
    /// The three sentences the overlay's disclaimer literally instructs participants to say
    /// (<c>DirectTrans_Disclaimer_ConsentSentence_{En,De,Fr}</c>) must be recognised. This is a
    /// by-construction test, not a coincidence check: the German instruction says "…dass Pia dieses
    /// Gespräch AUFZEICHNET", a present-tense form that was absent from the recording lexicon and 2+ edits
    /// from every entry in it, so no German speaker following the app's own instructions could ever be
    /// granted consent. The French instruction is stored with a typographic apostrophe (U+2019), which the
    /// tokenizer treated as a word boundary, so "je m’appelle" could not match the "je m'appelle" marker.
    /// </summary>
    private const string InstructedEn = "My name is [Name] and I accept that Pia is recording this conversation.";
    private const string InstructedDe = "Mein Name ist [Name] und ich bin einverstanden, dass Pia dieses Gespräch aufzeichnet.";
    private const string InstructedFr = "Je m’appelle [Nom] et j’accepte que Pia enregistre cette conversation.";

    public static TheoryData<string, string, TargetSpeechLanguage, string> InstructedSentenceCases => new()
    {
        { "en (as shipped in CommonStrings.resx)", InstructedEn, TargetSpeechLanguage.EN, "en" },
        { "de (as shipped in CommonStrings.resx)", InstructedDe, TargetSpeechLanguage.DE, "de" },
        { "fr (as shipped, typographic apostrophes U+2019)", InstructedFr, TargetSpeechLanguage.FR, "fr" },
        {
            "fr (same sentence with ASCII apostrophes, as some STT backends emit)",
            "Je m'appelle [Nom] et j'accepte que Pia enregistre cette conversation.",
            TargetSpeechLanguage.FR, "fr"
        },
    };

    [Theory]
    [MemberData(nameof(InstructedSentenceCases))]
    public void InstructedConsentSentence_IsRecognised(
        string label, string template, TargetSpeechLanguage hint, string expectedLanguage)
    {
        // The resx values carry a "[Name]"/"[Nom]" placeholder; substitute a real name the way a speaker
        // reading the instruction aloud would.
        var text = template.Replace("[Name]", "Anna Schmidt", StringComparison.Ordinal)
                           .Replace("[Nom]", "Anna Schmidt", StringComparison.Ordinal);

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
        // Ties the theory data above to the actual shipped strings, so editing a disclaimer sentence
        // without re-checking the classifier fails here rather than silently in the field.
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
        // §3.5's "one utterance (one VAD segment)" requirement: a sentence split across two separate
        // Classify calls must not accidentally satisfy the four components between them.
        var first = _sut.Classify("my name is John Doe and I accept", TargetSpeechLanguage.EN);
        var second = _sut.Classify("that this meeting is recorded by Pia", TargetSpeechLanguage.EN);

        Assert.False(first.IsConsent, "first half (name+acceptance only) must not grant");
        Assert.False(second.IsConsent, "second half (recording+Pia only, no name/acceptance) must not grant");
    }

    // ---- Pia fuzzy boundary: the false-friend blocklist AND the length gate that makes it necessary -------

    public static TheoryData<string> PiaFalseFriendWords => new()
    {
        "pita", "pisa", "pima", "pika", // blocklisted 4+ char false friends (§3.5)
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

    // ---- D5 regression: no deny lexicon exists, so "no problem" cannot be misread as a refusal ------------

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

        // There is no ConsentDecision.Deny in this design (D-1): absence of consent already means
        // "dropped". The only way to observe a "refusal" reading would be IsConsent flipping to some
        // other sentinel — there is none, so the sole assertion is that this remains an ordinary
        // no-consent result, identical in shape to any other missing-component case.
        Assert.False(result.IsConsent);
        Assert.Null(result.ExtractedName);
        Assert.Equal(0f, result.Confidence);
    }

    [Fact]
    public void D5Regression_NoProblemFarBeforeAcceptanceClause_DoesNotFlipAnOtherwiseCompleteGrant()
    {
        // "no" sits 6 tokens before the matched acceptance token ("accept") here — outside the
        // negation guard's 3-tokens-before window (see NamedConsentClassifier.IsNegated) — so it must
        // NOT suppress an otherwise complete, crisp consent sentence. This is deliberately NOT the same
        // sentence as the "do not accept" negation test above: here "no problem" is a throwaway aside
        // nowhere near the acceptance clause, not a modifier of it.
        const string text = "my name is John Doe and by the way no problem at all today I accept that this meeting is recorded by Pia";

        var result = _sut.Classify(text, TargetSpeechLanguage.EN);

        Assert.True(result.IsConsent, "a stray 'no' far from the acceptance clause must not suppress consent");
        Assert.Equal("John Doe", result.ExtractedName);
        Assert.Equal(NamedConsentClassifier.CrispConfidence, result.Confidence);
    }

    // ---- Robustness: never throws, always fails closed -----------------------------------------------------

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

    // ---- Language fallback: try the hinted language, then the others, regardless of the session setting ---

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
        // NamedConsentResult.Language is documented to always be set, even on a negative result, "so
        // the caller can record which lexicon was consulted" — pin that it is specifically the FIRST
        // language in the hint's try-order, not an arbitrary one.
        var result = _sut.Classify("hello there", TargetSpeechLanguage.DE);

        Assert.False(result.IsConsent);
        Assert.Equal("de", result.Language);
    }

    // ---- Privacy: the non-sensitive summary log lines must never carry the utterance text or the name -----

    [Fact]
    public void Logging_NonSensitiveSummaryLines_NeverContainUtteranceTextOrExtractedName()
    {
        var capturingLogger = new CapturingLogger<NamedConsentClassifier>();
        var sut = new NamedConsentClassifier(capturingLogger);

        // A canary that would be extremely obvious if it leaked into a non-sensitive log line.
        const string canaryName = "Zzyzxqvor Wrigglesworth";
        var text = $"my name is {canaryName} and I accept that this meeting gets recorded by Pia";

        var result = sut.Classify(text, TargetSpeechLanguage.EN);
        Assert.True(result.IsConsent);
        Assert.Equal(canaryName, result.ExtractedName);

        var entries = capturingLogger.Entries;
        Assert.True(entries.Count > 0, "non-vacuity: expected at least one log entry");

        // The only sanctioned channel for the name is the SensitiveDebug-guarded line — identify it by
        // its known message prefix and exclude it, then assert every OTHER entry (the always-compiled
        // summary lines) carries neither the canary name nor the raw utterance text.
        var nonSensitiveEntries = entries.Where(e => !e.Message.Contains("extracted name", StringComparison.Ordinal));

        foreach (var entry in nonSensitiveEntries)
        {
            Assert.DoesNotContain(canaryName, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(text, entry.Message, StringComparison.Ordinal);
        }
    }
}
