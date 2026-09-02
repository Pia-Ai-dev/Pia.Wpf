namespace Pia.Services.Consent;

/// <summary>
/// Trilingual keyword tables for <see cref="NamedConsentClassifier"/> — every table in one place so a
/// reviewer can audit the whole consent lexicon without hunting across files. All entries are already
/// lowercase because the classifier only ever compares them against tokens produced by its own
/// tokenizer, which lowercases the utterance before anything else runs.
///
/// <para>Phrase entries (e.g. <c>{"my","name","is"}</c>) are pre-split into the exact token sequence
/// the classifier expects to see contiguously; lists that mix single- and multi-token phrases are
/// ordered longest-first, per §3.5's "scan for the longest marker" rule, so a longer, more specific
/// phrase is preferred over a shorter one that happens to be a prefix of it.</para>
///
/// <para><b>Pia reference.</b> §3.5 (owner decision D-1) makes a reference to Pia a hard requirement of
/// consent, satisfied by a fuzzy match: exact <c>"pia"</c> is crisp; every other alias, the letter-spelled
/// <c>p i a</c> sequence, and the generic Levenshtein-≤1 rule are all treated as repaired. The alias set
/// below is deliberately curated per language rather than left to the generic ≤1 edit-distance rule,
/// because that generic rule is length-gated at ≥4 characters (see <see cref="PiaFalseFriends"/>) and so
/// cannot reach 3-letter STT artifacts like "pea" or "bia" on its own.</para>
/// </summary>
internal static class ConsentLexicon
{
    /// <summary>
    /// One name-introduction marker. <see cref="RequiresUtteranceStart"/> marks a phrase that is only a
    /// self-introduction when it OPENS the utterance: "this is" is ordinary English prose everywhere
    /// else, and allowing it mid-sentence let "I accept that <b>this is</b> recorded by Pia" — an
    /// utterance with no name introduction at all — capture "recorded by pia" as a person's name.
    /// </summary>
    /// <param name="Phrase">The exact contiguous token sequence to match.</param>
    /// <param name="RequiresUtteranceStart">When true, only a match at token index 0 counts.</param>
    public sealed record NameMarker(string[] Phrase, bool RequiresUtteranceStart = false);

    // ---- Name introduction markers -----------------------------------------------------------------
    // Order is a PREFERENCE order, not just a longest-first order: the classifier tries every marker in
    // this order and keeps the first one that yields a valid name capture, so the most specific, most
    // unambiguous self-introduction must come first and the weakest ("this is") must come last.

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<NameMarker>> NameMarkers =
        new Dictionary<string, IReadOnlyList<NameMarker>>
        {
            ["en"] = new[]
            {
                new NameMarker(new[] { "my", "name", "is" }), // canonical EN self-introduction
                new NameMarker(new[] { "i", "am" }),          // "I am John Doe"
                new NameMarker(new[] { "i'm" }),              // contracted "I'm John Doe" — apostrophe survives tokenization
                // Weakest marker, and the only one that is ordinary prose rather than an introduction:
                // accepted only as the utterance's opening words ("This is John Doe, I consent …").
                new NameMarker(new[] { "this", "is" }, RequiresUtteranceStart: true),
            },
            ["de"] = new[]
            {
                new NameMarker(new[] { "mein", "name", "ist" }), // canonical DE self-introduction
                new NameMarker(new[] { "ich", "heisse" }),       // ASCII-folded STT rendering of "heiße"
                new NameMarker(new[] { "ich", "heiße" }),        // literal spelling with ß (a Unicode letter, kept intact)
                new NameMarker(new[] { "ich", "heise" }),        // single-s STT slip that drops one doubled consonant
                new NameMarker(new[] { "ich", "bin" }),          // "ich bin John Doe" — shorter DE variant
            },
            ["fr"] = new[]
            {
                new NameMarker(new[] { "mon", "nom", "est" }), // canonical FR self-introduction
                new NameMarker(new[] { "je", "m'appelle" }),   // contracted "je m'appelle" — apostrophe survives tokenization
                new NameMarker(new[] { "je", "suis" }),        // "je suis Jean Dupont" — shorter FR variant
            },
        };

