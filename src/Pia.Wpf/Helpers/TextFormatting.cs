using System.Text;

namespace Pia.Helpers;

/// <summary>
/// Small, dependency-free text utilities shared across the assistant surface
/// (e.g. chat-title derivation and AI-generated title sanitization).
/// </summary>
public static class TextFormatting
{
    /// <summary>
    /// Trims the text and collapses every run of whitespace (including newlines)
    /// into a single space, so multi-line content renders as a tidy one-liner.
    /// </summary>
    public static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var ch in text.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
        }
        return sb.ToString();
    }
}
