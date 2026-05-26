using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Pia.Services.Providers.Http;
using Xunit;

namespace Pia.Wpf.Tests.Unit.Providers;

public class OpenAiWebSearchHandlerTests
{
    [Fact]
    public void Rewrite_AddsWebSearchPreviewToolToEmptyRequest()
    {
        var body = """{"model":"gpt-4o-mini","input":[]}""";

        var result = OpenAiWebSearchHandler.Rewrite(body);

        Assert.NotNull(result);
        var node = JsonNode.Parse(result!)!.AsObject();
        var tools = node["tools"]!.AsArray();
        Assert.Single(tools);
        Assert.Equal("web_search_preview", tools[0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Rewrite_AppendsWebSearchPreviewToolToExistingTools()
    {
        var body = """{"model":"gpt-4o-mini","input":[],"tools":[{"type":"function","function":{"name":"ping"}}]}""";

        var result = OpenAiWebSearchHandler.Rewrite(body);

        Assert.NotNull(result);
        var tools = JsonNode.Parse(result!)!.AsObject()["tools"]!.AsArray();
        Assert.Equal(2, tools.Count);
        Assert.Equal("web_search_preview", tools[1]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Rewrite_ReturnsNullForEmptyBody()
    {
        Assert.Null(OpenAiWebSearchHandler.Rewrite(string.Empty));
    }

    [Fact]
    public void Rewrite_ReturnsNullForMalformedJson()
    {
        Assert.Null(OpenAiWebSearchHandler.Rewrite("not json"));
    }

    [Fact]
    public async Task SendAsync_InjectsWebSearchPreviewToolIntoRequest()
    {
        var captured = new CapturingHandler();
        var handler = new OpenAiWebSearchHandler { InnerHandler = captured };
        var client = new HttpClient(handler);

        var body = """{"model":"gpt-4o-mini","input":[]}""";
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        await client.PostAsync("https://api.openai.com/v1/responses", content);

        Assert.NotNull(captured.LastBody);
        var tools = JsonNode.Parse(captured.LastBody!)!.AsObject()["tools"]!.AsArray();
        Assert.Equal("web_search_preview", tools[0]!["type"]!.GetValue<string>());
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
