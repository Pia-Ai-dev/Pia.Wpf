using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Similarity;

namespace Pia.Services.Consent;

/// <summary>
/// Recognises a speaker-initiated, four-component spoken consent sentence per §3.5 of the
/// direct-transcription design (owner decision D-1): a name introduction with a capturable name, an
/// acceptance verb, a recording reference, and a fuzzy-matched reference to Pia — all four within one
/// utterance, with no negation scoped to the acceptance clause.
///
/// <para>Pure, synchronous, and never throws: <see cref="Classify"/> wraps its whole body in a
/// try/catch and fails closed (returns <see cref="NamedConsentResult.NoConsent"/>) on any exception,
/// because it runs inside the consent forward loop, where a throw would cost the utterance.</para>
/// </summary>
public sealed class NamedConsentClassifier : INamedConsentClassifier
{
    /// <summary>The single grant threshold in the system (owner decision D-1). <see cref="ConsentStateManager"/>
    /// never re-judges confidence — it only ever sees an already-decided <see cref="ConsentEvidence"/>.</summary>
    public const float GrantConfidenceThreshold = 0.85f;

    /// <summary>All four components matched verbatim (no fuzzy repair anywhere).</summary>
    public const float CrispConfidence = 0.95f;

    /// <summary>At least one of the four components was recovered only via a fuzzy (Levenshtein-repaired) match.</summary>
    public const float RepairedConfidence = 0.85f;

    private const int MaxNameTokens = 4;
    private const int NegationWindowBefore = 3;
    private const int NegationWindowAfter = 2;

    private static readonly string[] LanguageOrderEn = { "en", "de", "fr" };
    private static readonly string[] LanguageOrderDe = { "de", "en", "fr" };
    private static readonly string[] LanguageOrderFr = { "fr", "en", "de" };

    private readonly ILogger<NamedConsentClassifier> _logger;

