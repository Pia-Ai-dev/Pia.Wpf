using System.Net;
using System.Net.Http;
using System.Text;
using Pia.Services.Providers.Http;
using Xunit;

namespace Pia.Wpf.Tests.Unit.Providers;

public class MistralAgentsHandlerTests
{
    [Fact]
    public void Rewrite_ReplacesModelWithAgentId()
    {
        var body = """{"model":"mistral-small-latest","messages":[]}""";
        var result = MistralAgentsHandler.Rewrite(body, "ag:test:123");

        Assert.NotNull(result);
        Assert.Contains("\"agent_id\":\"ag:test:123\"", result);
        Assert.DoesNotContain("\"model\"", result);
    }

    [Fact]
    public void Rewrite_KeepsMessagesAndOtherFields()
    {
        var body = """{"model":"x","messages":[{"role":"user","content":"hi"}],"stream":true,"tools":[]}""";
        var result = MistralAgentsHandler.Rewrite(body, "ag:abc");

        Assert.NotNull(result);
        Assert.Contains("\"messages\"", result);
        Assert.Contains("\"stream\"", result);
        Assert.Contains("\"tools\"", result);
        Assert.Contains("\"agent_id\":\"ag:abc\"", result);
    }

    [Fact]
    public void Rewrite_ReturnsNullForEmptyBody()
    {
        Assert.Null(MistralAgentsHandler.Rewrite(string.Empty, "ag:123"));
    }

    [Fact]
    public void Rewrite_ReturnsNullForInvalidJson()
    {
        Assert.Null(MistralAgentsHandler.Rewrite("not json", "ag:123"));
    }

    [Fact]
    public void Rewrite_StripsSystemMessages()
    {
        var body = """{"model":"x","messages":[{"role":"system","content":"think"},{"role":"user","content":"hi"}]}""";
        var result = MistralAgentsHandler.Rewrite(body, "ag:abc");

        Assert.NotNull(result);
        Assert.DoesNotContain("\"system\"", result);
        Assert.Contains("\"user\"", result);
    }

    [Fact]
    public void Rewrite_PreservesNonSystemMessages()
    {
        var body = """{"model":"x","messages":[{"role":"user","content":"hi"},{"role":"assistant","content":"hello"},{"role":"tool","content":"result","tool_call_id":"1"}]}""";
        var result = MistralAgentsHandler.Rewrite(body, "ag:abc");

        Assert.NotNull(result);
        Assert.Contains("\"user\"", result);
        Assert.Contains("\"assistant\"", result);
        Assert.Contains("\"tool\"", result);
    }

    // HTTP-level tests that verify what actually reaches the inner handler

    [Theory]
    [InlineData("https://api.mistral.ai/v1/chat/completions")]  // SDK with /v1 in endpoint path
    [InlineData("https://api.mistral.ai/chat/completions")]     // SDK without /v1 in path
    public async Task SendAsync_RewritesUrl_ToAgentsCompletions(string inputUrl)
    {
        var captured = new CapturingHandler();
        var handler = new MistralAgentsHandler("ag:proj:model:abc") { InnerHandler = captured };
        var client = new HttpClient(handler);

        var body = """{"model":"mistral-small","messages":[{"role":"user","content":"hi"}]}""";
        await client.PostAsync(inputUrl, new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal("/v1/agents/completions", captured.LastRequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task SendAsync_RewritesBody_EvenWithoutContentTypeHeader()
    {
        var captured = new CapturingHandler();
        var handler = new MistralAgentsHandler("ag:proj:model:abc") { InnerHandler = captured };
        var client = new HttpClient(handler);

        // ByteArrayContent has no Content-Type by default — simulates SDK pipeline content
        var body = """{"model":"mistral-small","messages":[{"role":"user","content":"hi"}]}""";
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        await client.PostAsync("https://api.mistral.ai/v1/chat/completions", content);

        Assert.NotNull(captured.LastBody);
        Assert.Contains("agent_id", captured.LastBody);
        Assert.DoesNotContain("\"model\"", captured.LastBody);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
