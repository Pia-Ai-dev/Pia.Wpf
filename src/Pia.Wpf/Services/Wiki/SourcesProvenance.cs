using System.IO;

namespace Pia.Services.Wiki;

/// <summary>
/// Shared conventions between the ingest writer (<see cref="IngestService"/>, which records provenance
/// on topic pages) and the sources reader (<see cref="VaultSourcesService"/>, which surfaces the RAW
/// layer in the Memory view): which <c>sources/</c> files count as ingestable text, and how the
/// best-effort <c>sources: [sources/a, sources/b]</c> frontmatter flow list is read back. Kept in one
/// place so the writer and the reader can never drift on the lenient parse.
/// </summary>
public static class SourcesProvenance
{
    /// <summary>Extensions ingest treats as text (binary handling is deferred).</summary>
    private static readonly string[] TextExtensions =
        [".txt", ".md", ".markdown", ".text", ".csv", ".json", ".log", ".html", ".htm", ".xml"];

    /// <summary>True iff ingest would accept the file as a text source (extension-less = best-effort text).</summary>
    public static bool IsTextSource(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
        {
            return true; // extension-less files are treated as text (best-effort)
        }

        return TextExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The source refs recorded in a page's <c>sources:</c> frontmatter, or empty when the page has no
    /// (parseable) frontmatter or no <c>sources:</c> key. Reads the RAW text — never
    /// <see cref="Pia.Models.Vault.VaultDocument.Frontmatter"/>, whose YAML parser flattens flow lists
    /// to their .NET type name (see IngestService's round-trip note).
    /// </summary>
    public static IReadOnlyList<string> ReadSourceRefs(string rawText)
    {
        var open = rawText.IndexOf("---\n", StringComparison.Ordinal);
        if (open != 0)
        {
            return [];
        }

        var close = rawText.IndexOf("\n---", open + 3, StringComparison.Ordinal);
        if (close < 0)
        {
            return [];
        }

        var fmBody = rawText[(open + 4)..(close + 1)];
        return ParseFlowList(FindKeyValue(fmBody, "sources:"));
    }

    /// <summary>
    /// Parse a flattened <c>sources</c> value back into individual refs. The parser may flatten a YAML
    /// list to either a flow form <c>[a, b]</c> or a space/newline-joined scalar; handle both leniently.
    /// </summary>
    public static List<string> ParseFlowList(string? value)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            trimmed = trimmed[1..^1];
        }

        foreach (var part in trimmed.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var item = part.Trim().Trim('-').Trim();
            if (item.Length > 0 && !result.Contains(item, StringComparer.Ordinal))
            {
                result.Add(item);
            }
        }

        return result;
    }

    /// <summary>Return the raw value text after a <c>key:</c> line in the frontmatter keys block, or null if absent.</summary>
    public static string? FindKeyValue(string fmBody, string keyPrefix)
    {
        foreach (var line in fmBody.Split('\n'))
        {
            if (line.StartsWith(keyPrefix, StringComparison.Ordinal))
            {
                return line[keyPrefix.Length..].Trim();
            }
        }

        return null;
    }
}
