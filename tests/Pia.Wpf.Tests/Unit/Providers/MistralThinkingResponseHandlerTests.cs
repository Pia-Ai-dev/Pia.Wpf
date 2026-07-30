using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Pia.Services.Providers.Http;
using Xunit;

namespace Pia.Wpf.Tests.Unit.Providers;

public class MistralThinkingResponseHandlerTests
{
    private const string ResponseWithThinking = """
        {
          "choices": [{
            "message": {
              "role": "assistant",
              "content": [
                {"type":"thinking","thinking":"let me reason"},
                {"type":"text","text":"ok"}
              ]
            }
          }]
        }
        """;

    private const string ResponseWithoutThinking = """
        {
          "choices": [{
            "message": {
              "role": "assistant",
              "content": "ok"
            }
          }]
        }
        """;

    [Fact]
    public void RewriteThinkingParts_ConvertsThinkingToInlineThinkTag()
    {
        var result = MistralThinkingResponseHandler.RewriteThinkingParts(ResponseWithThinking);

        Assert.NotNull(result);
        var parts = JsonNode.Parse(result!)!["choices"]![0]!["message"]!["content"]!.AsArray();
        Assert.Equal(2, parts.Count);
        // Thinking part became a normal text part wrapped in <think>…</think>.
        Assert.Equal("text", parts[0]!["type"]!.GetValue<string>());
        Assert.Equal("<think>let me reason</think>", parts[0]!["text"]!.GetValue<string>());
        Assert.Equal("ok", parts[1]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void RewriteThinkingParts_FlattensArrayShapedThinking()
    {
        var body = """
            {"choices":[{"message":{"content":[
                {"type":"thinking","thinking":[{"type":"text","text":"a"},{"type":"text","text":"b"}]},
                {"type":"text","text":"answer"}
            ]}}]}
            """;

        var result = MistralThinkingResponseHandler.RewriteThinkingParts(body);

        Assert.NotNull(result);
        var parts = JsonNode.Parse(result!)!["choices"]![0]!["message"]!["content"]!.AsArray();
        Assert.Equal("<think>ab</think>", parts[0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void RewriteThinkingParts_HandlesMultipleThinkingParts()
    {
        var body = """
            {"choices":[{"message":{"content":[
                {"type":"thinking","thinking":"first"},
                {"type":"thinking","thinking":"second"},
                {"type":"text","text":"answer"}
            ]}}]}
            """;

        var result = MistralThinkingResponseHandler.RewriteThinkingParts(body);

        Assert.NotNull(result);
        Assert.Contains("<think>first</think>", result);
        Assert.Contains("<think>second</think>", result);
        Assert.Contains("answer", result);
        Assert.DoesNotContain("\"thinking\"", result);
    }

    [Fact]
    public void RewriteThinkingParts_ReturnsNull_WhenNoThinkingPresent()
    {
        Assert.Null(MistralThinkingResponseHandler.RewriteThinkingParts(ResponseWithoutThinking));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    public void RewriteThinkingParts_ReturnsNull_ForEmptyOrInvalid(string body)
    {
        Assert.Null(MistralThinkingResponseHandler.RewriteThinkingParts(body));
    }

    [Fact]
    public void RewriteStreamLine_ConvertsDeltaThinking()
    {
        var line = """data: {"choices":[{"delta":{"content":[{"type":"thinking","thinking":"mid"}]}}]}""";

        var result = MistralThinkingResponseHandler.RewriteStreamLine(line);

        Assert.StartsWith("data: ", result);
        Assert.Contains("<think>mid</think>", result);
        Assert.DoesNotContain("\"thinking\"", result);
    }

    [Theory]
    [InlineData("data: [DONE]")]
    [InlineData(": comment")]
    [InlineData("")]
    [InlineData("""data: {"choices":[{"delta":{"content":"plain"}}]}""")]
    public void RewriteStreamLine_PassesThroughUnaffectedLines(string line)
    {
        Assert.Equal(line, MistralThinkingResponseHandler.RewriteStreamLine(line));
    }

    [Fact]
    public async Task SendAsync_RewritesBufferedJsonResponse()
    {
        var handler = new MistralThinkingResponseHandler
        {
            InnerHandler = new StubHandler(ResponseWithThinking, "application/json"),
        };
        var client = new HttpClient(handler);

        var response = await client.GetAsync("https://api.mistral.ai/v1/chat/completions", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("<think>let me reason</think>", body);
        Assert.DoesNotContain("\"thinking\"", body);
    }

    [Fact]
    public async Task SendAsync_TransformsStreamingResponse_AndPreservesFraming()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":[{\"type\":\"thinking\",\"thinking\":\"hmm\"}]}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new MistralThinkingResponseHandler
        {
            InnerHandler = new StubHandler(sse, "text/event-stream"),
        };
        var client = new HttpClient(handler);

        var response = await client.GetAsync(
            "https://api.mistral.ai/v1/chat/completions", HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("<think>hmm</think>", body);
        Assert.DoesNotContain("\"thinking\"", body);
        // Visible content and the terminal sentinel survive, and event framing is intact.
        Assert.Contains("Hello", body);
        Assert.Contains("data: [DONE]", body);
        Assert.Contains("\n\n", body);
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
