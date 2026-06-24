using System.IO;
using System.Text.Json;

namespace Pia.Infrastructure;

/// <summary>
/// Delta-filtered, in-process lint for write/patch operations. In-process parsers ONLY
/// (privacy + minimal-deps): JSON via <see cref="System.Text.Json"/>; YAML/TOML/etc. return
/// <c>null</c> (no parser available). "Delta-filtered" means a pre-existing parse error in the
/// OLD content is NOT blamed on the current write — only errors absent from the OLD baseline
/// are surfaced. Owned here so a future patch tool reuses the same helper.
/// </summary>
public static class WriteLintHelper
{
    /// <summary>
    /// Returns a human-readable lint message for NEW errors introduced by writing
    /// <paramref name="newContent"/> to <paramref name="path"/>, or <c>null</c> when there is
    /// nothing to surface (no supported parser, both versions parse, or the same error already
    /// existed in <paramref name="oldContent"/>).
    /// </summary>
    /// <param name="oldContent">Existing file content, or null for a brand-new file (empty baseline).</param>
    public static string? Lint(string path, string? oldContent, string newContent)
    {
        var ext = Path.GetExtension(path);
        if (string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase))
            return LintJson(oldContent, newContent);

        // No in-process parser for this type — explicitly no opinion.
        return null;
    }

    private static string? LintJson(string? oldContent, string newContent)
    {
        var baseline = JsonError(oldContent);   // null when old is valid / absent
        var current = JsonError(newContent);    // null when new is valid

        if (current is null) return null;                 // new content is valid JSON
        if (baseline == current) return null;             // same pre-existing error — not ours

        return $"JSON syntax error: {current}";
    }

    /// <summary>
    /// Parse signature for delta comparison: returns a stable "message @ line:pos" string when the
    /// content fails to parse as JSON, or null when it parses (or is null/empty — an empty file is a
    /// valid baseline, not a JSON error). Whitespace-only is treated as no-error baseline too.
    /// </summary>
    private static string? JsonError(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        try
        {
            using var _ = JsonDocument.Parse(content);
            return null;
        }
        catch (JsonException ex)
        {
            // LineNumber/BytePositionInLine are nullable; normalize for a stable signature.
            var line = ex.LineNumber.HasValue ? ex.LineNumber.Value + 1 : 0;
            var pos = ex.BytePositionInLine ?? 0;
            // Strip the volatile "Path: $ | LineNumber: .. | BytePositionInLine: .." suffix
            // that System.Text.Json appends so the signature is stable across content lengths.
            var msg = ex.Message;
            var cut = msg.IndexOf(" Path:", StringComparison.Ordinal);
            if (cut >= 0) msg = msg[..cut];
            return $"{msg.TrimEnd()} (line {line}, position {pos})";
        }
    }
}
