using System.Text.Json.Nodes;
using static Pia.Services.Providers.Http.AnthropicNativeStore;

namespace Pia.Services.Providers.Http;

/// <summary>The server's replay rule, applied to a Messages API request body before it leaves the client.</summary>
internal static class AnthropicNativeReplay
{
    /// <summary>
    /// Rewrites assistant messages whose <c>tool_use</c> ids all hit one stored response: verbatim when their text
    /// still matches the stored text, else blocks-only. Returns how many messages were rewritten.
    /// </summary>
    public static int Apply(JsonArray messages, AnthropicNativeStore store, int maxMessages)
    {
        var replayed = 0;
        var newlyReplayed = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not JsonObject { } message
                || Str(message["role"]) != "assistant"
                || message["content"] is not JsonArray content)
            {
                continue;
            }

            var ids = content.OfType<JsonObject>()
                .Where(b => Type(b) == "tool_use")
                .Select(b => Str(b["id"]))
                .ToList();
            if (ids.Count == 0 || ids.Any(id => id is null) || !TryFindOne(store, ids!, out var key, out var wasReplayed))
                continue;

            // Once replayed, always replayed: flipping a message back would change the prefix every later thinking
            // block is bound to. The cap only limits entries replayed for the first time.
            if (!wasReplayed)
            {
                if (newlyReplayed >= maxMessages)
                    continue;
                newlyReplayed++;
            }

            message["content"] = Rebuild(content, store.Take(key), ids.ToHashSet()!);
            replayed++;
        }

        if (replayed > 0)
            DropUnpairedSearchBlocks(messages);
        return replayed;
    }

    /// <summary>The last occurrence of a repeated <c>tool_use</c> id wins; earlier calls and their results are dropped.</summary>
    public static bool DropEarlierDuplicateToolUses(JsonArray messages)
    {
        var lastOwner = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < messages.Count; i++)
        {
            foreach (var id in BlocksOf(messages[i], "assistant", "tool_use").Select(b => Str(b["id"])).OfType<string>())
                lastOwner[id] = i;
        }

        var changed = false;
        for (var i = 0; i < messages.Count; i++)
        {
            foreach (var block in BlocksOf(messages[i], "assistant", "tool_use").ToList())
            {
                if (Str(block["id"]) is not { } id || lastOwner[id] == i)
                    continue;

                Remove(messages[i]!, block);
                if (i + 1 < messages.Count)
                {
                    foreach (var result in BlocksOf(messages[i + 1], "user", "tool_result")
                                 .Where(r => Str(r["tool_use_id"]) == id).ToList())
                    {
                        Remove(messages[i + 1]!, result);
                    }
                }

                changed = true;
            }
        }

        if (changed)
        {
            for (var i = messages.Count - 1; i >= 0; i--)
            {
                if (messages[i]?["content"] is JsonArray { Count: 0 })
                    messages.RemoveAt(i);
            }
        }

        return changed;
    }

    private static bool TryFindOne(AnthropicNativeStore store, IReadOnlyList<string> ids, out object key, out bool replayed)
    {
        key = null!;
        replayed = false;
        foreach (var id in ids)
        {
            if (!store.TryFind(id, out var hit, out replayed) || (key is not null && !ReferenceEquals(key, hit)))
                return false;
            key = hit;
        }

        return true;
    }

    private static JsonArray Rebuild(JsonArray content, IReadOnlyList<JsonObject> stored, HashSet<string> ids)
    {
        // Calls the request no longer holds have no tool_result behind them, so they cannot come back.
        bool Keep(JsonObject b) => Type(b) != "tool_use" || (Str(b["id"]) is { } id && ids.Contains(id));

        var clientText = string.Concat(content.OfType<JsonObject>().Where(b => Type(b) == "text").Select(b => Str(b["text"])));
        var storedText = string.Concat(stored.Where(b => Type(b) == "text").Select(b => Str(b["text"])));
        if (Normalize(clientText) == Normalize(storedText))
            return [.. stored.Where(Keep).Select(b => (JsonNode)b)];

        JsonArray rebuilt = [];
        foreach (var block in content.OfType<JsonObject>().Where(b => Type(b) is "thinking" or "redacted_thinking"))
            rebuilt.Add(block.DeepClone());
        if (clientText.Length > 0)
            rebuilt.Add(new JsonObject { ["type"] = "text", ["text"] = clientText });
        foreach (var block in stored.Where(b => Type(b) is "server_tool_use" or "web_search_tool_result" or "tool_use"))
        {
            if (Keep(block))
                rebuilt.Add(block);
        }

        return rebuilt;
    }

    // A search pair is legal only whole and in one message; a pending call or an orphan result would be a 400.
    private static void DropUnpairedSearchBlocks(JsonArray messages)
    {
        foreach (var message in messages.OfType<JsonObject>())
        {
            if (Str(message["role"]) != "assistant" || message["content"] is not JsonArray content)
                continue;

            var blocks = content.OfType<JsonObject>().ToList();
            var uses = blocks.Where(b => Type(b) == "server_tool_use").Select(b => Str(b["id"])).ToHashSet();
            var results = blocks.Where(b => Type(b) == "web_search_tool_result").Select(b => Str(b["tool_use_id"])).ToHashSet();
            foreach (var block in blocks)
            {
                if ((Type(block) == "server_tool_use" && !results.Contains(Str(block["id"])))
                    || (Type(block) == "web_search_tool_result" && !uses.Contains(Str(block["tool_use_id"]))))
                {
                    content.Remove(block);
                }
            }
        }
    }

    private static string Normalize(string text) =>
        string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    private static IEnumerable<JsonObject> BlocksOf(JsonNode? message, string role, string type) =>
        message is JsonObject m && Str(m["role"]) == role && m["content"] is JsonArray content
            ? content.OfType<JsonObject>().Where(b => Type(b) == type)
            : [];

    private static void Remove(JsonNode message, JsonObject block) =>
        ((JsonArray)message["content"]!).Remove(block);
}