    public NamedConsentClassifier(ILogger<NamedConsentClassifier> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public NamedConsentResult Classify(string utteranceText, TargetSpeechLanguage languageHint)
    {
        var languages = LanguageOrder(languageHint);

        try
        {
            if (string.IsNullOrWhiteSpace(utteranceText))
                return NamedConsentResult.NoConsent(languages[0]);

            foreach (var language in languages)
            {
                var result = ClassifyForLanguage(utteranceText, language);
                if (result.IsConsent)
                {
                    _logger.LogDebug(
                        "NamedConsentClassifier: consent recognised (language={Language} confidence={Confidence})",
                        result.Language, result.Confidence);
                    _logger.SensitiveDebug(
                        "NamedConsentClassifier: extracted name {Name} (language={Language})",
                        result.ExtractedName, result.Language);
                    return result;
                }
            }

            _logger.LogDebug(
                "NamedConsentClassifier: no consent recognised (firstLanguageTried={Language})",
                languages[0]);
            return NamedConsentResult.NoConsent(languages[0]);
        }
        catch (Exception ex)
        {
            // Fail-closed: a classifier bug must never surface as an exception on the forward loop's
            // sole raw-channel consumer. The utterance text itself is never included in this log line.
            _logger.LogWarning(ex, "NamedConsentClassifier threw; treating utterance as non-consent");
            return NamedConsentResult.NoConsent(languages[0]);
        }
    }

    private static string[] LanguageOrder(TargetSpeechLanguage hint) => hint switch
    {
        TargetSpeechLanguage.EN => LanguageOrderEn,
        TargetSpeechLanguage.DE => LanguageOrderDe,
        TargetSpeechLanguage.FR => LanguageOrderFr,
        _ => LanguageOrderEn, // Auto: try en, de, fr in that order (design doc §3.5)
    };

    private static NamedConsentResult ClassifyForLanguage(string utteranceText, string language)
    {
        var tokens = Tokenize(utteranceText);
        if (tokens.Count == 0)
            return NamedConsentResult.NoConsent(language);

        // Component 1: name introduction with a capturable name.
        if (!TryCaptureName(tokens, language, out var nameTokens, out var markerRepaired))
            return NamedConsentResult.NoConsent(language);

        // Component 2: acceptance verb — with the negation guard applied to EVERY occurrence, not just
        // the first one in table order (see TryResolveAcceptance).
        if (!TryResolveAcceptance(tokens, language, out var acceptRepaired))
            return NamedConsentResult.NoConsent(language);

        // Component 3: recording reference.
        if (!TryFindPhrase(tokens, ConsentLexicon.Recording[language], out _, out _, out var recordingRepaired))
            return NamedConsentResult.NoConsent(language);

        // Component 4: a reference to Pia (hard requirement, fuzzy-matched — D-1).
        if (!TryFindPiaReference(tokens, out var piaRepaired))
            return NamedConsentResult.NoConsent(language);

        var anyRepaired = markerRepaired || acceptRepaired || recordingRepaired || piaRepaired;
        var confidence = anyRepaired ? RepairedConfidence : CrispConfidence;
        var extractedName = string.Join(" ", nameTokens.Select(TitleCaseToken));

        return new NamedConsentResult(true, extractedName, language, confidence);
    }

    /// <summary>
    /// Component 1. Enumerates every marker in <see cref="ConsentLexicon.NameMarkers"/> preference order
    /// (crisp matches first, then repaired ones) and keeps the FIRST one that yields a valid name capture.
    ///
    /// <para>Returning on the first marker MATCH instead — as a single left-to-right phrase scan does —
    /// was wrong in two directions: an incidental prose phrase could satisfy the component with a
    /// fabricated "name" ("I accept that <b>this is</b> recorded by Pia" captured "recorded by pia"), and
    /// an incidental earlier phrase could beat a genuine later self-introduction and put the wrong word
    /// into the DPAPI-protected consent evidence.</para>
    /// </summary>
    private static bool TryCaptureName(
        IReadOnlyList<string> tokens, string language, out List<string> nameTokens, out bool repaired)
    {
        var stopTokens = BuildNameStopTokens(language);

        // Two passes so a crisp marker anywhere always beats a repaired one, matching TryFindPhrase.
        for (var pass = 0; pass < 2; pass++)
        {
            var crispOnly = pass == 0;
            foreach (var marker in ConsentLexicon.NameMarkers[language])
            {
                for (var i = 0; i + marker.Phrase.Length <= tokens.Count; i++)
                {
                    if (marker.RequiresUtteranceStart && i != 0)
                        continue;
                    if (!TryMatchSequence(tokens, i, marker.Phrase, out var markerRepaired))
                        continue;
                    if (crispOnly && markerRepaired)
                        continue;

                    // Capture one token beyond the cap so a capture that is actually too long (5+
                    // name-shaped tokens before any stop token) can be told apart from one that
                    // legitimately ends at the cap.
                    var captured = ExtractNameTokens(
                        tokens, i + marker.Phrase.Length, stopTokens, MaxNameTokens + 1);

                    if (captured.Count == 0 || captured.Count > MaxNameTokens)
                        continue;
                    if (captured.Any(t => !IsValidNameToken(t)))
                        continue;

                    // No explicit "is the capture lexicon vocabulary?" check is needed any more: every
                    // such word is a STOP token now, so the capture ends before it. The previous version
                    // only rejected a capture that was ENTIRELY lexicon vocabulary, which a single filler
                    // token defeated — "Recorded By Pia" was accepted as a person's name because "by" was
                    // in no table.
                    nameTokens = captured;
                    repaired = markerRepaired;
                    return true;
                }
            }
        }

        nameTokens = new List<string>();
        repaired = false;
        return false;
    }

    /// <summary>
    /// Every token that terminates (and disqualifies) a name capture: the consent lexicon itself in all
    /// four component categories, plus clause boundaries and grammatical function words.
    /// </summary>
    private static HashSet<string> BuildNameStopTokens(string language)
    {
        var set = new HashSet<string>(ConsentLexicon.FlattenedAcceptanceTokens[language], StringComparer.Ordinal);
        set.UnionWith(ConsentLexicon.FlattenedRecordingTokens[language]);
        set.UnionWith(ConsentLexicon.Negation[language]);
        set.UnionWith(ConsentLexicon.Boosters[language]);
        set.UnionWith(ConsentLexicon.PiaAliasSet);
        set.UnionWith(ConsentLexicon.ClauseBoundaryTokens);
        set.UnionWith(ConsentLexicon.FunctionWords);
        set.Add("pia");
        return set;
    }

    /// <summary>
    /// Component 2 plus the negation guard, evaluated over EVERY acceptance occurrence in the utterance.
    /// Grants only when at least one occurrence is un-negated AND no occurrence is negated — negation
    /// wins unconditionally (owner decision D-1), so a sentence that accepts one thing while refusing the
    /// recording can never be read as consent.
    ///
    /// <para>Checking only the first occurrence in lexicon-table order was the bug: "I accept the
    /// invitation, but I do not agree to be recorded by Pia" pinned the guard to <c>accept</c> (belonging
    /// to "the invitation") and never looked at the negated <c>agree</c>.</para>
    /// </summary>
    /// <param name="repaired">
    /// True when every un-negated occurrence needed a fuzzy repair; false as soon as one un-negated
    /// occurrence matched verbatim, so a crisp sentence is never downgraded by an incidental fuzzy hit.
    /// </param>
    private static bool TryResolveAcceptance(IReadOnlyList<string> tokens, string language, out bool repaired)
    {
        repaired = false;

        var occurrences = FindAllPhraseOccurrences(tokens, ConsentLexicon.Acceptance[language]);
        if (occurrences.Count == 0)
            return false;

        var haveUnnegated = false;
        var allUnnegatedWereRepaired = true;

        foreach (var (start, end, occurrenceRepaired) in occurrences)
        {
            if (IsNegated(tokens, language, start, end))
                return false; // negation anywhere on any acceptance occurrence: fail closed.

            haveUnnegated = true;
            if (!occurrenceRepaired)
                allUnnegatedWereRepaired = false;
        }

        repaired = allUnnegatedWereRepaired;
        return haveUnnegated;
    }

    private static List<(int Start, int End, bool Repaired)> FindAllPhraseOccurrences(
        IReadOnlyList<string> tokens, IReadOnlyList<string[]> phrases)
    {
        var found = new List<(int Start, int End, bool Repaired)>();
        foreach (var phrase in phrases)
        {
            for (var i = 0; i + phrase.Length <= tokens.Count; i++)
            {
                if (TryMatchSequence(tokens, i, phrase, out var sequenceRepaired))
                    found.Add((i, i + phrase.Length, sequenceRepaired));
            }
        }
        return found;
    }

    private static List<string> ExtractNameTokens(IReadOnlyList<string> tokens, int startIndex, HashSet<string> stopTokens, int maxTokens)
    {
        var captured = new List<string>();
        for (var i = startIndex; i < tokens.Count && captured.Count < maxTokens; i++)
        {
            var token = tokens[i];
            if (stopTokens.Contains(token))
                break;

            captured.Add(token);
        }

        return captured;
    }

    private static bool IsValidNameToken(string token)
    {
        if (token.Length < 2)
            return false;

        foreach (var ch in token)
        {
            if (!char.IsLetter(ch) && ch != '-' && ch != '\'')
                return false;
        }

        return true;
    }

    private static string TitleCaseToken(string token)
    {
        var sb = new StringBuilder(token.Length);
        var capitalizeNext = true;
        foreach (var ch in token)
        {
            if (ch == '-' || ch == '\'')
            {
                sb.Append(ch);
                capitalizeNext = true;
                continue;
            }

            sb.Append(capitalizeNext ? char.ToUpperInvariant(ch) : ch);
            capitalizeNext = false;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Finds a contiguous match of any phrase in <paramref name="phrasesLongestFirst"/> anywhere in
    /// <paramref name="tokens"/>. A phrase token of length &gt;= 5 may be satisfied by a
    /// Levenshtein-≤1 repair instead of an exact match (§3.5's fuzzy-tolerance rule); shorter tokens
    /// must match exactly.
    ///
    /// <para>Runs two passes rather than returning on the first hit in list order: pass 1 accepts only
    /// a fully crisp match anywhere in the utterance, pass 2 falls back to a repaired match. Without
    /// this, a verbatim sentence could be scored as "repaired" purely because some fuzzy-tolerant
    /// lexicon entry earlier in the list happened to fuzzy-match a token that a later, more specific
    /// entry would have matched exactly (e.g. FR "enregistre" fuzzy-matching the token "enregistrée"
    /// before the list reaches the exact "enregistrée" entry) — an artifact of table order, not of the
    /// utterance, and one that must never downgrade a crisp sentence's confidence.</para>
    /// </summary>
    private static bool TryFindPhrase(
        IReadOnlyList<string> tokens,
        IReadOnlyList<string[]> phrasesLongestFirst,
        out int start,
        out int end,
        out bool repaired)
    {
        if (TryFindPhraseCore(tokens, phrasesLongestFirst, crispOnly: true, out start, out end, out repaired))
            return true;

        return TryFindPhraseCore(tokens, phrasesLongestFirst, crispOnly: false, out start, out end, out repaired);
    }

    private static bool TryFindPhraseCore(
        IReadOnlyList<string> tokens,
        IReadOnlyList<string[]> phrasesLongestFirst,
        bool crispOnly,
        out int start,
        out int end,
        out bool repaired)
    {
        foreach (var phrase in phrasesLongestFirst)
        {
            for (var i = 0; i + phrase.Length <= tokens.Count; i++)
            {
                if (!TryMatchSequence(tokens, i, phrase, out var sequenceRepaired))
                    continue;

                if (crispOnly && sequenceRepaired)
                    continue;

                start = i;
                end = i + phrase.Length;
                repaired = sequenceRepaired;
                return true;
            }
        }

        start = -1;
        end = -1;
        repaired = false;
        return false;
    }

    private static bool TryMatchSequence(IReadOnlyList<string> tokens, int start, string[] phrase, out bool repaired)
    {
        repaired = false;
        for (var k = 0; k < phrase.Length; k++)
        {
            if (!TokenMatches(tokens[start + k], phrase[k], out var tokenRepaired))
                return false;

            repaired |= tokenRepaired;
        }

        return true;
    }

    private static bool TokenMatches(string token, string lexiconWord, out bool repaired)
    {
        repaired = false;

        if (token == lexiconWord)
            return true;

        // Fuzzy repair only for lexicon words >= 5 chars (§3.5) — short function words like "is"/"et"
        // would admit far too many unrelated tokens under a Levenshtein-≤1 rule. Two further exclusions,
        // both required: a curated false-friend blocklist (a 1-edit neighbour of a lexicon word is quite
        // often another real word with an unrelated meaning — "content" vs "consent"), and any token
        // carrying an elided French negation, which is a refusal one edit from "j'accepte".
        if (lexiconWord.Length >= 5
            && !ConsentLexicon.FuzzyFalseFriends.Contains(token)
            && !ConsentLexicon.IsElidedNegation(token)
            && Levenshtein.WithinOne(token, lexiconWord))
        {
            repaired = true;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Two passes for the same reason as <see cref="TryFindPhrase"/>: the exact token "pia" must win
    /// whenever it appears anywhere in the utterance, even if an alias or a fuzzy (rule-b) hit for some
    /// OTHER token would otherwise have been found first by a single left-to-right scan.
    /// </summary>
    private static bool TryFindPiaReference(IReadOnlyList<string> tokens, out bool repaired)
    {
        if (tokens.Any(t => t == "pia"))
        {
            repaired = false;
            return true;
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (ConsentLexicon.PiaAliasSet.Contains(token))
            {
                repaired = true;
                return true;
            }

            if (token.Length >= 4
                && !ConsentLexicon.PiaFalseFriends.Contains(token)
                && Levenshtein.Distance(token, "pia") <= 1)
            {
                repaired = true;
                return true;
            }
        }

        // Letter-spelled fallback: three consecutive single-letter tokens "p", "i", "a".
        var letters = ConsentLexicon.PiaLetterSequence;
        for (var i = 0; i + letters.Length <= tokens.Count; i++)
        {
            var allMatch = true;
            for (var k = 0; k < letters.Length; k++)
            {
                if (tokens[i + k] != letters[k])
                {
                    allMatch = false;
                    break;
                }
            }

            if (allMatch)
            {
                repaired = true;
                return true;
            }
        }

        repaired = false;
        return false;
    }

    /// <summary>
    /// True when the acceptance occurrence [<paramref name="acceptStart"/>, <paramref name="acceptEnd"/>)
    /// is negated. Two scopes, because one size does not fit:
    ///
    /// <list type="bullet">
    /// <item><b>Strong</b> negations (<c>not</c>, <c>cannot</c>, <c>nicht</c>, <c>ne</c>/<c>pas</c>, …) are
    /// scanned across the WHOLE clause containing the verb — from the previous clause boundary to the next
    /// one (or the utterance's ends). A fixed token window could not work: German's Satzklammer puts
    /// <c>nicht</c> at the clause's end ("…ich akzeptiere die Aufzeichnung durch Pia <b>nicht</b>"), and
    /// English stacks modifiers ("I do <b>not</b> really want to accept …").</item>
    /// <item><b>Weak</b> negations (<c>no</c>, <c>kein</c>, <c>non</c>) are also ordinary determiners, so
    /// they only count inside the narrow fixed window immediately around the verb. This is what keeps
    /// "…by the way no problem at all today I accept that this is recorded by Pia" a grant.</item>
    /// </list>
    ///
    /// <para>Both tiers are matched with the same length-gated fuzzy repair the grant components get, so a
    /// mangled "nich" still refuses while a mangled "acept" still accepts. An elided French negation
    /// ("n'accepte") also counts, since the apostrophe leaves no standalone "ne" token to find.</para>
    /// </summary>
    private static bool IsNegated(IReadOnlyList<string> tokens, string language, int acceptStart, int acceptEnd)
    {
        var strong = ConsentLexicon.StrongNegation[language];
        var weak = ConsentLexicon.WeakNegation[language];

        var (clauseStart, clauseEnd) = ClauseBounds(tokens, acceptStart, acceptEnd);
        for (var i = clauseStart; i < clauseEnd; i++)
        {
            if (i >= acceptStart && i < acceptEnd)
                continue; // the verb itself is never its own negation
            if (MatchesAnyLexiconToken(tokens[i], strong) || ConsentLexicon.IsElidedNegation(tokens[i]))
                return true;
        }

        var windowStart = Math.Max(0, acceptStart - NegationWindowBefore);
        for (var i = windowStart; i < acceptStart; i++)
        {
            if (MatchesAnyLexiconToken(tokens[i], weak))
                return true;
        }

        var windowEnd = Math.Min(tokens.Count, acceptEnd + NegationWindowAfter);
        for (var i = acceptEnd; i < windowEnd; i++)
        {
            if (MatchesAnyLexiconToken(tokens[i], weak))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The clause containing [<paramref name="acceptStart"/>, <paramref name="acceptEnd"/>): from just
    /// after the nearest preceding <see cref="ConsentLexicon.ClauseBoundaryTokens"/> token (or 0) up to
    /// the nearest following one (or the end of the utterance).
    /// </summary>
    private static (int Start, int End) ClauseBounds(IReadOnlyList<string> tokens, int acceptStart, int acceptEnd)
    {
        var start = 0;
        for (var i = acceptStart - 1; i >= 0; i--)
        {
            if (ConsentLexicon.ClauseBoundaryTokens.Contains(tokens[i]))
            {
                start = i + 1;
                break;
            }
        }

        var end = tokens.Count;
        for (var i = acceptEnd; i < tokens.Count; i++)
        {
            if (ConsentLexicon.ClauseBoundaryTokens.Contains(tokens[i]))
            {
                end = i;
                break;
            }
        }

        return (start, end);
    }

    /// <summary>
    /// Exact-or-fuzzy membership test against a single-token lexicon set, using the same
    /// <see cref="TokenMatches"/> rules (and therefore the same &gt;=5-character length gate and the same
    /// false-friend blocklist) the grant components use.
    /// </summary>
    private static bool MatchesAnyLexiconToken(string token, HashSet<string> lexiconTokens)
    {
        if (lexiconTokens.Contains(token))
            return true;

        foreach (var lexiconWord in lexiconTokens)
        {
            if (TokenMatches(token, lexiconWord, out _))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Lowercases and tokenizes <paramref name="text"/>. This is the ported rule classifier's
    /// <c>Normalize</c> (lowercase; keep letters/digits/whitespace/apostrophe; replace every other
    /// character with a space so punctuation still forms a token boundary), extended in exactly one
    /// way: a hyphen between two word characters is kept as part of the token instead of becoming a
    /// space, so a hyphenated name ("Anne-Marie") survives as one token instead of being split into
    /// two. No lexicon word in this file contains a hyphen, so this extension cannot change matching
    /// for markers/acceptance/recording/negation/Pia — it only changes how name tokens are captured,
    /// which the old classifier never needed to do.
    ///
    /// <para>Typographic apostrophes (U+2019 RIGHT SINGLE QUOTATION MARK and friends) are folded to the
    /// ASCII <c>'</c> first. Without that fold, the French sentence the disclaimer instructs participants
    /// to say ("Je m’appelle …, j’accepte …", which the resx stores with U+2019) tokenized as
    /// <c>je | m | appelle</c> and could never match the <c>je m'appelle</c> marker.</para>
    /// </summary>
    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text))
            return tokens;

        var lower = text.ToLowerInvariant();
        var current = new StringBuilder();

        for (var i = 0; i < lower.Length; i++)
        {
            var ch = FoldApostrophe(lower[i]);
            var isWordChar = char.IsLetterOrDigit(ch) || ch == '\'';
            var isInteriorHyphen = ch == '-'
                && current.Length > 0
                && i + 1 < lower.Length
                && (char.IsLetterOrDigit(lower[i + 1]) || FoldApostrophe(lower[i + 1]) == '\'');

            if (isWordChar || isInteriorHyphen)
            {
                current.Append(ch);
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens;
    }

    /// <summary>Maps every Unicode apostrophe/quote variant STT and typography produce onto ASCII <c>'</c>,
    /// so one spelling of "m'appelle" is enough in the lexicon.</summary>
    private static char FoldApostrophe(char ch) => ch switch
    {
        '‘' or '’' or 'ʼ' or 'ʹ' or '′' or '´' or '`' => '\'',
        _ => ch,
    };
}
