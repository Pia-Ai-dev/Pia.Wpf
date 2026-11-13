using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.Similarity;

namespace Pia.Services;

public partial class TokenMapService : ITokenMapService
{
    private static readonly string[] Categories = ["Person", "Nickname", "Email", "Phone", "Address", "Date", "Custom"];

    private const string GuidPattern =
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b";

    private static readonly string TokenPattern = $@"\[(?:{string.Join("|", Categories.Select(Regex.Escape))})_\d+\]";

    private static readonly Regex KeptSpanRegex = new($"{TokenPattern}|{GuidPattern}", RegexOptions.Compiled);

    // A JSON or source escape such as \n ends a word, although its last character is a letter or hex digit.
    private const string WordStart = @"(?:(?<![\p{L}\p{M}\p{N}])|(?<=\\(?:[nrtbf]|u[0-9A-Fa-f]{4})))";
    private const string WordEnd = @"(?![\p{L}\p{M}\p{N}])";

    private readonly IPiiDetector _piiDetector;
    private readonly IMemoryService _memoryService;
    private readonly ISettingsService _settingsService;

    private Dictionary<string, string> _valueToToken = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _tokenToValue = new(StringComparer.OrdinalIgnoreCase);
    // Every spelling here is exactly the value its token detokenizes to.
    private Dictionary<string, string> _exactValueToToken = new(StringComparer.Ordinal);
    private Dictionary<string, int> _categoryCounters = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _customKeywords = new(StringComparer.OrdinalIgnoreCase);

    private Regex? _detokenizeRegex;
    private int _exactValuesVersion;
    private VerbatimMatcher? _verbatimMatcher;

    private sealed record VerbatimMatcher(Regex Regex, int Version);

    public TokenMapService(IPiiDetector piiDetector, IMemoryService memoryService, ISettingsService settingsService)
    {
        _piiDetector = piiDetector;
        _memoryService = memoryService;
        _settingsService = settingsService;
        BuildDetokenizeRegex();
    }

    public string Tokenize(string value, string category)
    {
        if (_valueToToken.TryGetValue(value, out var existingToken))
            return existingToken;

        var token = MintToken(value, category);
        _valueToToken[value] = token;
        return token;
    }

    // Unlike Tokenize, a case variant of a known value gets a token of its own.
    private string TokenizeExact(string value, string category)
    {
        if (_exactValueToToken.TryGetValue(value, out var existingToken))
            return existingToken;

        var token = MintToken(value, category);
        _valueToToken.TryAdd(value, token);
        return token;
    }

    private string MintToken(string value, string category)
    {
        _categoryCounters.TryGetValue(category, out var counter);
        counter++;
        _categoryCounters[category] = counter;

        var token = $"[{category}_{counter}]";
        _tokenToValue[token] = value;
        _exactValueToToken[value] = token;
        _exactValuesVersion++;
        return token;
    }

    public string TokenizeVerbatim(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var result = _exactValueToToken.Count == 0 ? text : GetVerbatimRegex().Replace(text, ReplaceKnownValue);
        return TokenizeDetectedPii(result);

        string ReplaceKnownValue(Match match) =>
            !match.Groups["kept"].Success && _exactValueToToken.TryGetValue(match.Value, out var token)
                ? token
                : match.Value;
    }

    private Regex GetVerbatimRegex()
    {
        var version = _exactValuesVersion;
        if (_verbatimMatcher is { } cached && cached.Version == version)
            return cached.Regex;

        var values = _exactValueToToken.Keys
            .Where(value => value.Length > 0)
            .OrderByDescending(value => value.Length)
            .ThenBy(value => value, StringComparer.Ordinal)
            .ToList();
        var wordValues = values.Where(value => IsWordChar(value[0])).Select(Literal).ToList();
        var otherValues = values.Where(value => !IsWordChar(value[0])).Select(Literal).ToList();

        // WordStart is shared, not repeated per value: a group that starts with plain literals is searched
        // by its first characters, which keeps a large map fast.
        var alternatives = new List<string> { $"(?<kept>{TokenPattern}|{GuidPattern})" };
        if (wordValues.Count > 0)
            alternatives.Add($"{WordStart}(?:{string.Join("|", wordValues)})");
        if (otherValues.Count > 0)
            alternatives.Add($"(?:{string.Join("|", otherValues)})");

        var regex = new Regex(string.Join("|", alternatives));
        _verbatimMatcher = new VerbatimMatcher(regex, version);
        return regex;

        static string Literal(string value) => Regex.Escape(value) + (IsWordChar(value[^1]) ? WordEnd : "");
    }

