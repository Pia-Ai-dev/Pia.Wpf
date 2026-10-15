using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Anthropic;
using Microsoft.Extensions.AI;
using Pia.Services.Providers.Http;
using Xunit;

namespace Pia.Tests.Services.Providers;

/// <summary>The direct provider replays the search pairs and citations the SDK adapter drops, by the server's rule.</summary>
public class AnthropicNativeReplayTests
{
    private static readonly List<JsonObject> Recorded = AnthropicNativeAccumulator
        .FromBuffered(AnthropicNativeAccumulatorTests.Fixture("anthropic-two-search-buffered.json")).Blocks.ToList();

    private static string? Type(JsonNode? block) => (string?)block?["type"];

    private static JsonObject ToolUse => Recorded.Single(b => Type(b) == "tool_use");

    private static string RecordedText => string.Concat(Recorded.Where(b => Type(b) == "text").Select(b => (string?)b["text"]));

    private static AnthropicNativeStore StoreWithRecorded()
    {
        var store = new AnthropicNativeStore();
        Assert.True(store.TryAdd(Recorded));
        return store;
    }

    /// <summary>What the SDK adapter sends back for the recorded turn: thinking, one text, the call.</summary>
    private static JsonObject AdapterAssistant(string? text = null, bool withThinking = true)
    {
        JsonArray content = [];
        if (withThinking)
            content.Add(Recorded.Single(b => Type(b) == "thinking").DeepClone());
        text ??= RecordedText;
        if (text.Length > 0)
            content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        content.Add(new JsonObject
        {
            ["type"] = "tool_use", ["id"] = ToolUse["id"]!.DeepClone(), ["name"] = "save_note",
            ["input"] = ToolUse["input"]!.DeepClone(),
        });
        return new JsonObject { ["role"] = "assistant", ["content"] = content };
    }

