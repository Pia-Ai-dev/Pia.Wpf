using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services;
using Pia.Services.Exceptions;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Pia.Server relays an upstream failure inside an already-open 200 stream as a choiceless
/// <c>data: {"error":…,"message":…}</c> chunk followed by <c>[DONE]</c>. Until this seam threw, that chunk
/// was skipped like any other choiceless line and the turn ended as an empty answer with no trace of why.
/// </summary>
public class PiaCloudChatClientStreamErrorTests
{
    private const string MarkerChunk = "data: {\"guardrail\":{\"protected\":true}}\n\n";
    private const string AnswerChunk = "data: {\"choices\":[{\"delta\":{\"content\":\"Answer\"}}]}\n\n";
    private const string DoneChunk = "data: [DONE]\n\n";

    /// <summary>Byte-for-byte what ChatStreamService writes when the upstream deadline expires.</summary>
    private const string TimeoutChunk =
        "data: {\"error\":\"Bad Gateway\",\"message\":\"Request to upstream AI provider timed out.\"}\n\n";

    [Fact]
    public async Task ErrorChunk_Throws_WithTheServerMessageVerbatim()
    {
        var ex = await Assert.ThrowsAsync<PiaCloudStreamException>(() => StreamAsync(TimeoutChunk + DoneChunk));

        Assert.Equal("Request to upstream AI provider timed out.", ex.Message);
        Assert.Equal("Bad Gateway", ex.Title);
    }

    [Fact]
    public async Task ErrorChunk_AfterTheGuardrailMarker_StillThrows()
    {
        await Assert.ThrowsAsync<PiaCloudStreamException>(
            () => StreamAsync(MarkerChunk + TimeoutChunk + DoneChunk));
    }

    [Fact]
    public async Task ErrorChunk_WithoutAMessage_FallsBackToTheTitle()
    {
        var ex = await Assert.ThrowsAsync<PiaCloudStreamException>(
            () => StreamAsync("data: {\"error\":\"Upstream Error\"}\n\n" + DoneChunk));

        Assert.Equal("Upstream Error", ex.Message);
    }

    [Fact]
    public async Task UpstreamStyleErrorObject_IsSurfacedByItsMessage()
    {
        // OpenAI-compatible upstreams emit the error as an object; the proxy forwards such a chunk unchanged.
        var ex = await Assert.ThrowsAsync<PiaCloudStreamException>(() => StreamAsync(
            "data: {\"error\":{\"message\":\"model overloaded\",\"type\":\"server_error\"}}\n\n" + DoneChunk));

        Assert.Equal("model overloaded", ex.Message);
        Assert.Equal("server_error", ex.Title);
    }

    [Fact]
    public async Task AnOrdinaryAnswer_IsUnaffected()
    {
        Assert.Equal("Answer", await StreamAsync(AnswerChunk + DoneChunk));
    }

    /// <summary>A 400-shaped exception would trip AiClientService's tool-less retry in round 0, and an
    /// unattended routine would then re-run silently with no tools. The stream error must stay outside that arm.</summary>
    [Fact]
    public async Task TheException_IsNotAnHttpFailure()
    {
        var ex = await Assert.ThrowsAsync<PiaCloudStreamException>(() => StreamAsync(TimeoutChunk + DoneChunk));

        Assert.IsNotAssignableFrom<HttpRequestException>(ex);
    }

    private static async Task<string> StreamAsync(string sse)
    {
        var http = new HttpClient(new StubHandler(sse));
        var client = new PiaCloudChatClient(
            http, "https://cloud.pia", (_, _) => Task.FromResult<string?>("token"), NullLogger.Instance);

        var text = new StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") }, cancellationToken: TestContext.Current.CancellationToken))
        {
            foreach (var t in update.Contents.OfType<TextContent>())
                text.Append(t.Text);
        }
        return text.ToString();
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StringContent(body, Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
