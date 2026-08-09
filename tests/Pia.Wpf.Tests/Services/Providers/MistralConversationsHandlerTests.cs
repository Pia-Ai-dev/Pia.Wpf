using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Pia.Services.Providers.Http;
using Xunit;

namespace Pia.Tests.Services.Providers;

public class MistralConversationsHandlerTests
{
    // ---- Request rewriting --------------------------------------------------

    [Fact]
    public void RewriteRequest_SingleUserMessage_CollapsesInputsToString()
    {
        var body = """{"model":"mistral-small-latest","messages":[{"role":"user","content":"hi"}],"stream":true}""";
        var result = MistralConversationsHandler.RewriteRequest(body, "ag:test:123");

        Assert.NotNull(result);
        var obj = JsonNode.Parse(result!) as JsonObject;
        Assert.NotNull(obj);
        Assert.Equal("ag:test:123", obj!["agent_id"]?.GetValue<string>());
        Assert.False(obj["stream"]?.GetValue<bool>() ?? true);
        Assert.False(obj["store"]?.GetValue<bool>() ?? true);
        Assert.Equal("hi", obj["inputs"]?.GetValue<string>());
        Assert.Null(obj["model"]);
        Assert.Null(obj["messages"]);
    }

    [Fact]
    public void RewriteRequest_StripsSystemMessages()
    {
        var body = """{"model":"x","messages":[{"role":"system","content":"sys"},{"role":"user","content":"hi"}]}""";
        var result = MistralConversationsHandler.RewriteRequest(body, "ag:abc");

        Assert.NotNull(result);
        var obj = JsonNode.Parse(result!) as JsonObject;
        Assert.Equal("hi", obj!["inputs"]?.GetValue<string>());
    }

    [Fact]
    public void RewriteRequest_MultiTurn_EmitsInputsArray()
    {
        var body = """
        {
          "model":"x",
          "messages":[
            {"role":"user","content":"first"},
            {"role":"assistant","content":"reply"},
            {"role":"user","content":"follow-up"}
          ]
        }
        """;
        var result = MistralConversationsHandler.RewriteRequest(body, "ag:abc");

        Assert.NotNull(result);
        var obj = JsonNode.Parse(result!) as JsonObject;
        var inputs = obj!["inputs"] as JsonArray;
        Assert.NotNull(inputs);
        Assert.Equal(3, inputs!.Count);
        Assert.Equal("user", inputs[0]!["role"]?.GetValue<string>());
        Assert.Equal("first", inputs[0]!["content"]?.GetValue<string>());
        Assert.Equal("assistant", inputs[1]!["role"]?.GetValue<string>());
        Assert.Equal("follow-up", inputs[2]!["content"]?.GetValue<string>());
    }

    [Fact]
    public void RewriteRequest_AlwaysForcesStreamFalse()
    {
        var body = """{"model":"x","messages":[{"role":"user","content":"hi"}],"stream":true}""";
        var result = MistralConversationsHandler.RewriteRequest(body, "ag:abc");

        var obj = JsonNode.Parse(result!) as JsonObject;
        Assert.False(obj!["stream"]?.GetValue<bool>() ?? true);
    }

    [Fact]
    public void RewriteRequest_ReturnsNullForEmptyBody()
    {
        Assert.Null(MistralConversationsHandler.RewriteRequest(string.Empty, "ag:123"));
    }

    [Fact]
    public void RewriteRequest_ReturnsNullForInvalidJson()
    {
        Assert.Null(MistralConversationsHandler.RewriteRequest("not json", "ag:123"));
    }

    // ---- Response transformation -------------------------------------------

    [Fact]
    public void TransformResponse_ExtractsTextFromMessageOutputContentChunks()
    {
        var body = """
        {
          "conversation_id": "conv_abc",
          "outputs": [
            {
              "type": "tool.execution",
              "name": "web_search",
              "id": "te_1"
            },
            {
              "type": "message.output",
              "role": "assistant",
              "model": "mistral-medium-latest",
              "content": [
                {"type":"text","text":"Hello "},
                {"type":"tool_reference","tool":"web_search","title":"Example","url":"https://example.org","source":"web_search"},
                {"type":"text","text":"world."}
              ]
            }
          ],
          "usage": {"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}
        }
        """;

        var result = MistralConversationsHandler.TransformResponse(body);
        Assert.NotNull(result);

        var obj = JsonNode.Parse(result!) as JsonObject;
        Assert.Equal("chat.completion", obj!["object"]?.GetValue<string>());
        Assert.Equal("conv_abc", obj["id"]?.GetValue<string>());
        Assert.Equal("mistral-medium-latest", obj["model"]?.GetValue<string>());

        var choices = obj["choices"] as JsonArray;
        Assert.NotNull(choices);
        Assert.Single(choices!);
        var msg = choices[0]!["message"] as JsonObject;
        Assert.Equal("assistant", msg!["role"]?.GetValue<string>());
        Assert.Equal("Hello world.", msg["content"]?.GetValue<string>());
        Assert.Equal("stop", choices[0]!["finish_reason"]?.GetValue<string>());

        Assert.Equal(5, obj["usage"]?["total_tokens"]?.GetValue<int>());
    }

