using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Pins the <c>metadata.pia_persona_type</c> wire contract: present with the persona's model type when
/// one is set, omitted ENTIRELY when it is null (no empty <c>metadata</c> object on the wire — the
/// server's blank-fall-through contract is "key absent", not "key empty").
/// </summary>
public class PiaCloudChatClientModelTypeTests
{
    private static PiaCloudChatClient CreateClient(CapturingHandler handler, string? modelType)
        => new(
            new HttpClient(handler), "https://cloud.pia", (_, _) => Task.FromResult<string?>("token"),
            NullLogger.Instance, mode: "Assistant", managedPersonaId: null, modelType: modelType);

    [Fact]
    public async Task NonStreaming_WithModelType_SendsPersonaTypeMetadata()
    {
        var handler = new CapturingHandler();
        var client = CreateClient(handler, modelType: "fast");

        await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            cancellationToken: TestContext.Current.CancellationToken);

        var body = JsonNode.Parse(handler.Body!)!.AsObject();
        Assert.Equal("fast", body["metadata"]?["pia_persona_type"]?.GetValue<string>());
    }

    [Fact]
    public async Task NonStreaming_WithoutModelType_OmitsMetadataEntirely()
    {
        var handler = new CapturingHandler();
        var client = CreateClient(handler, modelType: null);

        await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            cancellationToken: TestContext.Current.CancellationToken);

        var body = JsonNode.Parse(handler.Body!)!.AsObject();
        Assert.False(body.ContainsKey("metadata"));
    }

    [Fact]
    public async Task Streaming_WithModelType_SendsPersonaTypeMetadata()
    {
        // Interactive chat streams, so the streaming body must carry the key too.
        var handler = new CapturingHandler(
            "data: {\"choices\":[{\"delta\":{\"content\":\"Answer\"}}]}\n\n" + "data: [DONE]\n\n",
            "text/event-stream");
        var client = CreateClient(handler, modelType: "code");

        await foreach (var _ in client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            cancellationToken: TestContext.Current.CancellationToken))
        {
        }

        var body = JsonNode.Parse(handler.Body!)!.AsObject();
        Assert.Equal("code", body["metadata"]?["pia_persona_type"]?.GetValue<string>());
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _responseBody;
        private readonly string _mediaType;

        public CapturingHandler(
            string responseBody = """{"message":{"role":"assistant","content":"Answer"},"model":"m"}""",
            string mediaType = "application/json")
        {
            _responseBody = responseBody;
            _mediaType = mediaType;
        }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var content = new StringContent(_responseBody, Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_mediaType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}
