namespace Pia.Services.LiveTranscription;

/// <summary>Scalar quoting shared by the transcript renderers.</summary>
internal static class YamlText
{
    private static readonly char[] Structural =
        [':', '#', '"', '\'', ',', '[', ']', '{', '}', '&', '*', '!', '|', '>', '%', '@', '`'];

    /// <summary>
    /// Quotes a value that would otherwise change the document's structure. Single-quoted style (inner
    /// <c>'</c> doubled) rather than double-quoted, because double-quoted YAML needs backslash escapes —
    /// a display label or a user-typed title can contain anything.
    /// </summary>
    public static string Scalar(string? value)
    {
        var text = (value ?? string.Empty).ReplaceLineEndings(" ");

        var needsQuoting = text.Length == 0
            || text != text.Trim()
            || text.StartsWith('-')
            || text.IndexOfAny(Structural) >= 0;

        return needsQuoting ? "'" + text.Replace("'", "''") + "'" : text;
    }
}