    // The same set as the regex class [\p{L}\p{M}\p{N}].
    private static bool IsWordChar(char c) => char.GetUnicodeCategory(c) <= UnicodeCategory.OtherNumber;

    // Replaces each detected span in place, so a token stands for exactly the text it covers.
    private string TokenizeDetectedPii(string text)
    {
        var detected = _piiDetector.DetectPii(text);
        if (detected.Count == 0)
            return text;

        var kept = KeptSpanRegex.Matches(text);
        var builder = new StringBuilder(text.Length);
        var position = 0;
        foreach (var match in detected.OrderBy(m => m.Start).ThenByDescending(m => m.Length))
        {
            if (match.Start < position || !IsDetectedAt(text, match) || BreaksKeptSpan(kept, match))
                continue;

            builder.Append(text, position, match.Start - position).Append(TokenizeExact(match.Value, match.Category));
            position = match.Start + match.Length;
        }

        return position == 0 ? text : builder.Append(text, position, text.Length - position).ToString();
    }

    private static bool IsDetectedAt(string text, PiiMatch match) =>
        match.Length > 0
        && match.Length == match.Value.Length
        && match.Start >= 0
        && match.Start + match.Length <= text.Length
        && string.CompareOrdinal(text, match.Start, match.Value, 0, match.Length) == 0;

    // A detection may swallow a whole GUID, as in a GUID@host address, but never a token or part of either.
    private static bool BreaksKeptSpan(MatchCollection kept, PiiMatch match)
    {
        var end = match.Start + match.Length;
        return kept.Any(span =>
            span.Index < end && match.Start < span.Index + span.Length
            && (span.Value[0] == '[' || span.Index < match.Start || span.Index + span.Length > end));
    }

