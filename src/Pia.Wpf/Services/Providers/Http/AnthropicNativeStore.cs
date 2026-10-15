using System.Text.Json.Nodes;

namespace Pia.Services.Providers.Http;

/// <summary>
/// The native content blocks of recent Anthropic responses, keyed by every client <c>tool_use</c> id in them, so a later
/// request can replay the search pairs and citations the SDK adapter drops.
/// </summary>
internal sealed class AnthropicNativeStore
{
    internal static readonly TimeSpan SlidingExpiry = TimeSpan.FromMinutes(120);
    internal const long DefaultMaxChars = 8_000_000;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _byId = new(StringComparer.Ordinal);
    private readonly List<Entry> _entries = [];
    private readonly TimeProvider _clock;
    private readonly long _maxChars;
    private long _chars;

    public AnthropicNativeStore(TimeProvider? clock = null, long maxChars = DefaultMaxChars)
    {
        _clock = clock ?? TimeProvider.System;
        _maxChars = maxChars;
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    /// <summary>Stores a response's blocks when it holds a client tool call and something the adapter would drop.</summary>
    public bool TryAdd(IReadOnlyList<JsonObject> blocks)
    {
        var ids = blocks.Where(b => Type(b) == "tool_use").Select(b => Str(b["id"])).OfType<string>().ToList();
        if (ids.Count == 0 || !blocks.Any(IsNative))
            return false;

        var json = new JsonArray([.. blocks.Select(b => (JsonNode)b.DeepClone())]).ToJsonString();
        if (json.Length > _maxChars)
            return false;

        lock (_gate)
        {
            var entry = new Entry(json, ids, _clock.GetUtcNow());
            foreach (var id in ids)
            {
                if (_byId.TryGetValue(id, out var previous))
                    Remove(previous);
            }

            foreach (var id in ids)
                _byId[id] = entry;
            _entries.Add(entry);
            _chars += json.Length;
            Evict();
            return true;
        }
    }

    /// <summary>A fresh copy of the entry holding <paramref name="toolUseId"/>; a hit slides the expiry of the whole entry.</summary>
    public bool TryGet(string toolUseId, out StoredResponse response)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(toolUseId, out var entry))
            {
                var now = _clock.GetUtcNow();
                if (now - entry.LastUsed < SlidingExpiry)
                {
                    entry.LastUsed = now;
                    entry.Replayed = true;
                    response = new StoredResponse(entry.Key, ParseBlocks(entry.Json));
                    return true;
                }

                Remove(entry);
            }
        }

        response = default;
        return false;
    }

    // Detached from the parsed array so the caller can add them to a request body.
    private static List<JsonObject> ParseBlocks(string json)
    {
        var array = (JsonArray)JsonNode.Parse(json)!;
        var blocks = array.OfType<JsonObject>().ToList();
        array.Clear();
        return blocks;
    }

    // Never-replayed entries go first: evicting one a live loop replays would flip it to the unreplayed shape and
    // invalidate every later thinking block.
    private void Evict()
    {
        var now = _clock.GetUtcNow();
        foreach (var stale in _entries.Where(e => now - e.LastUsed >= SlidingExpiry).ToList())
            Remove(stale);

        while (_chars > _maxChars && _entries.Count > 1)
        {
            var victim = _entries.Where(e => !e.Replayed).MinBy(e => e.LastUsed)
                ?? _entries.MinBy(e => e.LastUsed)!;
            Remove(victim);
        }
    }

    private void Remove(Entry entry)
    {
        if (!_entries.Remove(entry))
            return;

        _chars -= entry.Json.Length;
        foreach (var id in entry.Ids)
        {
            if (_byId.TryGetValue(id, out var current) && ReferenceEquals(current, entry))
                _byId.Remove(id);
        }
    }

    internal static bool IsNative(JsonObject block) =>
        Type(block) is "server_tool_use" or "web_search_tool_result"
        || (Type(block) == "text" && block["citations"] is JsonArray { Count: > 0 });

    internal static string? Type(JsonObject block) => Str(block["type"]);

    internal static string? Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    internal readonly record struct StoredResponse(object Key, IReadOnlyList<JsonObject> Blocks);

    private sealed class Entry(string json, IReadOnlyList<string> ids, DateTimeOffset now)
    {
        public object Key { get; } = new();
        public string Json { get; } = json;
        public IReadOnlyList<string> Ids { get; } = ids;
        public DateTimeOffset LastUsed { get; set; } = now;
        public bool Replayed { get; set; }
    }
}