    [Fact]
    public void TransformResponse_HandlesStringContent()
    {
        var body = """
        {
          "conversation_id":"c",
          "outputs":[{"type":"message.output","role":"assistant","content":"plain text"}]
        }
        """;
        var result = MistralConversationsHandler.TransformResponse(body);
        Assert.NotNull(result);

        var obj = JsonNode.Parse(result!) as JsonObject;
        var choices = obj!["choices"] as JsonArray;
        Assert.Equal("plain text", choices![0]!["message"]?["content"]?.GetValue<string>());
    }

    [Fact]
    public void TransformResponse_ReturnsNullForUnrecognisedShape()
    {
        // No `outputs` field — looks like a plain chat.completion already.
        var body = """{"choices":[{"message":{"role":"assistant","content":"hi"}}]}""";
        Assert.Null(MistralConversationsHandler.TransformResponse(body));
    }

    [Fact]
    public void TransformResponse_ReturnsNullForInvalidJson()
    {
        Assert.Null(MistralConversationsHandler.TransformResponse("not json"));
        Assert.Null(MistralConversationsHandler.TransformResponse(string.Empty));
    }

    // ---- HTTP-level (URL + body in/out) ------------------------------------

    [Theory]
    [InlineData("https://api.mistral.ai/v1/chat/completions")]
    [InlineData("https://api.mistral.ai/chat/completions")]
    public async Task SendAsync_RewritesUrlToConversations(string inputUrl)
    {
        var captured = new CapturingHandler();
        var handler = new MistralConversationsHandler("ag:proj:model:abc") { InnerHandler = captured };
        var client = new HttpClient(handler);

        var body = """{"model":"mistral-small","messages":[{"role":"user","content":"hi"}]}""";
        await client.PostAsync(inputUrl, new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        Assert.Equal("/v1/conversations", captured.LastRequestUri?.AbsolutePath);
        Assert.NotNull(captured.LastBody);
        var sentObj = JsonNode.Parse(captured.LastBody!) as JsonObject;
        Assert.Equal("ag:proj:model:abc", sentObj!["agent_id"]?.GetValue<string>());
        Assert.Equal("hi", sentObj["inputs"]?.GetValue<string>());
    }

    [Fact]
    public async Task SendAsync_TransformsResponseBodyOnTheWayBack()
    {
        var conversationsResponse = """
        {
          "conversation_id":"conv_1",
          "outputs":[{"type":"message.output","role":"assistant","model":"m","content":[{"type":"text","text":"ready"}]}]
        }
        """;
        var captured = new CapturingHandler(conversationsResponse);
        var handler = new MistralConversationsHandler("ag:1") { InnerHandler = captured };
        var client = new HttpClient(handler);

        var body = """{"model":"x","messages":[{"role":"user","content":"hi"}]}""";
        var response = await client.PostAsync(
            "https://api.mistral.ai/v1/chat/completions",
            new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        var returned = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var obj = JsonNode.Parse(returned) as JsonObject;
        Assert.Equal("chat.completion", obj!["object"]?.GetValue<string>());
        var choices = obj["choices"] as JsonArray;
        Assert.Equal("ready", choices![0]!["message"]?["content"]?.GetValue<string>());
    }

    [Fact]
    public async Task SendAsync_WhenClientRequestedStreaming_ReturnsSse()
    {
        var conversationsResponse = """
        {
          "conversation_id":"conv_1",
          "outputs":[{"type":"message.output","role":"assistant","model":"m","content":[{"type":"text","text":"ready"}]}]
        }
        """;
        var captured = new CapturingHandler(conversationsResponse);
        var handler = new MistralConversationsHandler("ag:1") { InnerHandler = captured };
        var client = new HttpClient(handler);

        var body = """{"model":"x","messages":[{"role":"user","content":"hi"}],"stream":true}""";
        var response = await client.PostAsync(
            "https://api.mistral.ai/v1/chat/completions",
            new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var sse = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("data: ", sse);
        Assert.Contains("chat.completion.chunk", sse);
        Assert.Contains("\"content\":\"ready\"", sse);
        Assert.Contains("data: [DONE]", sse);
    }

    [Fact]
    public void BuildSseFromChatCompletion_WrapsContentAsSingleChunk()
    {
        var json = """
        {
          "id":"x",
          "object":"chat.completion",
          "created":123,
          "model":"m",
          "choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}]
        }
        """;
        var sse = MistralConversationsHandler.BuildSseFromChatCompletion(json);
        Assert.StartsWith("data: ", sse);
        Assert.EndsWith("data: [DONE]\n\n", sse);
        Assert.Contains("\"content\":\"hello\"", sse);
        Assert.Contains("\"finish_reason\":\"stop\"", sse);
    }

    [Fact]
    public async Task SendAsync_LeavesNonChatCompletionsUrlsAlone()
    {
        var captured = new CapturingHandler();
        var handler = new MistralConversationsHandler("ag:1") { InnerHandler = captured };
        var client = new HttpClient(handler);

        await client.GetAsync("https://api.mistral.ai/v1/models", TestContext.Current.CancellationToken);

        Assert.Equal("/v1/models", captured.LastRequestUri?.AbsolutePath);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _responseBody;
        public Uri? LastRequestUri { get; private set; }
        public string? LastBody { get; private set; }

        public CapturingHandler(string responseBody = "{}")
        {
            _responseBody = responseBody;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
