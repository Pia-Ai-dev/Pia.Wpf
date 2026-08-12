using System.Text.RegularExpressions;

namespace Pia.Infrastructure.Vault;

/// <summary>
/// Deterministic post-synthesis backstop that guarantees a topic-page body contains ONLY wikilinks that
/// resolve. The ingest synthesizer is prompted to link only known topics, but that is best-effort; this
/// pass is the actual guarantee. It is pure, unit-testable, and shares <see cref="VaultSlug.Slugify"/> with
/// the write path so a kept link canonicalizes to the exact on-disk filename slug.
///
/// <para>For every <c>[[target]]</c> / <c>[[target|label]]</c> in the body (with a leading <c>topics/</c>
/// stripped), <c>canonical = VaultSlug.Slugify(target)</c>:</para>
/// <list type="bullet">
///   <item><b>Target exists</b> (<c>canonical</c> ∈ <paramref name="knownSlugs"/>): rewrite to the canonical
///     <c>[[topics/&lt;canonical&gt;]]</c> (preserving any <c>|label</c>). This deterministically fixes
///     slug-drift (accents, punctuation, spacing) so a kept link always resolves at click time.</item>
///   <item><b>Target missing</b>: replace the whole <c>[[...]]</c> with its display text — the label if
///     present, else a readable form of the slug — so the prose keeps the words but carries no dead link.</item>
/// </list>
///
/// <para>The scan mirrors <see cref="Pia.Converters.WikiLinkConverter"/>'s code-aware pattern: fenced
/// (<c>```</c>/<c>~~~</c>) blocks and inline <c>`code`</c> spans are matched FIRST and left verbatim, so a
/// <c>[[...]]</c> documented inside code is never touched, and only DOUBLE-bracket tokens match, so a
/// single-bracket PII placeholder such as <c>[Person_1]</c> is left alone.</para>
/// </summary>
public static class WikiLinkReconciler
{
    // A single ordered scan mirroring WikiLinkConverter.Pattern: match a code region FIRST (fenced ``` /
    // ~~~ block, or an inline `code` span) or else a wikilink. Because the alternatives are ordered and the
    // regex consumes left-to-right, any [[...]] inside code is swallowed by the code match and never
    // rewritten. Targets/labels stop at ']' / '|' / newline so a stray single-bracket token never matches.
    private static readonly Regex Pattern = new(
        @"(?<code>```[\s\S]*?```|~~~[\s\S]*?~~~|`+[^`\r\n]*`+)" +
        @"|\[\[\s*(?<target>[^\]|\r\n]+?)\s*(?:\|\s*(?<label>[^\]\r\n]+?)\s*)?\]\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const string TopicsPrefix = "topics/";

    /// <summary>
    /// Rewrite kept links to their canonical slug and strip dangling links to plain text. Returns
    /// <paramref name="body"/> unchanged when it is null/empty. An empty <paramref name="knownSlugs"/>
    /// strips every link (nothing resolves).
    /// </summary>
    public static string Reconcile(string body, IReadOnlySet<string> knownSlugs)
    {
        if (string.IsNullOrEmpty(body))
        {
            return body;
        }

        return Pattern.Replace(body, match =>
        {
            // Code region: leave exactly as authored (mirrors WikiLinkConverter).
            if (match.Groups["code"].Success)
            {
                return match.Value;
            }

            var label = match.Groups["label"].Success ? match.Groups["label"].Value : null;

            // Trim surrounding slashes then strip a leading "topics/" — mirroring the click-time resolution
            // in VaultIndexService.WikiTargetReferences / MemoryViewModel, so "[[/topics/foo]]" is treated
            // the same here as it is there.
            var segment = match.Groups["target"].Value.Trim('/');
            if (segment.StartsWith(TopicsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                segment = segment[TopicsPrefix.Length..];
            }

            var canonical = VaultSlug.Slugify(segment);
            if (knownSlugs.Contains(canonical))
            {
                return label is null
                    ? $"[[{TopicsPrefix}{canonical}]]"
                    : $"[[{TopicsPrefix}{canonical}|{label}]]";
            }

            // Dangling: keep the words, drop the link syntax.
            return label ?? DisplayText(segment, canonical);
        });
    }

    // Display text for a stripped (dangling) link. A model that ignored the grounding and emitted a
    // natural-language target ("AT&T", "McDonald's") keeps its own words verbatim — the slug would lose the
    // casing and punctuation. A slug-shaped target ("globex-corp") has no words to preserve, so it is
    // title-cased from the canonical slug instead ("Globex Corp").
    private static string DisplayText(string segment, string canonical)
    {
        if (segment.Length > 0 && !IsSlugShaped(segment))
        {
            return segment;
        }

        var words = canonical.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0
            ? canonical
            : string.Join(' ', words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    // True when every character is already in the slug alphabet [a-z0-9-] (so there is nothing a slug
    // round-trip would lose). Anything with uppercase, spaces, or punctuation is treated as real prose.
    private static bool IsSlugShaped(string s) =>
        s.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
}
