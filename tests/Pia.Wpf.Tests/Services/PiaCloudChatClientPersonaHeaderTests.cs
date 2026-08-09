using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>The only client-side assertion on the <c>X-Pia-Persona</c> header name and value format, so both
/// are spelled out literally rather than derived.</summary>
public class PiaCloudChatClientPersonaHeaderTests
{
    // A literal string, so the "D" Guid format the server's Guid.TryParse expects is pinned too.
    private const string PersonaIdText = "6f1b3f2a-9c44-4d1e-8b77-2a0d5e91c4aa";

    private static readonly Guid PersonaId = Guid.Parse(PersonaIdText);

    private static PiaCloudChatClient CreateClient(CapturingHandler handler, string? mode, Guid? managedPersonaId)
        => new(
            new HttpClient(handler), "https://cloud.pia", (_, _) => Task.FromResult<string?>("token"),
            NullLogger.Instance, mode, managedPersonaId);

    [Fact]
    public async Task NonStreaming_WithPersona_SendsExactHeader()
    {
        var handler = new CapturingHandler();
        var client = CreateClient(handler, mode: null, managedPersonaId: PersonaId);

        await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(handler.Request!.Headers.TryGetValues("X-Pia-Persona", out var values));
        Assert.Equal(PersonaIdText, Assert.Single(values!));
    }

    [Fact]
    public async Task NonStreaming_WithoutPersona_OmitsHeaderEntirely()
    {
        var handler = new CapturingHandler();
        var client = CreateClient(handler, mode: null, managedPersonaId: null);

        await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            cancellationToken: TestContext.Current.CancellationToken);

        // Absent, not empty: an unparseable value would fail open to group-only scope server-side, but
        // sending one at all would be a contract violation the server could never distinguish from a bug.
        Assert.False(handler.Request!.Headers.Contains("X-Pia-Persona"));
    }

    [Fact]
    public async Task NonStreaming_WithModeAndPersona_SendsBoth()
    {
        var handler = new CapturingHandler();
        var client = CreateClient(handler, mode: "Assistant", managedPersonaId: PersonaId);

        await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(handler.Request!.Headers.TryGetValues("X-Pia-Mode", out var modes));
        Assert.Equal("Assistant", Assert.Single(modes!));
        Assert.True(handler.Request!.Headers.TryGetValues("X-Pia-Persona", out var personas));
        Assert.Equal(PersonaIdText, Assert.Single(personas!));
    }

    [Fact]
    public async Task Streaming_WithPersona_SendsExactHeader()
    {
        // Interactive chat streams, so the streaming path is the one that carries the header in practice.
        // Both paths share SendWithAuthRetryAsync — this asserts the sharing has not been broken.
        var handler = new CapturingHandler(
            "data: {\"choices\":[{\"delta\":{\"content\":\"Answer\"}}]}\n\n" + "data: [DONE]\n\n",
            "text/event-stream");
        var client = CreateClient(handler, mode: "Assistant", managedPersonaId: PersonaId);

        await foreach (var _ in client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            cancellationToken: TestContext.Current.CancellationToken))
        {
        }

        Assert.True(handler.Request!.Headers.TryGetValues("X-Pia-Persona", out var values));
        Assert.Equal(PersonaIdText, Assert.Single(values!));
    }

    /// <summary>Captures the outgoing request so the headers can be asserted after the call completes.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly string _mediaType;

        public CapturingHandler(
            string body = """{"message":{"role":"assistant","content":"Answer"},"model":"m"}""",
            string mediaType = "application/json")
        {
            _body = body;
            _mediaType = mediaType;
        }

        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            var content = new StringContent(_body, Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_mediaType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