    // ---- Acceptance verb, longest phrase first -------------------------------------------------------

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string[]>> Acceptance =
        new Dictionary<string, IReadOnlyList<string[]>>
        {
            ["en"] = new[]
            {
                new[] { "i", "am", "okay", "with" }, // "I am okay with" — longest EN acceptance phrase
                new[] { "i'm", "ok", "with" },       // "I'm ok with"
                new[] { "accept" },
                new[] { "accepts" },
                new[] { "accepted" },
                new[] { "agree" },
                new[] { "agreed" },
                new[] { "consent" },
                new[] { "consents" },
            },
            ["de"] = new[]
            {
                new[] { "bin", "einverstanden" }, // "bin einverstanden" — most common spoken form
                new[] { "stimme", "zu" },
                new[] { "willige", "ein" },
                new[] { "einverstanden" },
                new[] { "akzeptiere" },
            },
            ["fr"] = new[]
            {
                new[] { "suis", "d'accord" }, // "suis d'accord"
                new[] { "accepte" },
                new[] { "j'accepte" },        // apostrophe survives tokenization as one token
                new[] { "consens" },
                new[] { "d'accord" },
            },
        };

    // ---- Recording reference, single tokens only ------------------------------------------------------

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string[]>> Recording =
        new Dictionary<string, IReadOnlyList<string[]>>
        {
            ["en"] = new[]
            {
                new[] { "recorded" },
                new[] { "recording" },
                new[] { "record" },
                new[] { "records" },
                new[] { "taped" },
            },
            ["de"] = new[]
            {
                new[] { "aufgezeichnet" },
                new[] { "aufgenommen" },
                new[] { "aufzeichnung" },
                new[] { "aufnahme" },
                new[] { "mitschnitt" },
                // Present-tense/infinitive forms. Kept even though the instructed sentence now uses the
                // noun "Aufzeichnung": "…dass Pia dieses Gespräch AUFZEICHNET" was what the disclaimer
                // asked for until 2026-09, it is 2 edits from "aufgezeichnet" and 3 from "aufzeichnung"
                // so no fuzzy repair reaches it, and anyone who learned that wording still has to get in.
                new[] { "aufzeichnet" },
                new[] { "aufzeichnen" },
                new[] { "aufzeichne" },
                new[] { "aufnimmt" },
                new[] { "aufnehmen" },
                new[] { "mitschneidet" },
            },
            ["fr"] = new[]
            {
                // Accented and ASCII-folded STT variants both included — Normalize/tokenize only
                // lowercases, it does not strip accents, so "enregistrée" and "enregistree" are two
                // distinct tokens that must each be listed.
                new[] { "enregistre" },
                new[] { "enregistree" },
                new[] { "enregistré" },
                new[] { "enregistrée" },
                new[] { "enregistrés" },
                new[] { "enregistrées" },
                new[] { "enregistrement" },
                new[] { "enregistrer" },
            },
        };

    // ---- Negation tokens ------------------------------------------------------------------------------
    // Split into STRONG and WEAK because the two need different scopes (see NamedConsentClassifier.IsNegated):
    //
    //  * STRONG negations unambiguously negate the verb of their clause wherever they sit in it, so they
    //    are matched across the WHOLE clause containing the acceptance verb. German needs this because
    //    the Satzklammer puts "nicht" at the clause's end ("…ich akzeptiere die Aufzeichnung durch Pia
    //    NICHT"), arbitrarily far from the verb; English needs it for "I do not really want to accept …".
    //  * WEAK negations are also ordinary determiners/interjections ("no problem", "kein Problem"), so a
    //    clause-wide scan would misread a throwaway aside as a refusal. They only count immediately
    //    around the acceptance verb (the narrow fixed window).
    //
    // Both sets are matched with the same length-gated Levenshtein-<=1 repair the four GRANT components
    // get, so STT noise cannot silently erase a refusal while still repairing an acceptance verb.

    public static readonly IReadOnlyDictionary<string, HashSet<string>> StrongNegation =
        new Dictionary<string, HashSet<string>>
        {
            ["en"] = new(StringComparer.Ordinal)
            {
                "not", "don't", "dont", "doesn't", "doesnt", "never", "won't", "wont",
                "cannot", "can't", "cant", "refuse", "refuses", "refused", "refusing",
                "decline", "declines", "declined", "disagree", "disagrees", "disagreed",
                "deny", "denies", "denied", "object", "objects", "unwilling", "nope",
            },
            ["de"] = new(StringComparer.Ordinal)
            {
                "nicht", "nein", "nie", "niemals", "keinesfalls",
                "widerspreche", "widersprechen", "widerspruch",
                "verweigere", "verweigern", "ablehne", "ablehnen", "lehne", "untersage",
            },
            ["fr"] = new(StringComparer.Ordinal)
            {
                // "ne"/"pas" are the two halves of French negation; both are strong, so the clause-wide
                // scan catches the pair however far apart the speaker (or the STT) puts them.
                "ne", "pas", "jamais", "refuse", "refusons", "aucun", "aucune",
                "nullement", "interdis", "oppose", "refus",
            },
        };

