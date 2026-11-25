using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Pia.Services.Providers.Http;
using Xunit;

namespace Pia.Tests.Services.Providers;

public class AnthropicNativeContentBuilderTests
{
    internal static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", name));

    internal static AnthropicNativeContentBuilder Accumulate(string sse)
    {
        var accumulator = new AnthropicNativeContentBuilder();
        foreach (var line in sse.Split('\n'))
            accumulator.FeedLine(line.TrimEnd('\r'));
        return accumulator;
    }

    private static string? Type(JsonObject block) => (string?)block["type"];

    [Fact]
    public void TheRecordedStream_RebuildsEveryBlock()
    {
        var accumulator = Accumulate(Fixture("anthropic-two-search-stream.sse"));

        Assert.True(accumulator.IsComplete);
        var blocks = accumulator.Blocks;
        Assert.Equal(
            ["server_tool_use", "server_tool_use", "web_search_tool_result", "web_search_tool_result",
             "thinking", "text", "text", "text", "text", "text", "tool_use"],
            blocks.Select(Type));
        Assert.All(blocks.Where(b => Type(b) == "server_tool_use"), b => Assert.NotNull((string?)b["input"]!["query"]));
        Assert.All(blocks.Where(b => Type(b) == "web_search_tool_result"),
            b => Assert.NotEmpty((JsonArray)b["content"]!));
        Assert.False(string.IsNullOrEmpty((string?)blocks[4]["signature"]));
        Assert.False(string.IsNullOrEmpty((string?)blocks[4]["thinking"]));
        Assert.Equal(2, blocks.Count(b => b["citations"] is JsonArray { Count: > 0 }));
        Assert.NotNull((string?)blocks[^1]["input"]!["text"]);
    }

    [Fact]
    public void TheRecordedStream_MatchesTheShapeOfTheBufferedBody()
    {
        var streamed = Accumulate(Fixture("anthropic-two-search-stream.sse")).Blocks;
        var buffered = AnthropicNativeContentBuilder.FromBuffered(Fixture("anthropic-two-search-buffered.json"));

        Assert.True(buffered.IsComplete);
        Assert.Equal(KeysOf(buffered.Blocks.Where(b => Type(b) != "text")),
            KeysOf(streamed.Where(b => Type(b) != "text")));
    }

    private static IEnumerable<string> KeysOf(IEnumerable<JsonObject> blocks) =>
        blocks.Select(b => Type(b) + ":" + string.Join(",", b.Select(p => p.Key).Order(StringComparer.Ordinal)));

    [Fact]
    public void ASyntheticStream_RebuildsTheExactContent()
    {
        var sse = Sse(
            """{"type":"message_start","message":{"content":[]}}""",
            """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"plan "}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"it"}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"SIG"}}""",
            """{"type":"content_block_stop","index":0}""",
            """{"type":"content_block_start","index":1,"content_block":{"citations":[],"type":"text","text":""}}""",
            """{"type":"content_block_delta","index":1,"delta":{"type":"citations_delta","citation":{"type":"web_search_result_location","cited_text":"c","url":"u","title":"t","encrypted_index":"e"}}}""",
            """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"cited"}}""",
            """{"type":"content_block_stop","index":1}""",
            """{"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_1","name":"f","input":{}}}""",
            """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"a\":"}}""",
            """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"1}"}}""",
            """{"type":"content_block_stop","index":2}""",
            """{"type":"message_delta","delta":{"stop_reason":"tool_use"}}""",
            """{"type":"message_stop"}""");

        var accumulator = Accumulate(sse);

        Assert.True(accumulator.IsComplete);
        Assert.Equal(
            """[{"type":"thinking","thinking":"plan it","signature":"SIG"},{"citations":[{"type":"web_search_result_location","cited_text":"c","url":"u","title":"t","encrypted_index":"e"}],"type":"text","text":"cited"},{"type":"tool_use","id":"toolu_1","name":"f","input":{"a":1}}]""",
            new JsonArray([.. accumulator.Blocks.Select(b => (JsonNode)b.DeepClone())]).ToJsonString());
    }