    private static JsonObject ToolResult(string id) => new()
    {
        ["role"] = "user",
        ["content"] = new JsonArray { new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = "saved" } },
    };

    private static string Body(params JsonObject[] turns) => new JsonObject
    {
        ["model"] = "claude-sonnet-5-5",
        ["max_tokens"] = 1024,
        ["messages"] = new JsonArray([new JsonObject { ["role"] = "user", ["content"] = "go" }, .. turns]),
    }.ToJsonString();

    private static JsonArray Messages(string? rewritten)
    {
        Assert.NotNull(rewritten);
        return (JsonArray)JsonNode.Parse(rewritten!)!["messages"]!;
    }

    private static string Json(IEnumerable<JsonNode?> blocks) => new JsonArray([.. blocks.Select(b => b!.DeepClone())]).ToJsonString();

    [Fact]
    public void UnchangedText_ReplaysTheStoredContentVerbatim()
    {
        var rewritten = AnthropicRequestHandler.Rewrite(
            Body(AdapterAssistant(), ToolResult((string)ToolUse["id"]!)), false, false, nativeStore: StoreWithRecorded());

        var content = (JsonArray)Messages(rewritten)[1]!["content"]!;
        Assert.Equal(Json(Recorded), Json(content));
    }

    // Whitespace differences only: the adapter may join the cited fragments differently.
    [Fact]
    public void TextThatDiffersOnlyInWhitespace_StillReplaysVerbatim()
    {
        var rewritten = AnthropicRequestHandler.Rewrite(
            Body(AdapterAssistant(" " + RecordedText.Replace(" ", "  ", StringComparison.Ordinal) + "\n"),
                ToolResult((string)ToolUse["id"]!)),
            false, false, nativeStore: StoreWithRecorded());

        Assert.Equal(Json(Recorded), Json((JsonArray)Messages(rewritten)[1]!["content"]!));
    }

    // AgentToolCarryover carries the calls of a step without its prose or thinking.
    [Fact]
    public void ACarriedCallWithoutText_ReplaysBlocksOnly()
    {
        var rewritten = AnthropicRequestHandler.Rewrite(
            Body(AdapterAssistant(text: "", withThinking: false), ToolResult((string)ToolUse["id"]!)),
            false, false, nativeStore: StoreWithRecorded());

        var content = (JsonArray)Messages(rewritten)[1]!["content"]!;
        Assert.Equal(["server_tool_use", "server_tool_use", "web_search_tool_result", "web_search_tool_result", "tool_use"],
            content.Select(Type));
        Assert.Equal(Json(Recorded.Where(b => Type(b) != "thinking" && Type(b) != "text")), Json(content));
    }

    [Fact]
    public void RewrittenText_ReplaysBlocksOnly_KeepingTheMessagesOwnThinkingAndText()
    {
        var assistant = AdapterAssistant("a different sentence");
        var thinking = assistant["content"]![0]!.ToJsonString();

        var rewritten = AnthropicRequestHandler.Rewrite(
            Body(assistant, ToolResult((string)ToolUse["id"]!)), false, false, nativeStore: StoreWithRecorded());

        var content = (JsonArray)Messages(rewritten)[1]!["content"]!;
        Assert.Equal(["thinking", "text", "server_tool_use", "server_tool_use", "web_search_tool_result",
            "web_search_tool_result", "tool_use"], content.Select(Type));
        Assert.Equal(thinking, content[0]!.ToJsonString());
        Assert.Equal("a different sentence", (string?)content[1]!["text"]);
    }

    [Fact]
    public void NoHit_LeavesTheBodyUntouched()
    {
        Assert.Null(AnthropicRequestHandler.Rewrite(
            Body(AdapterAssistant(), ToolResult((string)ToolUse["id"]!)), false, false,
            nativeStore: new AnthropicNativeStore()));
    }

    [Fact]
    public void OnlyTheNewestEightMessages_AreReplayed()
    {
        var store = new AnthropicNativeStore();
        var turns = new List<JsonObject>();
        for (var i = 0; i < AnthropicRequestHandler.MaxReplayedMessages + 2; i++)
        {
            var id = $"toolu_{i:D2}";
            store.TryAdd(
            [
                new JsonObject { ["type"] = "server_tool_use", ["id"] = "srv_" + i, ["name"] = "web_search", ["input"] = new JsonObject() },
                new JsonObject { ["type"] = "web_search_tool_result", ["tool_use_id"] = "srv_" + i, ["content"] = new JsonArray() },
                new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = "f", ["input"] = new JsonObject() },
            ]);
            turns.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray { new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = "f", ["input"] = new JsonObject() } },
            });
            turns.Add(ToolResult(id));
        }

        var messages = Messages(AnthropicRequestHandler.Rewrite(Body([.. turns]), false, false, nativeStore: store));

        var replayed = messages.Where(m => (string?)m!["role"] == "assistant")
            .Select(m => ((JsonArray)m!["content"]!).Any(b => Type(b) == "web_search_tool_result"))
            .ToList();
        Assert.Equal([false, false, .. Enumerable.Repeat(true, AnthropicRequestHandler.MaxReplayedMessages)], replayed);
    }

    [Fact]
    public void ADuplicateToolUseId_KeepsOnlyItsLastOccurrence()
    {
        var id = (string)ToolUse["id"]!;
        var paused = AdapterAssistant(text: "", withThinking: false);
        var pausedResult = ToolResult(id);
        pausedResult["content"]![0]!["content"] = "Paused: waiting for approval";

        var messages = Messages(AnthropicRequestHandler.Rewrite(
            Body(paused, pausedResult, AdapterAssistant(text: "", withThinking: false), ToolResult(id)),
            false, false, nativeStore: new AnthropicNativeStore()));

        Assert.Equal(3, messages.Count);
        Assert.DoesNotContain("Paused:", messages.ToJsonString(), StringComparison.Ordinal);
    }

    // A pending call's result would open the next response; replayed alone it is a 400.
    [Fact]
    public void APendingSearchCall_IsNotReplayed()
    {
        var store = new AnthropicNativeStore();
        store.TryAdd(
        [
            new JsonObject { ["type"] = "server_tool_use", ["id"] = "srv_done", ["name"] = "web_search", ["input"] = new JsonObject() },
            new JsonObject { ["type"] = "web_search_tool_result", ["tool_use_id"] = "srv_done", ["content"] = new JsonArray() },
            new JsonObject { ["type"] = "server_tool_use", ["id"] = "srv_pending", ["name"] = "web_search", ["input"] = new JsonObject() },
            new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_x", ["name"] = "f", ["input"] = new JsonObject() },
        ]);
        var assistant = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = new JsonArray { new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_x", ["name"] = "f", ["input"] = new JsonObject() } },
        };

        var content = (JsonArray)Messages(AnthropicRequestHandler.Rewrite(
            Body(assistant, ToolResult("toolu_x")), false, false, nativeStore: store))[1]!["content"]!;

        Assert.DoesNotContain(content, b => (string?)b!["id"] == "srv_pending");
        Assert.Contains(content, b => (string?)b!["id"] == "srv_done");
    }

    // Compaction keeps a pair whole but may drop one of a round's parallel calls with its result.
    [Fact]
    public void AStoredCallTheRequestNoLongerHolds_IsNotReplayed()
    {
        var store = new AnthropicNativeStore();
        store.TryAdd(
        [
            new JsonObject { ["type"] = "server_tool_use", ["id"] = "srv_1", ["name"] = "web_search", ["input"] = new JsonObject() },
            new JsonObject { ["type"] = "web_search_tool_result", ["tool_use_id"] = "srv_1", ["content"] = new JsonArray() },
            new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_kept", ["name"] = "f", ["input"] = new JsonObject() },
            new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_dropped", ["name"] = "f", ["input"] = new JsonObject() },
        ]);
        var assistant = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = new JsonArray { new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_kept", ["name"] = "f", ["input"] = new JsonObject() } },
        };

        var content = (JsonArray)Messages(AnthropicRequestHandler.Rewrite(
            Body(assistant, ToolResult("toolu_kept")), false, false, nativeStore: store))[1]!["content"]!;

        Assert.DoesNotContain(content, b => (string?)b!["id"] == "toolu_dropped");
        Assert.Contains(content, b => (string?)b!["id"] == "toolu_kept");
    }

    /// <summary>The reported defect end to end: round 2 of a real SDK loop carries round 1's searches and citations.</summary>
    [Fact]
    public async Task ATwoRoundLoopThroughTheSdk_SendsRoundOnesSearchesBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new ScriptedTransport(
            (AnthropicNativeAccumulatorTests.Fixture("anthropic-two-search-stream.sse"), "text/event-stream"),
            ("""{"id":"msg_2","type":"message","role":"assistant","model":"claude-sonnet-5-5","content":[{"type":"text","text":"done"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}""", "application/json"));
        var store = new AnthropicNativeStore();
        var http = new HttpClient(new AnthropicRequestHandler(false, false, null, false, store) { InnerHandler = transport });
        var client = new AnthropicClient { ApiKey = "test", HttpClient = http }.AsIChatClient("claude-sonnet-5-5");
        var options = new ChatOptions { MaxOutputTokens = 1024, Tools = [AIFunctionFactory.Create((string text) => "saved", "save_note")] };
        List<ChatMessage> history = [new(ChatRole.User, "go")];

        var round1 = await client.GetStreamingResponseAsync(history, options, ct).ToChatResponseAsync(ct);
        history.AddRange(round1.Messages);
        var call = round1.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Single();
        history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, "saved")]));
        await client.GetResponseAsync(history, options, ct);

        Assert.Equal(1, store.Count);
        var sent = (JsonArray)JsonNode.Parse(transport.Bodies[1])!["messages"]!;
        var assistant = (JsonArray)sent.Single(m => (string?)m!["role"] == "assistant")!["content"]!;
        var streamed = AnthropicNativeAccumulatorTests.Accumulate(AnthropicNativeAccumulatorTests.Fixture("anthropic-two-search-stream.sse")).Blocks;
        Assert.Equal(Json(streamed), Json(assistant));
    }

    private sealed class ScriptedTransport(params (string Body, string MediaType)[] responses) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var (body, mediaType) = responses[Bodies.Count - 1];
            var content = new StringContent(body, Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}
