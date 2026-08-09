using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

public class PiaCloudChatClientReasoningTests
{
    private static PiaCloudChatClient CreateClient(string body, string mediaType)
    {
        var http = new HttpClient(new StubHandler(body, mediaType));
        return new PiaCloudChatClient(
            http, "https://cloud.pia", (_, _) => Task.FromResult<string?>("token"), NullLogger.Instance);
    }

    [Theory]
    [InlineData("reasoning")]
    [InlineData("reasoning_content")]
    public async Task Streaming_SurfacesReasoning_AsTextReasoningContent(string field)
    {
        var sse =
            $"data: {{\"choices\":[{{\"delta\":{{\"{field}\":\"server thinking\"}}}}]}}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"Answer\"}}]}\n\n" +
            "data: [DONE]\n\n";
        var client = CreateClient(sse, "text/event-stream");

        var reasoning = new List<string>();
        var text = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") }, cancellationToken: TestContext.Current.CancellationToken))
        {
            reasoning.AddRange(update.Contents.OfType<TextReasoningContent>().Select(r => r.Text));
            text.AddRange(update.Contents.OfType<TextContent>().Select(t => t.Text));
        }

        Assert.Contains("server thinking", reasoning);
        Assert.Contains("Answer", text);
    }

    [Fact]
    public async Task NonStreaming_SurfacesReasoning_AsTextReasoningContent()
    {
        var json = """{"message":{"role":"assistant","content":"Answer","reasoning":"server thinking"},"model":"m"}""";
        var client = CreateClient(json, "application/json");

        var response = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }, cancellationToken: TestContext.Current.CancellationToken);

        var reasoning = response.Messages.SelectMany(m => m.Contents).OfType<TextReasoningContent>().Select(r => r.Text);
        Assert.Contains("server thinking", reasoning);
        Assert.Equal("Answer", response.Text);
    }

    [Fact]
    public async Task NonStreaming_ParsesOpenAiEnvelope_FromChoicesArray()
    {
        // The real Pia.Server non-streaming chat endpoint returns the raw upstream OpenAI-compatible
        // envelope (choices[0].message / finish_reason — AiProxyEndpoints "Return raw upstream JSON").
        // Interactive chat streams, so only SendRequestAsync callers (e.g. ingest) hit this path.
        var json = """{"model":"gpt-x","choices":[{"message":{"role":"assistant","content":"Answer"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":5,"total_tokens":8}}""";
        var client = CreateClient(json, "application/json");

        var response = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Answer", response.Text);
        Assert.Equal("gpt-x", response.ModelId);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal(8, response.Usage!.TotalTokenCount);
    }

    [Fact]
    public async Task Streaming_ContentAsArrayOfTextParts_IsConcatenated()
    {
        // Some providers (e.g. Mistral reasoning chunks) emit delta.content as an array of
        // typed parts instead of a string. The client must concatenate the text parts rather
        // than throw "The node must be of type 'JsonValue'".
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":[{\"type\":\"text\",\"text\":\"Hello \"},{\"type\":\"text\",\"text\":\"world\"}]}}]}\n\n" +
            "data: [DONE]\n\n";
        var client = CreateClient(sse, "text/event-stream");

        var text = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") }, cancellationToken: TestContext.Current.CancellationToken))
        {
            text.AddRange(update.Contents.OfType<TextContent>().Select(t => t.Text));
        }

        Assert.Equal("Hello world", string.Concat(text));
    }

    [Fact]
    public async Task Streaming_ContentAsUnknownShape_IsIgnored()
    {
        // An object-shaped content node must not throw; it is simply ignored.
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":{\"unexpected\":\"object\"}}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"Answer\"}}]}\n\n" +
            "data: [DONE]\n\n";
        var client = CreateClient(sse, "text/event-stream");

        var text = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") }, cancellationToken: TestContext.Current.CancellationToken))
        {
            text.AddRange(update.Contents.OfType<TextContent>().Select(t => t.Text));
        }

        Assert.Equal("Answer", string.Concat(text));
    }

    [Fact]
    public async Task Streaming_WithoutReasoning_YieldsNoReasoningContent()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"Just an answer\"}}]}\n\n" +
            "data: [DONE]\n\n";
        var client = CreateClient(sse, "text/event-stream");

        var reasoning = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") }, cancellationToken: TestContext.Current.CancellationToken))
        {
            reasoning.AddRange(update.Contents.OfType<TextReasoningContent>().Select(r => r.Text));
        }

        Assert.Empty(reasoning);
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
