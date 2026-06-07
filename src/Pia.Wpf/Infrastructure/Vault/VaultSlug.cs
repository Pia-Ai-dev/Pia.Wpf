using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Pia.Infrastructure.Vault;

/// <summary>
/// The deterministic §6 slug algorithm (steps 1-6, without the document-scoped collision suffix of
/// step 7). Shared by <see cref="MarkdownVaultParser"/> (which adds dedupe per document) and the write
/// path so a heading slugs identically across clients and across read/write — there is exactly one
/// implementation of the algorithm.
/// </summary>
public static class VaultSlug
{
    // §6 step 4: maximal runs of non-[a-z0-9] -> single '-'.
    private static readonly Regex NonSlugRun = new("[^a-z0-9]+", RegexOptions.Compiled);

    /// <summary>
    /// Slugify a heading per spec §6 steps 1-6: NFD normalize, strip combining marks (Mn), invariant
    /// lowercase, collapse non-<c>[a-z0-9]</c> runs to a single hyphen, trim hyphens, and fall back to
    /// <c>section</c> if empty. Step 7 (collision suffix) is the parser's responsibility (it is
    /// document-scoped) and is not applied here.
    /// </summary>
    public static string Slugify(string heading)
    {
        var decomposed = heading.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        var lowered = sb.ToString().ToLowerInvariant();
        var slug = NonSlugRun.Replace(lowered, "-").Trim('-');
        return slug.Length == 0 ? "section" : slug;
    }
}
