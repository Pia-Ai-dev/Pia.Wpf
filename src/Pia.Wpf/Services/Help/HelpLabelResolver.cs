using System.Globalization;
using System.Text.RegularExpressions;
using Pia.Localization;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services.Help;

public sealed record HelpLabel(string English, string Localized);

/// <summary>Maps the English UI labels the guide quotes to the ones on the user's screen.</summary>
public sealed partial class HelpLabelResolver
{
    // Section names the guide walks through on top of the ones pia_settings paths already use.
    private static readonly string[] QuotedSectionKeys =
    [
        "Settings_Chat_Section", "Settings_Agent_NewChatMode_Section_Header", "Settings_MeetingBrowser_Section",
    ];

    public static IReadOnlyList<string> Keys { get; } =
        [.. HelpSettingsResolver.PathLocalizationKeys.Concat(QuotedSectionKeys).Distinct(StringComparer.Ordinal)];

    private static readonly Lazy<Dictionary<string, string>> KeyByEnglish = new(() => Keys
        .GroupBy(English, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase));

    private readonly ILocalizationService _localizationService;

    public HelpLabelResolver(ILocalizationService localizationService)
    {
        _localizationService = localizationService;
    }

    public TargetLanguage Language => _localizationService.CurrentLanguage;

    /// <summary>Empty on an English screen, and for labels that read the same in both languages.</summary>
    public IReadOnlyList<HelpLabel> For(IEnumerable<string> candidates)
    {
        if (Language == TargetLanguage.EN) return [];

        var labels = new List<HelpLabel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var phrase = candidate.Trim().TrimEnd(':', '.', ',', ';');
            if (!KeyByEnglish.Value.TryGetValue(phrase, out var key) || !seen.Add(key)) continue;

            var english = English(key);
            var localized = _localizationService[key];
            if (!string.Equals(english, localized, StringComparison.OrdinalIgnoreCase))
                labels.Add(new HelpLabel(english, localized));
        }
        return labels;
    }

    /// <summary>The guide bolds the UI labels it quotes, and joins a settings path with arrows.</summary>
    public static IEnumerable<string> Candidates(string markdown)
    {
        foreach (Match bold in BoldSpan().Matches(markdown))
        {
            foreach (var segment in bold.Groups[1].Value.Split(['→', '>']))
                yield return segment;
        }

        foreach (Match heading in HeadingLine().Matches(markdown))
            yield return heading.Groups[1].Value;
    }

    private static string English(string key) => LocalizationSource.Instance.GetString(key, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\*\*([^*]+)\*\*")]
    private static partial Regex BoldSpan();

    [GeneratedRegex(@"^#{2,6}[ \t]+(.+?)[ \t]*\r?$", RegexOptions.Multiline)]
    private static partial Regex HeadingLine();
}