    public static readonly IReadOnlyDictionary<string, HashSet<string>> WeakNegation =
        new Dictionary<string, HashSet<string>>
        {
            ["en"] = new(StringComparer.Ordinal) { "no" },
            ["de"] = new(StringComparer.Ordinal) { "kein", "keine", "keinen", "keinem" },
            ["fr"] = new(StringComparer.Ordinal) { "non" },
        };

    /// <summary>Union of both negation tiers per language — used for the name-capture reject/stop rule
    /// (a capture like "not John Doe" is not a name), never for the negation decision itself.</summary>
    public static readonly IReadOnlyDictionary<string, HashSet<string>> Negation = BuildNegationUnion();

    private static IReadOnlyDictionary<string, HashSet<string>> BuildNegationUnion()
    {
        var result = new Dictionary<string, HashSet<string>>();
        foreach (var (language, strong) in StrongNegation)
        {
            var all = new HashSet<string>(strong, StringComparer.Ordinal);
            all.UnionWith(WeakNegation[language]);
            result[language] = all;
        }
        return result;
    }

    // ---- Boosters — kept for a v2 LLM assist, MUST NOT affect the v1 decision or confidence -------------

    public static readonly IReadOnlyDictionary<string, string[]> Boosters =
        new Dictionary<string, string[]>
        {
            ["en"] = new[] { "meeting", "conversation", "call" },
            ["de"] = new[] { "gesprach", "gespräch", "besprechung", "meeting" },
            ["fr"] = new[] { "reunion", "réunion", "conversation", "appel" },
        };

    // ---- Conjunctions that terminate a name capture (language-agnostic: STT can drift languages) -------

    public static readonly HashSet<string> ConjunctionTokens = new(StringComparer.Ordinal)
    {
        "and", "und", "et", "that", "dass", "daß", "que",
    };

    /// <summary>
    /// Tokens that end one clause and open the next, used to scope the strong-negation scan. Includes the
    /// conjunctions above plus adversatives, which are load-bearing: without "but"/"aber"/"mais" the
    /// clause containing "I accept the invitation" and the clause containing "I do not agree to be
    /// recorded" would be one scope, and a sentence that accepts one thing while refusing the recording
    /// could not be told apart from one that accepts both.
    /// </summary>
    public static readonly HashSet<string> ClauseBoundaryTokens = BuildClauseBoundaryTokens();

    private static HashSet<string> BuildClauseBoundaryTokens()
    {
        var set = new HashSet<string>(ConjunctionTokens, StringComparer.Ordinal);
        set.UnionWith(new[]
        {
            "but", "however", "though", "although", "or", "because", "so",
            "aber", "jedoch", "doch", "oder", "weil", "sondern",
            "mais", "cependant", "pourtant", "ou", "parce", "car",
        });
        return set;
    }

    /// <summary>
    /// Grammatical function words that can never be part of a person's name. They terminate a name
    /// capture, so "I am John Doe FROM Acme" still captures "John Doe" rather than four tokens, and
    /// "my name is NOT John Doe" captures nothing at all. Without this, a capture only had to avoid
    /// being ENTIRELY lexicon vocabulary, which a single filler token defeated.
    /// </summary>
    public static readonly HashSet<string> FunctionWords = new(StringComparer.Ordinal)
    {
        // en
        "a", "an", "the", "of", "from", "by", "with", "to", "at", "in", "on", "for",
        "is", "are", "was", "were", "be", "being", "been", "this", "these", "those", "it",
        "he", "she", "we", "they", "you", "his", "her", "their", "our", "your", "my",
        "here", "there", "today", "now", "just", "also", "please", "yes", "yeah",
        // de
        "der", "die", "das", "den", "dem", "des", "ein", "eine", "einen", "einem", "einer",
        "von", "durch", "mit", "für", "dieses", "diese", "dieser", "diesem", "ist", "sind",
        "wird", "werden", "hier", "heute", "jetzt", "ja", "auch", "bitte", "mein", "meine",
        // fr
        "le", "la", "les", "un", "une", "des", "de", "du", "par", "avec", "pour",
        "cette", "ce", "ces", "cet", "est", "sont", "ici", "aujourd'hui", "oui", "aussi",
        "mon", "ma", "mes", "nom", "name",
    };

    // ---- Pia reference ----------------------------------------------------------------------------------