    public string TokenizeStructuredResult(string formattedResult)
    {
        if (string.IsNullOrEmpty(formattedResult))
            return formattedResult;

        var result = formattedResult;

        // Mask GUIDs first so neither the known-PII pass nor the regex-based
        // PII detector can mangle hex digit runs inside an ID. Without this, a
        // query result like "[ID: ...-db79226916b8]" gets its 8-digit hex run
        // replaced with a [Phone_N] token, breaking the subsequent tool call.
        var guidPlaceholders = new Dictionary<string, string>();
        result = GuidRegex().Replace(result, match =>
        {
            var placeholder = $"__PIA_GUID_{guidPlaceholders.Count}__";
            guidPlaceholders[placeholder] = match.Value;
            return placeholder;
        });

        // Replace known PII values (longer values first to avoid partial matches)
        foreach (var (value, token) in _valueToToken.OrderByDescending(kvp => kvp.Key.Length))
        {
            result = result.Replace(value, token, StringComparison.OrdinalIgnoreCase);
        }

        // Detect and tokenize new emails/phones via regex
        var newMatches = _piiDetector.DetectPii(result);
        foreach (var match in newMatches)
        {
            // Only tokenize if this value isn't already a token
            if (!match.Value.StartsWith('[') && !_valueToToken.ContainsKey(match.Value))
            {
                var token = Tokenize(match.Value, match.Category);
                result = result.Replace(match.Value, token, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Fuzzy match custom PII keywords for near-miss typos
        result = FuzzyReplaceCustomKeywords(result);

        foreach (var (placeholder, guid) in guidPlaceholders)
            result = result.Replace(placeholder, guid);

        return result;
    }

    [GeneratedRegex(@"\b\w+\b")]
    private static partial Regex WordBoundaryRegex();

    [GeneratedRegex(GuidPattern)]
    private static partial Regex GuidRegex();

    private string FuzzyReplaceCustomKeywords(string text)
    {
        if (_customKeywords.Count == 0)
            return text;

        return WordBoundaryRegex().Replace(text, match =>
        {
            var word = match.Value;

            // Skip words that are 2 chars or fewer
            if (word.Length <= 2)
                return word;

            // Skip words already replaced with tokens
            if (word.StartsWith('['))
                return word;

            // Skip if already an exact match (handled earlier)
            if (_valueToToken.ContainsKey(word))
                return word;

            string? bestKeyword = null;
            var bestSimilarity = 0.0;

            foreach (var keyword in _customKeywords)
            {
                var similarity = JaroWinkler.Similarity(word.ToLowerInvariant(), keyword.ToLowerInvariant());
                if (similarity >= 0.85 && similarity < 1.0 && similarity > bestSimilarity)
                {
                    bestSimilarity = similarity;
                    bestKeyword = keyword;
                }
                else if (similarity >= 0.85 && similarity < 1.0 && similarity == bestSimilarity && bestKeyword is not null)
                {
                    // Tiebreak: prefer longer keyword, then alphabetically first
                    if (keyword.Length > bestKeyword.Length ||
                        (keyword.Length == bestKeyword.Length &&
                         string.Compare(keyword, bestKeyword, StringComparison.OrdinalIgnoreCase) < 0))
                    {
                        bestKeyword = keyword;
                    }
                }
            }

            if (bestKeyword is not null && _valueToToken.TryGetValue(bestKeyword, out var token))
                return token;

            return word;
        });
    }

    public string Detokenize(string text)
    {
        if (string.IsNullOrEmpty(text) || _detokenizeRegex is null)
            return text;

        return _detokenizeRegex.Replace(text, match =>
        {
            return _tokenToValue.TryGetValue(match.Value, out var realValue)
                ? realValue
                : match.Value; // Unknown token passes through
        });
    }

    public string DetokenizeLoose(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return LooseTokenRegex().Replace(text, match =>
        {
            // Rebuild the canonical [Category_number] form; _tokenToValue is OrdinalIgnoreCase, so a
            // lowercased category still resolves. Unknown tokens (or non-token brackets that happen to
            // match the shape, e.g. "[Chapter 1]") are absent from the map and pass through untouched.
            var canonical = $"[{match.Groups[1].Value}_{match.Groups[2].Value}]";
            return _tokenToValue.TryGetValue(canonical, out var realValue)
                ? realValue
                : match.Value;
        });
    }

    [GeneratedRegex(@"\[([A-Za-z]+)[ _\-]?(\d+)\]")]
    private static partial Regex LooseTokenRegex();

    public string DetokenizeBare(string text)
    {
        if (string.IsNullOrEmpty(text) || _tokenToValue.Count == 0)
            return text;

        // Recover the BRACKET-LESS token shape a generative rewrite sometimes leaves in a short
        // title/subject (e.g. "Person_1" instead of "[Person_1]") — the bracketed forms are already
        // handled by Detokenize/DetokenizeLoose. Only KNOWN tokens are reversed; unknown bare shapes
        // pass through (mirroring the bracketed passes). Iterate longest token first and anchor each
        // match on \b word boundaries so "Person_1" cannot partially match inside "Person_11". The
        // "_" separator also matches a "-" the model may have substituted; case-insensitive.
        foreach (var (token, value) in _tokenToValue.OrderByDescending(kvp => kvp.Key.Length))
        {
            var bare = token.Trim('[', ']');
            var pattern = @"\b" + Regex.Escape(bare).Replace("_", "[_-]") + @"\b";
            text = Regex.Replace(text, pattern, _ => value, RegexOptions.IgnoreCase);
        }

        return text;
    }

    public string? GetToken(string value, string category)
    {
        return _valueToToken.TryGetValue(value, out var token) ? token : null;
    }

    public async Task InitializeAsync()
    {
        // Load PII keywords from settings
        var settings = await _settingsService.GetSettingsAsync();
        foreach (var entry in settings.Privacy.PiiKeywords)
        {
            if (!string.IsNullOrWhiteSpace(entry.Keyword))
            {
                Tokenize(entry.Keyword, entry.Category);
                _customKeywords.Add(entry.Keyword);
            }
        }

        // Pre-populate from personal_profile memory objects
        var profiles = await _memoryService.GetObjectsByTypeAsync(MemoryObjectTypes.PersonalProfile);
        foreach (var profile in profiles)
        {
            RegisterPiiFromMemoryObject(profile);
        }

        // Pre-populate from contact_list memory objects
        var contacts = await _memoryService.GetObjectsByTypeAsync(MemoryObjectTypes.ContactList);
        foreach (var contact in contacts)
        {
            RegisterPiiFromMemoryObject(contact);
        }
    }

    public void Clear()
    {
        _valueToToken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _tokenToValue = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _exactValueToToken = new Dictionary<string, string>(StringComparer.Ordinal);
        _exactValuesVersion++;
        _categoryCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _customKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private void RegisterPiiFromMemoryObject(MemoryObject memoryObject)
    {
        var matches = _piiDetector.DetectPiiInStructured(memoryObject.Data, memoryObject.Type);
        foreach (var match in matches)
        {
            Tokenize(match.Value, match.Category);
        }
    }

    private void BuildDetokenizeRegex()
    {
        _detokenizeRegex = new Regex(TokenPattern, RegexOptions.Compiled);
    }
}
