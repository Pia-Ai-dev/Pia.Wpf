using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Data;
using Pia.Helpers;

namespace Pia.Converters;

/// <summary>
/// Display-only rewrite of Obsidian-style wikilinks in vault content into clickable markdown links, so the
/// Memory inspector can navigate between pages. <c>[[topics/foo]]</c> becomes <c>[topics/foo](pia-memory:topics/foo)</c>
/// and <c>[[topics/foo|Foo]]</c> becomes <c>[Foo](pia-memory:topics/foo)</c>; the shared markdown renderer
/// then produces a hyperlink for free and <c>MarkdownMessageControl</c> routes the <see cref="WikiLinkScheme"/>
/// to in-app navigation.
///
/// <para>Applied ONLY to the inspector's read binding — it never mutates the stored body, so edit mode and
/// copy still see the raw wikilink syntax that Obsidian requires. Single-bracket placeholder tokens such as
/// <c>[Person_1]</c> require double brackets to match, so they are left untouched. Wikilink-shaped tokens
/// inside inline code spans and fenced code blocks are left verbatim (matching Obsidian), so a page that
/// documents the <c>[[...]]</c> syntax — or a technical token like <c>[[nodiscard]]</c> — inside code renders
/// as authored rather than as a link.</para>
/// </summary>
public sealed class WikiLinkConverter : IValueConverter
{
    // A single ordered scan: match a code region FIRST (fenced ``` / ~~~ block, or an inline `code` span) or
    // else a wikilink. Because the regex consumes left-to-right and code alternatives precede the wikilink,
    // any [[...]] inside code is swallowed by the code match and never rewritten. Targets and aliases stop at
    // ']' / '|' / newline so a stray single-bracket token like "[Person_1]" never matches.
    private static readonly Regex Pattern = new(
        @"(?<code>```[\s\S]*?```|~~~[\s\S]*?~~~|`+[^`\r\n]*`+)" +
        @"|\[\[\s*(?<target>[^\]|\r\n]+?)\s*(?:\|\s*(?<label>[^\]\r\n]+?)\s*)?\]\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text || text.Length == 0)
        {
            return value;
        }

        return Pattern.Replace(text, static match =>
        {
            // Code region: leave exactly as authored.
            if (match.Groups["code"].Success)
            {
                return match.Value;
            }

            var target = match.Groups["target"].Value;
            var label = match.Groups["label"].Success ? match.Groups["label"].Value : target;
            // A target with a ')' or whitespace would break the generated markdown link destination; vault
            // slugs are lowercase-hyphen so this is defensive — leave such a wikilink unchanged.
            if (target.IndexOfAny([')', ' ', '\t']) >= 0)
            {
                return match.Value;
            }

            return $"[{label}]({WikiLinkScheme.Prefix}{target})";
        });
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
