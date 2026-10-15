using System.Text.Json.Nodes;
using Pia.Services.Providers.Http;
using Xunit;

namespace Pia.Tests.Services.Providers;

public class AnthropicNativeStoreTests
{
    private static List<JsonObject> Response(params string[] toolUseIds)
    {
        List<JsonObject> blocks =
        [
            new() { ["type"] = "server_tool_use", ["id"] = "srvtoolu_" + toolUseIds[0], ["name"] = "web_search", ["input"] = new JsonObject() },
            new() { ["type"] = "web_search_tool_result", ["tool_use_id"] = "srvtoolu_" + toolUseIds[0], ["content"] = new JsonArray() },
        ];
        foreach (var id in toolUseIds)
            blocks.Add(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = "f", ["input"] = new JsonObject() });
        return blocks;
    }

    [Fact]
    public void EveryToolUseIdOfAResponse_HitsTheSameEntry()
    {
        var store = new AnthropicNativeStore();

        Assert.True(store.TryAdd(Response("toolu_a", "toolu_b")));

        Assert.True(store.TryGet("toolu_a", out var a));
        Assert.True(store.TryGet("toolu_b", out var b));
        Assert.Same(a.Key, b.Key);
        Assert.Equal(4, a.Blocks.Count);
        Assert.False(store.TryGet("toolu_c", out _));
    }

    [Fact]
    public void AResponseWithoutSearchOrCitations_OrWithoutAClientCall_IsNotStored()
    {
        var store = new AnthropicNativeStore();

        Assert.False(store.TryAdd([new JsonObject { ["type"] = "text", ["text"] = "t" },
            new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_a", ["name"] = "f", ["input"] = new JsonObject() }]));
        Assert.False(store.TryAdd(Response("toolu_a").Take(2).ToList()));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void AReadSlidesTheExpiry_AndAnUnreadEntryExpires()
    {
        var clock = new MovableClock(DateTimeOffset.UnixEpoch);
        var store = new AnthropicNativeStore(clock);
        store.TryAdd(Response("toolu_a"));
        store.TryAdd(Response("toolu_b"));

        clock.Advance(AnthropicNativeStore.SlidingExpiry - TimeSpan.FromMinutes(1));
        Assert.True(store.TryGet("toolu_a", out _));
        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.True(store.TryGet("toolu_a", out _));
        Assert.False(store.TryGet("toolu_b", out _));
    }

    [Fact]
    public void ReturnedBlocks_AreFreshCopies_ThatCanJoinARequest()
    {
        var store = new AnthropicNativeStore();
        store.TryAdd(Response("toolu_a"));
        store.TryGet("toolu_a", out var first);

        var request = new JsonArray([.. first.Blocks.Select(b => (JsonNode)b)]);
        request[0]!["id"] = "changed";
        store.TryGet("toolu_a", out var second);

        Assert.Equal("srvtoolu_toolu_a", (string?)second.Blocks[0]["id"]);
    }

    // A replayed entry going missing mid-loop would flip that message back to the unreplayed shape.
    [Fact]
    public void OverTheCharBound_AnEntryNeverReplayed_IsEvictedBeforeAReplayedOne()
    {
        var oneEntry = new JsonArray([.. Response("toolu_a").Select(b => (JsonNode)b)]).ToJsonString().Length;
        var clock = new MovableClock(DateTimeOffset.UnixEpoch);
        var store = new AnthropicNativeStore(clock, maxChars: oneEntry * 2 + 10);
        store.TryAdd(Response("toolu_a"));
        Assert.True(store.TryGet("toolu_a", out _));
        clock.Advance(TimeSpan.FromSeconds(1));
        store.TryAdd(Response("toolu_b"));
        clock.Advance(TimeSpan.FromSeconds(1));

        store.TryAdd(Response("toolu_c"));

        Assert.True(store.TryGet("toolu_a", out _));
        Assert.False(store.TryGet("toolu_b", out _));
        Assert.True(store.TryGet("toolu_c", out _));
    }

    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
