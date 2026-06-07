using System.Text;

namespace Pia.Services;

/// <summary>
/// Splits a raw streamed assistant buffer into user-visible text and hidden
/// <c>&lt;think&gt;…&lt;/think&gt;</c> reasoning content. Tolerates an unclosed
/// trailing <c>&lt;think&gt;</c> block (everything after it is treated as
/// thinking) so partial streams render sensibly. Extracted verbatim from
/// <c>AssistantViewModel.ParseStreamedContent</c>.
/// </summary>
public static class StreamThinkTagParser
{
    public static (string visible, string thinking) Parse(string rawText)
    {
        var visible = new StringBuilder();
        var thinking = new StringBuilder();
        var remaining = rawText.AsSpan();

        while (remaining.Length > 0)
        {
            var thinkStart = remaining.IndexOf("<think>".AsSpan(), StringComparison.OrdinalIgnoreCase);
            if (thinkStart < 0)
            {
                visible.Append(remaining);
                break;
            }

            visible.Append(remaining[..thinkStart]);
            remaining = remaining[(thinkStart + 7)..]; // skip "<think>"

            var thinkEnd = remaining.IndexOf("</think>".AsSpan(), StringComparison.OrdinalIgnoreCase);
            if (thinkEnd < 0)
            {
                // Unclosed think block - all remaining is thinking content
                thinking.Append(remaining);
                break;
            }

            thinking.Append(remaining[..thinkEnd]);
            remaining = remaining[(thinkEnd + 8)..]; // skip "</think>"
        }

        return (visible.ToString().TrimStart(), thinking.ToString().Trim());
    }
}
