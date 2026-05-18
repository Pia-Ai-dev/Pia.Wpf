using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pia.Models;
using Pia.Services.Providers.Http;
using Xunit;

namespace Pia.Wpf.Tests.Unit.Providers;

public class OpenRouterReasoningHandlerTests
{
    [Fact]
    public void Rewrite_ReplacesFlatReasoningEffortWithNestedShape()
    {
        var body = """{"model":"openai/gpt-5","messages":[],"reasoning_effort":"medium"}""";

        var result = OpenRouterReasoningHandler.Rewrite(body, ReasoningEffort.Medium);

        Assert.NotNull(result);
        var node = JsonNode.Parse(result!)!.AsObject();
        Assert.False(node.ContainsKey("reasoning_effort"));
        Assert.Equal("medium", node["reasoning"]!["effort"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(ReasoningEffort.Minimal, "minimal")]
    [InlineData(ReasoningEffort.Low, "low")]
    [InlineData(ReasoningEffort.Medium, "medium")]
    [InlineData(ReasoningEffort.High, "high")]
    [InlineData(ReasoningEffort.XHigh, "high")]
    public void Rewrite_MapsEffortLevelsToOpenRouterStrings(ReasoningEffort effort, string expected)
    {
        var body = """{"model":"x","messages":[]}""";

        var result = OpenRouterReasoningHandler.Rewrite(body, effort);

        Assert.NotNull(result);
        var node = JsonNode.Parse(result!)!.AsObject();
        Assert.Equal(expected, node["reasoning"]!["effort"]!.GetValue<string>());
    }

    [Fact]
    public void Rewrite_OmitsReasoningEntirelyForNone()
    {
        var body = """{"model":"x","messages":[],"reasoning_effort":"low"}""";

        var result = OpenRouterReasoningHandler.Rewrite(body, ReasoningEffort.None);

        Assert.NotNull(result);
        var node = JsonNode.Parse(result!)!.AsObject();
        Assert.False(node.ContainsKey("reasoning_effort"));
        Assert.False(node.ContainsKey("reasoning"));
    }

    [Fact]
    public void Rewrite_ReturnsNullForEmptyBody()
    {
        Assert.Null(OpenRouterReasoningHandler.Rewrite(string.Empty, ReasoningEffort.High));
    }

    [Fact]
    public void Rewrite_ReturnsNullForMalformedJson()
    {
        Assert.Null(OpenRouterReasoningHandler.Rewrite("not json", ReasoningEffort.High));
    }

    [Fact]
    public async Task SendAsync_RewritesOutgoingRequestBody()
    {
        var captured = new CapturingHandler();
        var rewrite = new OpenRouterReasoningHandler(ReasoningEffort.High) { InnerHandler = captured };
        var client = new HttpClient(rewrite);

        var body = """{"model":"openai/gpt-5","messages":[],"reasoning_effort":"low"}""";
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        await client.PostAsync("https://openrouter.ai/api/v1/chat/completions", content);

        Assert.NotNull(captured.LastBody);
        var node = JsonNode.Parse(captured.LastBody!)!.AsObject();
        Assert.False(node.ContainsKey("reasoning_effort"));
        Assert.Equal("high", node["reasoning"]!["effort"]!.GetValue<string>());
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