    // Parallel search plus a client tool: the search result opens the NEXT response, so this one ends pending.
    [Fact]
    public void APendingSearchCall_AndAMaxUsesResult_AreKeptAsParsed()
    {
        var sse = Sse(
            """{"type":"content_block_start","index":0,"content_block":{"type":"web_search_tool_result","tool_use_id":"srvtoolu_0","content":{"type":"web_search_tool_result_error","error_code":"max_uses_exceeded"}}}""",
            """{"type":"content_block_stop","index":0}""",
            """{"type":"content_block_start","index":1,"content_block":{"type":"server_tool_use","id":"srvtoolu_1","name":"web_search","input":{}}}""",
            """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"query\":\"q\"}"}}""",
            """{"type":"content_block_stop","index":1}""",
            """{"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_1","name":"f","input":{}}}""",
            """{"type":"content_block_stop","index":2}""",
            """{"type":"message_delta","delta":{"stop_reason":"tool_use"}}""",
            """{"type":"message_stop"}""");

        var blocks = Accumulate(sse).Blocks;

        Assert.Equal("max_uses_exceeded", (string?)blocks[0]["content"]!["error_code"]);
        Assert.Equal("q", (string?)blocks[1]["input"]!["query"]);
        Assert.Equal("toolu_1", (string?)blocks[2]["id"]);
    }

    [Fact]
    public void AStreamCutOffMidBlock_IsNeverComplete()
    {
        var full = Fixture("anthropic-two-search-stream.sse");
        var cut = full[..full.IndexOf("\"type\":\"tool_use\"", StringComparison.Ordinal)];

        Assert.False(Accumulate(cut).IsComplete);
    }

    [Fact]
    public void AnErrorEvent_IsNeverComplete()
    {
        var sse = Sse(
            """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
            """{"type":"error","error":{"type":"overloaded_error"}}""",
            """{"type":"message_stop"}""");

        Assert.False(Accumulate(sse).IsComplete);
    }

    [Theory]
    [InlineData(true, 7, 11)]
    [InlineData(false, 13, 10)]
    public async Task TheTee_HandsTheReaderEveryByteUnchanged_AndReportsOnce(bool isEventStream, int chunk, int blockCount)
    {
        var name = isEventStream ? "anthropic-two-search-stream.sse" : "anthropic-two-search-buffered.json";
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "TestData", name),
            TestContext.Current.CancellationToken);
        var reports = new List<AnthropicNativeContentBuilder>();
        await using var tee = new AnthropicNativeStreamRecorder(new MemoryStream(bytes), isEventStream, reports.Add);

        var copy = new MemoryStream();
        var buffer = new byte[chunk];
        int read;
        while ((read = await tee.ReadAsync(buffer, TestContext.Current.CancellationToken)) > 0)
            copy.Write(buffer, 0, read);

        Assert.Equal(bytes, copy.ToArray());
        var report = Assert.Single(reports);
        Assert.Equal(blockCount, report.Blocks.Count);
    }

    [Fact]
    public async Task TheTee_ReportsNothing_WhenTheReaderStopsMidStream()
    {
        var bytes = Encoding.UTF8.GetBytes(Fixture("anthropic-two-search-stream.sse"));
        var reports = new List<AnthropicNativeContentBuilder>();
        await using (var tee = new AnthropicNativeStreamRecorder(new MemoryStream(bytes), true, reports.Add))
        {
            var buffer = new byte[bytes.Length / 2];
            await tee.ReadExactlyAsync(buffer, TestContext.Current.CancellationToken);
        }

        Assert.Empty(reports);
    }

    internal static string Sse(params string[] events) =>
        string.Concat(events.Select(e => "event: x\ndata: " + e + "\n\n"));
}
