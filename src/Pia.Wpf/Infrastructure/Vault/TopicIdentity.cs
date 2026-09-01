using System.Text.RegularExpressions;

namespace Pia.Infrastructure.Vault;

/// <summary>
/// Reduces a topic subject to a matching key, so two names for one thing land on one page. Ingest
/// deduplicated on the slug alone, which left "Meta" and "Meta Platforms", "DAX" and "DAX 40",
/// "Azure OpenAI" and "Azure OpenAI Service" as separate pages.
///
/// <para>Matching only — never a filename. <see cref="VaultSlug.Slugify"/> stays the sole source of
/// page paths, so a canonical key is free to be lossy in ways a slug must not be.</para>
/// </summary>
public static partial class TopicIdentity
{
    // Trailing noise words that name the legal or packaging form of a thing rather than the thing.
    private static readonly string[] DroppedSuffixes =
    [
        "incorporated", "inc", "corporation", "corp", "company", "co", "limited", "ltd", "plc",
        "gmbh", "mbh", "ag", "kg", "se", "sa", "nv", "bv", "oy", "ab", "as", "spa", "srl",
        "group", "holding", "holdings", "platforms", "technologies", "technology", "systems",
        "solutions", "software", "service", "services", "index", "industrial", "average",
    ];

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex Parenthetical();

    [GeneratedRegex(@"^(the|der|die|das|le|la|les|el|los)\b")]
    private static partial Regex LeadingArticle();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphanumericRun();

    /// <summary>
    /// The matching key for <paramref name="subject"/>: parentheticals and a leading article
    /// dropped, then slugified, then trailing form-words and a trailing bare number stripped
    /// ("DAX 40" and "DAX" agree). Falls back to the plain slug when stripping would empty it, so
    /// a topic legitimately named "Group" keeps an identity of its own.
    /// </summary>
    public static string Canonicalize(string subject)
    {
        var withoutParentheticals = Parenthetical().Replace(subject ?? string.Empty, " ");
        var slug = VaultSlug.Slugify(withoutParentheticals);
        var trimmed = LeadingArticle().Replace(slug, string.Empty).Trim('-');

        var words = trimmed.Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && IsDroppable(words[^1]))
        {
            words.RemoveAt(words.Count - 1);
        }

        var canonical = string.Join('-', words);
        return canonical.Length == 0 ? NonAlphanumericRun().Replace(slug, "-").Trim('-') : canonical;
    }

    private static bool IsDroppable(string word) =>
        DroppedSuffixes.Contains(word, StringComparer.Ordinal) || word.All(char.IsAsciiDigit);
}
