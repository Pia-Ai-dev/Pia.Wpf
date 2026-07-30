using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Pia.Services.Providers.Http;
using Xunit;

namespace Pia.Wpf.Tests.Unit.Providers;

public class OpenAiReasoningSummaryFallbackHandlerTests
{
    [Fact]
    public void StripReasoningSummary_RemovesSummary_KeepsEffort()
    {
        var body = """{"model":"gpt-5","reasoning":{"effort":"high","summary":"auto"}}""";

        var result = OpenAiReasoningSummaryFallbackHandler.StripReasoningSummary(body);

        Assert.NotNull(result);
        var reasoning = JsonNode.Parse(result!)!["reasoning"]!;
        Assert.Null(reasoning["summary"]);
        Assert.Equal("high", reasoning["effort"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"model":"gpt-5","reasoning":{"effort":"high"}}""")]
    [InlineData("""{"model":"gpt-5"}""")]
    [InlineData("not json")]
    [InlineData("")]
    public void StripReasoningSummary_ReturnsNull_WhenNothingToStrip(string body)
    {
        Assert.Null(OpenAiReasoningSummaryFallbackHandler.StripReasoningSummary(body));
    }

    [Fact]
    public async Task SendAsync_RetriesWithoutSummary_When400OnSummaryRequest()
    {
        var stub = new SummaryRejectingHandler();
        var client = new HttpClient(new OpenAiReasoningSummaryFallbackHandler { InnerHandler = stub });

        var content = new StringContent(
            """{"reasoning":{"effort":"high","summary":"auto"}}""", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("https://api.openai.com/v1/responses", content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, stub.Calls);
        Assert.NotNull(stub.LastBody);
        Assert.Null(JsonNode.Parse(stub.LastBody!)!["reasoning"]!["summary"]);
    }

    [Fact]
    public async Task SendAsync_DoesNotRetry_WhenFirstCallSucceeds()
    {
        var stub = new SummaryRejectingHandler();
        var client = new HttpClient(new OpenAiReasoningSummaryFallbackHandler { InnerHandler = stub });

        var content = new StringContent(
            """{"reasoning":{"effort":"high"}}""", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("https://api.openai.com/v1/responses", content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, stub.Calls);
    }

    /// <summary>Returns 400 whenever the request body still contains reasoning.summary,
    /// and 200 otherwise — simulating an org that rejects summary requests.</summary>
    private sealed class SummaryRejectingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var hasSummary = LastBody is not null &&
                JsonNode.Parse(LastBody)?["reasoning"]?["summary"] is not null;
            return new HttpResponseMessage(hasSummary ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
