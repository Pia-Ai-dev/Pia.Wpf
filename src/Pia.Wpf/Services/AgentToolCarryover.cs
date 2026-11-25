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
    /// <summary>The cut moves in steps of this many results and never reaches the newest this many.</summary>
    internal const int ClearChunk = 8;

    /// <summary>Carried result bodies are cleared only once their total passes this, so tiny acks never cost a re-write.</summary>
    internal const int CarriedResultBudgetChars = 48_000;

    /// <summary>
    /// The smallest multiple of <see cref="ClearChunk"/> whose uncleared tail fits the budget, capped so the newest
    /// <see cref="ClearChunk"/> keep their body. A pure function of the sizes, so a boundary that adds small results
    /// keeps the cut, and with it the cached prefix.
    /// </summary>
    internal static int ClearedCount(IReadOnlyList<int> resultChars)
    {
        var cap = resultChars.Count < ClearChunk ? 0 : (resultChars.Count - ClearChunk) / ClearChunk * ClearChunk;
        var tail = resultChars.Sum(c => (long)c);
        var cut = 0;
        while (cut < cap && tail > CarriedResultBudgetChars)
        {
            for (var i = cut; i < cut + ClearChunk; i++)
                tail -= resultChars[i];
            cut += ClearChunk;
        }

        return cut;
    }

    private static int ResultChars(FunctionResultContent result) => result.Result switch
    {
        string s => s.Length,
        null => 0,
        var other => JsonSerializer.Serialize(other).Length,
    };

    /// <summary>Cap on a persisted result; the live carry keeps results as sent.</summary>
    internal const int MaxCarriedResultChars = 4000;

    private const string TruncationSuffix = "\n[truncated]";

    /// <summary>The one sentence the per-step instruction adds, so a model that meets a cleared result re-reads
    /// instead of reconstructing. Model-facing, so deliberately unlocalized.</summary>
    internal const string ReReadHint =
        "Tool results from earlier steps that are shown cleared are not in your context — read the file again "
        + "before using its content; never reconstruct it from memory.";

    internal static string CapResult(string result) =>
        result.Length > MaxCarriedResultChars ? result[..MaxCarriedResultChars] + TruncationSuffix : result;

    /// <summary>The persisted and resumed shape of a round: calls and results only, each result capped.</summary>
    internal static IReadOnlyList<ChatMessage> Capture(IEnumerable<ChatMessage> roundMessages)
    {
        var captured = new List<ChatMessage>();
        foreach (var message in roundMessages)
        {
            var contents = new List<AIContent>(message.Contents.Count);
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case FunctionResultContent { Result: string s } result when s.Length > MaxCarriedResultChars:
                        contents.Add(new FunctionResultContent(result.CallId, CapResult(s)));
                        break;
                    case FunctionCallContent or FunctionResultContent:
                        contents.Add(content);
                        break;
                }
            }

            if (contents.Count > 0)
                captured.Add(new ChatMessage(message.Role, contents));
        }

        return captured;
    }

    /// <summary>
    /// Messages as a later request sends them: reasoning removed, since a thinking block replayed under another
    /// system prompt is a history edit, and tool images already swapped for their placeholder. Never mutates.
    /// </summary>
    internal static IReadOnlyList<ChatMessage> AsSent(IEnumerable<ChatMessage> messages)
    {
        var sent = new List<ChatMessage>();
        foreach (var message in messages)
        {
            if (!message.Contents.Any(c => c is TextReasoningContent))
            {
                sent.Add(message);
                continue;
            }

            var contents = message.Contents.Where(c => c is not TextReasoningContent).ToList();
            if (contents.Count > 0)
                sent.Add(new ChatMessage(message.Role, contents) { AdditionalProperties = message.AdditionalProperties });
        }

        ToolLoopImageMessages.Consume(sent);
        return sent;
    }

    /// <summary>
    /// The step request with every carried call and result dropped, for a turn that sends NO tools — the grace
    /// turn, and any step whose persona resolved a provider without tool calling. A provider handed
    /// <c>tool_calls</c> with no tools declared can reject the request outright, and a turn that cannot call
    /// anything has no use for the pairs regardless.
    /// </summary>
    internal static IReadOnlyList<ChatMessage> WithoutToolExchanges(IReadOnlyList<ChatMessage> messages)
    {
        if (!messages.Any(HasToolContent))
            return messages;

        return [.. messages.Where(m => !HasToolContent(m))];
    }

    private static bool HasToolContent(ChatMessage message) =>
        message.Contents.Any(c => c is FunctionCallContent or FunctionResultContent);

    /// <summary>
    /// Replaces the oldest <see cref="ClearedCount"/> result bodies with a placeholder naming the call to
    /// re-issue; the calls stay, since they are what the model needs to make the call again.
    /// </summary>
    internal static IReadOnlyList<ChatMessage> ClearOldResults(IReadOnlyList<ChatMessage> messages)
    {
        var calls = new Dictionary<string, FunctionCallContent>(StringComparer.Ordinal);
        var results = new List<(int Message, int Content)>();
        var sizes = new List<int>();
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
                    case FunctionResultContent result:
                        results.Add((i, j));
                        sizes.Add(ResultChars(result));
                        break;
                }
            }
        }

        var cleared = ClearedCount(sizes);
        if (cleared == 0)
            return messages;

        var stale = results.Take(cleared).ToLookup(r => r.Message, r => r.Content);
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
