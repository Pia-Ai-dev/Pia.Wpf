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

        // Set once a <think> block closes with real visible text already streamed, so the
        // next visible run gets a leading space instead of fusing onto the run before it
        // (a model that reopens reasoning mid-answer, e.g. around a tool call).
        var visibleSeparatorPending = false;

        while (remaining.Length > 0)
        {
            var thinkStart = remaining.IndexOf("<think>".AsSpan(), StringComparison.OrdinalIgnoreCase);
            if (thinkStart < 0)
            {
                AppendVisible(visible, remaining, ref visibleSeparatorPending);
                break;
            }

            AppendVisible(visible, remaining[..thinkStart], ref visibleSeparatorPending);
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
            visibleSeparatorPending = true;
        }

        return (visible.ToString().TrimStart(), thinking.ToString().Trim());
    }

    private static void AppendVisible(StringBuilder visible, ReadOnlySpan<char> segment, ref bool separatorPending)
    {
        if (segment.IsEmpty) return;
        if (separatorPending)
        {
            visible.Append(' ');
            separatorPending = false;
        }
        visible.Append(segment);
    }
}
