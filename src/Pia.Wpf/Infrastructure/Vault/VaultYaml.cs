using System.Globalization;
using System.Text.RegularExpressions;

namespace Pia.Infrastructure.Vault;

/// <summary>
/// Encodes a frontmatter scalar so it survives a round-trip through <see cref="MarkdownVaultParser"/>.
/// Hand-rolled rather than delegating to YamlDotNet's serializer: that one emits whole documents, so
/// it prefixes a document-start marker (<c>--- ''</c>) on values it cannot write plainly — which would
/// end the frontmatter block mid-way.
/// </summary>
public static partial class VaultYaml
{
    // YAML plain scalars that would deserialize to something other than this string.
    private static readonly HashSet<string> ReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "null", "true", "false", "yes", "no", "on", "off", "y", "n",
        ".inf", "-.inf", "+.inf", ".nan",
    };

    // Indicators that change meaning at the START of a plain scalar (§5.3 c-indicator).
    private const string LeadingIndicators = "-?:,[]{}#&*!|>'\"%@`";

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    /// <summary>
    /// Quote/escape <paramref name="value"/> for a single-line <c>key: value</c> frontmatter entry.
    /// Whitespace runs collapse to one space first: a newline would otherwise need a block scalar and
    /// break the hand-built one-line layout.
    /// </summary>
    public static string EncodeScalar(string? value)
    {
        var flat = WhitespaceRun().Replace(value ?? string.Empty, " ").Trim();

        // Single-quoted style is literal — '' is the only escape — so it needs no escape table.
        return NeedsQuoting(flat) ? "'" + flat.Replace("'", "''") + "'" : flat;
    }

    private static bool NeedsQuoting(string value)
    {
        if (value.Length == 0
            || LeadingIndicators.Contains(value[0])
            || value[^1] == ':'
            || value.Contains(": ", StringComparison.Ordinal)
            || value.Contains(" #", StringComparison.Ordinal)
            || ReservedWords.Contains(value)
            || value == "~")
        {
            return true;
        }

        // A numeric-looking title must not come back as a number's ToString().
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }
}
