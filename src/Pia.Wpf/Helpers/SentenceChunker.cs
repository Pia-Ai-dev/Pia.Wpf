using System.Text;

namespace Pia.Helpers;

/// <summary>
/// Accumulates streamed tokens and yields complete sentences on .!? boundaries.
/// </summary>
public class SentenceChunker
{
    private readonly StringBuilder _buffer = new();
    private static readonly char[] SentenceEnders = ['.', '!', '?'];

    /// <summary>
    /// Adds a token and yields any complete sentences found.
    /// A sentence boundary is a .!? followed by whitespace or end-of-token-at-buffer-boundary.
    /// </summary>
    public IEnumerable<string> AddToken(string token)
    {
        _buffer.Append(token);
        var text = _buffer.ToString();

        var searchFrom = 0;
        while (searchFrom < text.Length)
        {
            var endIndex = text.IndexOfAny(SentenceEnders, searchFrom);
            if (endIndex < 0)
                break;

            // Check if followed by whitespace or end of string
            var nextIndex = endIndex + 1;
            if (nextIndex < text.Length && !char.IsWhiteSpace(text[nextIndex]))
            {
                // Not a real boundary (e.g., "3.14", "e.g.")
                searchFrom = nextIndex;
                continue;
            }

            // Include trailing whitespace in the yielded sentence
            while (nextIndex < text.Length && char.IsWhiteSpace(text[nextIndex]))
                nextIndex++;

            var sentence = text[..nextIndex].Trim();
            if (!string.IsNullOrWhiteSpace(sentence))
            {
                yield return sentence;
            }

            text = text[nextIndex..];
            _buffer.Clear();
            _buffer.Append(text);
            searchFrom = 0;
        }
    }

    /// <summary>
    /// Returns any remaining text that hasn't formed a complete sentence.
    /// </summary>
    public string? Flush()
    {
        var remaining = _buffer.ToString().Trim();
        _buffer.Clear();
        return string.IsNullOrWhiteSpace(remaining) ? null : remaining;
    }
}