    /// <summary>
    /// Every alias here is treated as REPAIRED (not crisp) even though it is an exact table hit —
    /// only the literal token "pia" is crisp. Per-alias justification:
    /// <list type="bullet">
    /// <item><c>pias</c>, <c>pia's</c> — EN plural/possessive; the apostrophe survives tokenization.</item>
    /// <item><c>pea</c>, <c>peas</c>, <c>peer</c>, <c>pier</c> — EN STT renderings of /ˈpiːə/, the design
    /// doc's own worked examples of how "Pia" gets mis-transcribed by English acoustic models.</item>
    /// <item><c>pieer</c> — observed from Parakeet in a live session. Rule (b) cannot reach it
    /// (<c>Levenshtein("pieer","pia") == 3</c>), and it is a word in none of the three languages, so
    /// listing it costs no false-grant risk.</item>
    /// <item><c>piya</c>, <c>peeya</c> — EN phonetic spellings STT sometimes emits for the same vowel glide.</item>
    /// <item><c>pija</c>, <c>piha</c>, <c>bia</c> — DE renderings: /j/-glide insertion, /h/ epenthesis, and
    /// p→b voicing confusion respectively.</item>
    /// <item><c>pya</c>, <c>piat</c> — FR renderings: glide contraction and an orthographic silent-t.</item>
    /// </list>
    /// </summary>
    public static readonly string[] PiaAliases =
    {
        "pias", "pia's", "pea", "peas", "peer", "pier", "pieer",
        "piya", "peeya", "pija", "piha", "bia", "pya", "piat",
    };

    public static readonly HashSet<string> PiaAliasSet = new(PiaAliases, StringComparer.Ordinal);

    /// <summary>
    /// Real words one edit from "pia" that would otherwise false-positive rule (b) — the generic
    /// Levenshtein-≤1 fuzzy match. Rule (b) is length-gated at &gt;=4 chars precisely because a
    /// Levenshtein-≤1 check on a bare 3-letter word would also admit "via"/"pie"/"pin"/"pit"/"pig"/"pip",
    /// which is far too loose for a hard requirement; the length gate alone keeps those out, so this
    /// blocklist only needs to cover 4+ character false friends.
    /// </summary>
    public static readonly HashSet<string> PiaFalseFriends = new(StringComparer.Ordinal)
    {
        "pita", "pisa", "pima", "pika",
    };

    /// <summary>The letter-spelled fallback: three consecutive single-letter tokens "p", "i", "a".</summary>
    public static readonly string[] PiaLetterSequence = { "p", "i", "a" };

    // ---- Fuzzy false friends for the acceptance/recording/negation repair path ---------------------------

    /// <summary>
    /// Real words that sit exactly one edit from a &gt;=5-character lexicon entry while meaning something
    /// entirely different, so the Levenshtein-≤1 repair must refuse them outright. The motivating case:
    /// "the CONTENT of this meeting is recorded by Pia" contains no acceptance verb at all, yet
    /// <c>content</c> is one substitution from <c>consent</c>, which was enough to grant a purely
    /// descriptive sentence at exactly the 0.85 threshold. Same class: <c>accent</c>/<c>except</c> against
    /// <c>accept</c>, and the elided French negation <c>n'accepte</c> against <c>j'accepte</c> (which also
    /// destroyed the ne…pas straddle by leaving no standalone "ne" token behind).
    /// </summary>
    public static readonly HashSet<string> FuzzyFalseFriends = new(StringComparer.Ordinal)
    {
        // en — one edit from "consent"/"consents"/"accept"/"accepts"
        "content", "contents", "contest", "contests", "consult",
        "accent", "accents", "ascent", "ascents", "except", "excepts",
        "decent", "descent", "recent",
        // fr — the elided negation, whichever way STT renders the apostrophe
        "n'accepte", "naccepte", "n'accepté", "naccepté",
    };

    /// <summary>
    /// True for a token carrying an elided French negation ("n'accepte", "n'ai"). Such a token can never
    /// satisfy a fuzzy acceptance match — it is a REFUSAL that happens to be one edit from "j'accepte".
    /// </summary>
    public static bool IsElidedNegation(string token)
        => token.StartsWith("n'", StringComparison.Ordinal) && token.Length > 2;

    // ---- Precomputed flattened token sets (used for name-capture stop/reject checks) ---------------------

    public static readonly IReadOnlyDictionary<string, HashSet<string>> FlattenedAcceptanceTokens =
        Flatten(Acceptance);

    public static readonly IReadOnlyDictionary<string, HashSet<string>> FlattenedRecordingTokens =
        Flatten(Recording);

    private static IReadOnlyDictionary<string, HashSet<string>> Flatten(
        IReadOnlyDictionary<string, IReadOnlyList<string[]>> phrasesByLanguage)
    {
        var result = new Dictionary<string, HashSet<string>>();
        foreach (var (language, phrases) in phrasesByLanguage)
        {
            var flat = new HashSet<string>(StringComparer.Ordinal);
            foreach (var phrase in phrases)
            {
                foreach (var token in phrase)
                    flat.Add(token);
            }

            result[language] = flat;
        }

        return result;
    }
}
