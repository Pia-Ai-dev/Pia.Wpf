using System.Text.RegularExpressions;
using F23.StringSimilarity;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public partial class TokenMapService : ITokenMapService
{
    private static readonly string[] Categories = ["Person", "Nickname", "Email", "Phone", "Address", "Date", "Custom"];

    private readonly IPiiDetector _piiDetector;
    private readonly IMemoryService _memoryService;
    private readonly ISettingsService _settingsService;

    private Dictionary<string, string> _valueToToken = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _tokenToValue = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, int> _categoryCounters = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _customKeywords = new(StringComparer.OrdinalIgnoreCase);

    private Regex? _detokenizeRegex;

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

        if (!_categoryCounters.TryGetValue(category, out var counter))
            counter = 0;

        counter++;
        _categoryCounters[category] = counter;

        var token = $"[{category}_{counter}]";
        _valueToToken[value] = token;
        _tokenToValue[token] = value;
        return token;
    }

    public string TokenizeStructuredResult(string formattedResult)
    {
        if (string.IsNullOrEmpty(formattedResult))
            return formattedResult;

        var result = formattedResult;

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

        return result;
    }

    [GeneratedRegex(@"\b\w+\b")]
    private static partial Regex WordBoundaryRegex();

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

            var jw = new JaroWinkler();

            string? bestKeyword = null;
            var bestSimilarity = 0.0;

            foreach (var keyword in _customKeywords)
            {
                var similarity = jw.Similarity(word.ToLowerInvariant(), keyword.ToLowerInvariant());
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
        var categoryPattern = string.Join("|", Categories.Select(Regex.Escape));
        _detokenizeRegex = new Regex($@"\[(?:{categoryPattern})_\d+\]", RegexOptions.Compiled);
    }
}
