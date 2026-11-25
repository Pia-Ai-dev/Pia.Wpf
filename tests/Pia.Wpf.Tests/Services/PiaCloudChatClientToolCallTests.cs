using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

public class PiaCloudChatClientToolCallTests
{
    private static PiaCloudChatClient CreateClient(string body, string mediaType)
    {
        var http = new HttpClient(new StubHandler(body, mediaType));
        return new PiaCloudChatClient(
            http, "https://cloud.pia", (_, _) => Task.FromResult<string?>("token"), NullLogger.Instance);
    }

    private static async Task<List<FunctionCallContent>> CollectToolCallsAsync(string sse)
    {
        var client = CreateClient(sse, "text/event-stream");
        var calls = new List<FunctionCallContent>();
        await foreach (var update in client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") }, cancellationToken: TestContext.Current.CancellationToken))
        {
            calls.AddRange(update.Contents.OfType<FunctionCallContent>());
        }
        return calls;
    }

    private static string Path(FunctionCallContent call) => (string)call.Arguments!["path"]!.ToString()!;

    [Fact]
    public async Task Streaming_ParallelToolCalls_WithoutIndex_StayApart()
    {
        // A provider that omits `index` on parallel calls: keying accumulation on index alone folded
        // every call into one, concatenating the argument objects into unparseable JSON.
        var sse =
            """data: {"choices":[{"delta":{"tool_calls":[{"id":"call_a","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"Calculator.cs\"}"}}]}}]}""" + "\n\n" +
            """data: {"choices":[{"delta":{"tool_calls":[{"id":"call_b","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"Program.cs\"}"}}]}}]}""" + "\n\n" +
            """data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""" + "\n\n" +
            "data: [DONE]\n\n";

        var calls = await CollectToolCallsAsync(sse);

        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.Equal("read_file", c.Name));
        Assert.All(calls, c => Assert.Null(c.Exception));
        Assert.Equal(["Calculator.cs", "Program.cs"], calls.Select(Path));
        Assert.Equal(["call_a", "call_b"], calls.Select(c => c.CallId));
    }

