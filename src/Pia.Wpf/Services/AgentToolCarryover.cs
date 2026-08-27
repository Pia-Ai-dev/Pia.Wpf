using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Pia.Services;

/// <summary>
/// Carries one step's tool calls and results into the next step's request, bounded so a run cannot pay for
/// them forever. Both methods BUILD rather than mutate — a carried message is shared with the executor's
/// accumulating transcript, and an in-place rewrite would corrupt every later step.
/// </summary>
internal static class AgentToolCarryover
{
    /// <summary>How many of the newest tool results keep their body. Covers a read-then-write pair and the
    /// searches around it; a re-callable tool can simply be called again, which is what the placeholder says.</summary>
    internal const int KeptResults = 8;

    /// <summary>A carried result is context, not the deliverable — the step that needed it in full already had
    /// it inside its own tool loop.</summary>
    internal const int MaxCarriedResultChars = 4000;

    private const string TruncationSuffix = "\n[truncated]";

    /// <summary>The one sentence the per-step instruction adds, so a model that meets a cleared result re-reads
    /// instead of reconstructing. Model-facing, so deliberately unlocalized.</summary>
    internal const string ReReadHint =
        "Tool results from earlier steps that are shown cleared are not in your context — read the file again "
        + "before using its content; never reconstruct it from memory.";

    /// <summary>Snapshots the messages one tool round appended, capping each result at capture time.</summary>
    internal static IReadOnlyList<ChatMessage> Capture(IEnumerable<ChatMessage> roundMessages)
    {
        var captured = new List<ChatMessage>();
        foreach (var message in roundMessages)
        {
            captured.Add(NeedsTruncation(message) ? Truncate(message) : message);
        }

        return captured;
    }

    /// <summary>
    /// Replaces the body of every tool result older than the newest <see cref="KeptResults"/> with a
    /// placeholder naming the call to re-issue. The <see cref="FunctionCallContent"/> is left alone: it is the
    /// record that the call was made, and what the model needs to make it again.
    /// </summary>
    internal static IReadOnlyList<ChatMessage> ClearOldResults(IReadOnlyList<ChatMessage> messages)
    {
        var calls = new Dictionary<string, FunctionCallContent>(StringComparer.Ordinal);
        var results = new List<(int Message, int Content)>();
        for (var i = 0; i < messages.Count; i++)
        {
            var contents = messages[i].Contents;
            for (var j = 0; j < contents.Count; j++)
            {
                switch (contents[j])
                {
                    case FunctionCallContent call:
                        calls[call.CallId] = call;
                        break;
                    case FunctionResultContent:
                        results.Add((i, j));
                        break;
                }
            }
        }

        if (results.Count <= KeptResults)
            return messages;

        var stale = results.Take(results.Count - KeptResults).ToLookup(r => r.Message, r => r.Content);
        var rewritten = new List<ChatMessage>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            if (!stale.Contains(i))
            {
                rewritten.Add(messages[i]);
                continue;
            }

            var slots = stale[i].ToHashSet();
            var contents = new List<AIContent>(messages[i].Contents.Count);
            for (var j = 0; j < messages[i].Contents.Count; j++)
            {
                contents.Add(slots.Contains(j) && messages[i].Contents[j] is FunctionResultContent result
                    ? new FunctionResultContent(result.CallId, Placeholder(calls, result.CallId))
                    : messages[i].Contents[j]);
            }

            rewritten.Add(new ChatMessage(messages[i].Role, contents));
        }

        return rewritten;
    }

    private static bool NeedsTruncation(ChatMessage message) =>
        message.Contents.Any(c => c is FunctionResultContent { Result: string s } && s.Length > MaxCarriedResultChars);

    private static ChatMessage Truncate(ChatMessage message)
    {
        var contents = new List<AIContent>(message.Contents.Count);
        foreach (var content in message.Contents)
        {
            contents.Add(content is FunctionResultContent { Result: string s } result && s.Length > MaxCarriedResultChars
                ? new FunctionResultContent(result.CallId, s[..MaxCarriedResultChars] + TruncationSuffix)
                : content);
        }

        return new ChatMessage(message.Role, contents);
    }

    private static string Placeholder(IReadOnlyDictionary<string, FunctionCallContent> calls, string callId)
    {
        if (!calls.TryGetValue(callId, out var call))
            return "[result cleared; call the tool again if you need it]";

        var path = PathArgument(call);
        return path is null
            ? $"[result cleared; call {call.Name} again if you need it]"
            : $"[result cleared; call {call.Name} on {path} again if you need it]";
    }

    private static string? PathArgument(FunctionCallContent call)
    {
        if (call.Arguments is null || !call.Arguments.TryGetValue("path", out var value))
            return null;

        return value switch
        {
            string s when !string.IsNullOrWhiteSpace(s) => s,
            JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
            _ => null,
        };
    }
}
