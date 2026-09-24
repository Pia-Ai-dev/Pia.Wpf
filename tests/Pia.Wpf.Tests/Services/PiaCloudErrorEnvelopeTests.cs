using System.Text.Json.Nodes;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

public class PiaCloudErrorEnvelopeTests
{
    private static (string Title, string? Message) Describe(string chunkJson)
    {
        var chunk = JsonNode.Parse(chunkJson)!;
        return PiaCloudErrorEnvelope.Describe(chunk["error"], chunk);
    }

    [Fact]
    public void ReadsTheChatStreamServiceShape()
    {
        var (title, message) = Describe("""{"error":"Bad Gateway","message":"upstream died"}""");

        Assert.Equal("Bad Gateway", title);
        Assert.Equal("upstream died", message);
    }

    [Fact]
    public void ReadsAnOpenAiStyleErrorObject()
    {
        var (title, message) = Describe("""{"error":{"message":"context length exceeded","type":"invalid_request_error"}}""");

        Assert.Equal("invalid_request_error", title);
        Assert.Equal("context length exceeded", message);
    }

    /// <summary>The shape that put a raw JSON blob in a chat bubble: the whole upstream envelope arrives as a
    /// STRING, so reading it as a title surfaced the braces to the user.</summary>
    [Fact]
    public void UnwrapsAnEnvelopeNestedAsAString()
    {
        var (title, message) = Describe(
            """
            {"error":"{\"error\":{\"message\":\"No endpoints found that support image input\",\"code\":404,\"metadata\":{\"failed_routing_step\":\"Filter by Image Support\"}}}"}
            """);

        Assert.Equal("No endpoints found that support image input", message);
        Assert.Equal("404", title);
        Assert.DoesNotContain("{", message!);
    }

    [Fact]
    public void UnwrapsANestedEnvelopeThatOnlyCarriesAMessage()
    {
        var (title, message) = Describe("""{"error":"{\"message\":\"upstream refused\"}"}""");

        Assert.Equal(PiaCloudErrorEnvelope.DefaultTitle, title);
        Assert.Equal("upstream refused", message);
    }

    [Fact]
    public void KeepsAPlainStringErrorAsTheTitle()
    {
        var (title, message) = Describe("""{"error":"Service Unavailable"}""");

        Assert.Equal("Service Unavailable", title);
        Assert.Null(message);
    }

    /// <summary>A string that merely starts like JSON must not be swallowed — it is still the title.</summary>
    [Fact]
    public void KeepsUnparseableBraceTextAsTheTitle()
    {
        var (title, _) = Describe("""{"error":"{not json at all"}""");

        Assert.Equal("{not json at all", title);
    }

    [Fact]
    public void FallsBackToTheDefaultTitleWhenThereIsNothingToRead()
    {
        var (title, message) = Describe("""{"error":null}""");

        Assert.Equal(PiaCloudErrorEnvelope.DefaultTitle, title);
        Assert.Null(message);
    }

    [Fact]
    public void UsesACodeWhenNoTypeIsPresent()
    {
        var (title, _) = Describe("""{"error":{"message":"nope","code":"rate_limited"}}""");

        Assert.Equal("rate_limited", title);
    }
}