    [Fact]
    public async Task Streaming_ParallelToolCalls_ReusingIndexZero_StayApart()
    {
        // The observed shape: every call arrives as index 0 with its own id, and continuation deltas
        // carry only the index — so the index lookup must resolve to the call it most recently opened.
        var sse =
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_a","function":{"name":"read_file","arguments":"{\"path\":\"A"}}]}}]}""" + "\n\n" +
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":".cs\"}"}}]}}]}""" + "\n\n" +
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_b","function":{"name":"list_files","arguments":"{}"}}]}}]}""" + "\n\n" +
            """data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""" + "\n\n" +
            "data: [DONE]\n\n";

        var calls = await CollectToolCallsAsync(sse);

        Assert.Equal(2, calls.Count);
        Assert.Equal("read_file", calls[0].Name);
        Assert.Equal("A.cs", Path(calls[0]));
        Assert.Equal("list_files", calls[1].Name);
        Assert.Empty(calls[1].Arguments!);
    }

    [Fact]
    public async Task Streaming_SpecCompliantIndexes_AccumulateArgumentFragments()
    {
        var sse =
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_a","function":{"name":"read_file","arguments":""}}]}}]}""" + "\n\n" +
            """data: {"choices":[{"delta":{"tool_calls":[{"index":1,"id":"call_b","function":{"name":"write_file","arguments":""}}]}}]}""" + "\n\n" +
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"path\":\"A.cs\"}"}}]}}]}""" + "\n\n" +
            """data: {"choices":[{"delta":{"tool_calls":[{"index":1,"function":{"arguments":"{\"path\":\"B.cs\"}"}}]}}]}""" + "\n\n" +
            """data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""" + "\n\n" +
            "data: [DONE]\n\n";

        var calls = await CollectToolCallsAsync(sse);

        Assert.Equal(2, calls.Count);
        Assert.Equal("read_file", calls[0].Name);
        Assert.Equal("A.cs", Path(calls[0]));
        Assert.Equal("write_file", calls[1].Name);
        Assert.Equal("B.cs", Path(calls[1]));
    }

    [Fact]
    public async Task Streaming_ParallelToolCalls_WithoutIdOrIndex_SplitOnNameChange()
    {
        var sse =
            """data: {"choices":[{"delta":{"tool_calls":[{"function":{"name":"read_file","arguments":"{\"path\":\"A.cs\"}"}}]}}]}""" + "\n\n" +
            """data: {"choices":[{"delta":{"tool_calls":[{"function":{"name":"list_files","arguments":"{}"}}]}}]}""" + "\n\n" +
            """data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""" + "\n\n" +
            "data: [DONE]\n\n";

        var calls = await CollectToolCallsAsync(sse);

        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.Null(c.Exception));
        Assert.Equal("read_file", calls[0].Name);
        Assert.Equal("A.cs", Path(calls[0]));
        Assert.Equal("list_files", calls[1].Name);
        // A wire that names no call ids still has to yield distinct ones: two tool_calls sharing an
        // empty id, each with its own result, is a request providers answer with 400.
        Assert.Distinct(calls.Select(c => c.CallId).ToList());
    }

    [Fact]
    public async Task Streaming_UnparseableArguments_SurfaceAsExceptionNotSubstituteArguments()
    {
        var sse =
            """data: {"choices":[{"delta":{"tool_calls":[{"id":"call_a","function":{"name":"read_file","arguments":"{\"path\":\"A.cs\"}{\"path\":\"B.cs\"}"}}]}}]}""" + "\n\n" +
            """data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""" + "\n\n" +
            "data: [DONE]\n\n";

        var calls = await CollectToolCallsAsync(sse);

        var call = Assert.Single(calls);
        Assert.NotNull(call.Exception);
        Assert.Null(call.Arguments);
    }

    [Fact]
    public async Task NonStreaming_UnparseableArguments_SurfaceAsException()
    {
        var json =
            """{"model":"m","choices":[{"message":{"role":"assistant","tool_calls":[{"id":"call_a","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"A.cs\"}{\"path\":\"B.cs\"}"}}]},"finish_reason":"tool_calls"}]}""";
        var client = CreateClient(json, "application/json");

        var response = await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") }, cancellationToken: TestContext.Current.CancellationToken);

        var call = Assert.Single(response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        Assert.NotNull(call.Exception);
        Assert.Null(call.Arguments);
    }

    [Fact]
    public async Task NonStreaming_ParallelToolCalls_AreKeptSeparate()
    {
        var json =
            """{"model":"m","choices":[{"message":{"role":"assistant","tool_calls":[{"id":"call_a","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"A.cs\"}"}},{"id":"call_b","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"B.cs\"}"}}]},"finish_reason":"tool_calls"}]}""";
        var client = CreateClient(json, "application/json");

        var response = await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") }, cancellationToken: TestContext.Current.CancellationToken);

        var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal(["A.cs", "B.cs"], calls.Select(Path));
    }

    // The server's native-content store matches on the toolu_ id alone, so round 2 must send it back byte-for-byte.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnAnthropicId_GoesBackVerbatim_InTheNextRoundsRequest(bool streaming)
    {
        const string first = "toolu_01AbC-dEf_9";
        const string second = "toolu_01XyZ";
        var round1 = streaming
            ? """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"toolu_01AbC-dEf_9","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"A.cs\"}"}},{"index":1,"id":"toolu_01XyZ","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"B.cs\"}"}}]}}]}""" + "\n\n" +
              """data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""" + "\n\n" +
              "data: [DONE]\n\n"
            : """{"model":"m","choices":[{"message":{"role":"assistant","tool_calls":[{"id":"toolu_01AbC-dEf_9","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"A.cs\"}"}},{"id":"toolu_01XyZ","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"B.cs\"}"}}]},"finish_reason":"tool_calls"}]}""";
        const string round2 = """{"model":"m","choices":[{"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}]}""";
        var handler = new SequenceHandler((round1, streaming ? "text/event-stream" : "application/json"), (round2, "application/json"));
        var client = new PiaCloudChatClient(
            new HttpClient(handler), "https://cloud.pia", (_, _) => Task.FromResult<string?>("token"), NullLogger.Instance);
        var ct = TestContext.Current.CancellationToken;
        List<ChatMessage> history = [new(ChatRole.User, "hi")];

        var response = streaming
            ? await client.GetStreamingResponseAsync(history, cancellationToken: ct).ToChatResponseAsync(ct)
            : await client.GetResponseAsync(history, cancellationToken: ct);
        history.AddRange(response.Messages);
        history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(first, "a")]));
        history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(second, "b")]));
        await client.GetResponseAsync(history, cancellationToken: ct);

        var messages = (JsonArray)JsonNode.Parse(handler.Bodies[1])!["messages"]!;
        var assistant = messages.Single(m => (string?)m!["role"] == "assistant")!;
        Assert.Equal([first, second], ((JsonArray)assistant["tool_calls"]!).Select(c => (string?)c!["id"]));
        Assert.Equal([first, second],
            messages.Where(m => (string?)m!["role"] == "tool").Select(m => (string?)m!["tool_call_id"]));
    }

    private sealed class SequenceHandler(params (string Body, string MediaType)[] responses) : HttpMessageHandler
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

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly string _mediaType;

        public StubHandler(string body, string mediaType)
        {
            _body = body;
            _mediaType = mediaType;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StringContent(_body, Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_mediaType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
