using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Pins the guardrail protected-route marker across the real HTTP seam. GuardrailMarkerTests covers the
/// JSON predicate alone; these drive the client's own SSE loop and envelope parse with the exact bytes
/// Pia.Server emits (GuardrailResponseMarker.StreamChunk / InjectIntoEnvelope).
/// </summary>
public class PiaCloudChatClientGuardrailMarkerTests
{
    /// <summary>Byte-for-byte GuardrailResponseMarker.StreamChunk; the server emits it before the answer.</summary>
    private const string MarkerChunk = "data: {\"guardrail\":{\"protected\":true}}\n\n";

    private const string AnswerChunk = "data: {\"choices\":[{\"delta\":{\"content\":\"Answer\"}}]}\n\n";
    private const string DoneChunk = "data: [DONE]\n\n";

    private static PiaCloudChatClient CreateClient(string body, string mediaType)
    {
        var http = new HttpClient(new StubHandler(body, mediaType));
        return new PiaCloudChatClient(
            http, "https://cloud.pia", (_, _) => Task.FromResult<string?>("token"), NullLogger.Instance);
    }

    private static async Task<(bool Marked, string Text)> StreamAsync(string sse)
    {
        var client = CreateClient(sse, "text/event-stream");
        var marked = false;
        var text = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") }, cancellationToken: TestContext.Current.CancellationToken))
        {
            if (update.AdditionalProperties?.ContainsKey(GuardrailMarker.AdditionalPropertyKey) == true)
                marked = true;
            text.AddRange(update.Contents.OfType<TextContent>().Select(t => t.Text));
        }
        return (marked, string.Concat(text));
    }

    [Fact]
    public async Task Streaming_LeadingMarkerChunk_IsSurfaced_AndDoesNotDisturbTheAnswer()
    {
        var (marked, text) = await StreamAsync(MarkerChunk + AnswerChunk + DoneChunk);

        Assert.True(marked);
        Assert.Equal("Answer", text);
    }

    [Fact]
    public async Task Streaming_WithoutMarker_IsNotMarked()
    {
        var (marked, text) = await StreamAsync(AnswerChunk + DoneChunk);

        Assert.False(marked);
        Assert.Equal("Answer", text);
    }

    [Fact]
    public async Task Streaming_MarkerWithProtectedFalse_IsNotMarked()
    {
        var (marked, _) = await StreamAsync(
            "data: {\"guardrail\":{\"protected\":false}}\n\n" + AnswerChunk + DoneChunk);

        Assert.False(marked);
    }

    [Fact]
    public async Task NonStreaming_GuardrailRootField_IsSurfaced()
    {
        // The envelope InjectIntoEnvelope produces: the marker as a sibling of choices/usage.
        var json = """
            {"model":"m","choices":[{"message":{"role":"assistant","content":"Answer"},"finish_reason":"stop"}],"guardrail":{"protected":true}}
            """;
        var client = CreateClient(json, "application/json");

        var response = await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(response.AdditionalProperties?.ContainsKey(GuardrailMarker.AdditionalPropertyKey));
        Assert.Equal("Answer", response.Text);
    }

    [Fact]
    public async Task NonStreaming_WithoutMarker_IsNotMarked()
    {
        var json = """
            {"model":"m","choices":[{"message":{"role":"assistant","content":"Answer"},"finish_reason":"stop"}]}
            """;
        var client = CreateClient(json, "application/json");

        var response = await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(
            response.AdditionalProperties is null
            || !response.AdditionalProperties.ContainsKey(GuardrailMarker.AdditionalPropertyKey));
        Assert.Equal("Answer", response.Text);
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
