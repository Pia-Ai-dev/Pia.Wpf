using System.Text;
using System.Text.Json;
using Pia.Models;

namespace Pia.Services;

/// <summary>
/// A one-line-per-call record of the tools an interactive reply used, appended to that reply when it
/// is replayed to the model on a later turn. Interactive history carries no tool content at all, so
/// without this the model cannot see its own calls and will invent an answer when asked about them.
/// Names, targets and counts only — never a payload.
/// </summary>
internal static class ChatToolSummary
{
    private const int MaxTargetChars = 120;
    private const int MaxCalls = 40;

    private static readonly string[] TargetArgKeys =
        ["path", "reference", "name", "title", "pattern", "query"];

    /// <summary>One record line for a completed call. <paramref name="card"/> is the action card the
    /// call raised, when it raised one — it carries the resolved path and the diff tallies.</summary>
    public static string FormatCall(string toolName, IDictionary<string, object?>? arguments, ActionCardInfo? card)
    {
        var sb = new StringBuilder(toolName);

        var target = card is { FilePath.Length: > 0 } ? card.FilePath : FirstTargetArg(arguments);
        if (!string.IsNullOrEmpty(target))
            sb.Append(" (").Append(Truncate(target)).Append(')');

        if (card is not null)
        {
            sb.Append(" — ").Append(card.State switch
            {
                ActionCardState.Accepted => "approved and applied",
                ActionCardState.Declined => "declined by the user, not applied",
                _ => "not resolved",
            });

            if (card.HasDiff)
                sb.Append(", +").Append(card.AddedCount).Append('/').Append('-').Append(card.RemovedCount).Append(" lines");
        }

        return sb.ToString();
    }

    /// <summary>Appends the record to a reply's text for the model's eyes. Returns the text unchanged
    /// when there is nothing to record.</summary>
    public static string Append(string replyText, IReadOnlyList<string> calls)
    {
        if (calls.Count == 0) return replyText;

        var sb = new StringBuilder(replyText);
        if (sb.Length > 0) sb.Append("\n\n");
        sb.Append("[tool calls made while producing this reply — a record for your later reference, not part of the reply]");
        foreach (var call in calls.Take(MaxCalls))
            sb.Append("\n- ").Append(call);
        if (calls.Count > MaxCalls)
            sb.Append("\n- … and ").Append(calls.Count - MaxCalls).Append(" more");

        return sb.ToString();
    }

    private static string? FirstTargetArg(IDictionary<string, object?>? arguments)
    {
        if (arguments is null) return null;
        foreach (var key in TargetArgKeys)
        {
            if (!arguments.TryGetValue(key, out var value) || value is null) continue;
            var text = value switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
                JsonElement je => je.GetRawText(),
                _ => value.ToString(),
            };
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return null;
    }

    private static string Truncate(string value)
        => value.Length <= MaxTargetChars ? value : value[..MaxTargetChars] + "…";
}
