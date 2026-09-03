using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The proxy's over-length refusal is recognised by its <c>code</c>, and the cap it names is read from
/// a numeric field — a server that predates that field still has to be recognised, without a number.
/// </summary>
public sealed class OptimizeTextTooLongTests
{
    [Fact]
    public void ReadsTheLimitFromTheServersError()
    {
        var body = """
            {"error":"Bad Request","message":"'text' must be under 10,000 characters.",
             "code":"optimize_text_too_long","limit":10000}
            """;

        Assert.True(AiClientService.TryReadTextTooLongLimit(body, out var limit));
        Assert.Equal(10000, limit);
    }

    [Fact]
    public void RecognisesTheRefusalFromAServerThatSendsNoLimit()
    {
        var body = """
            {"error":"Bad Request","message":"'text' must be under 10,000 characters.",
             "code":"optimize_text_too_long"}
            """;

        Assert.True(AiClientService.TryReadTextTooLongLimit(body, out var limit));
        Assert.Null(limit);
    }

    [Theory]
    [InlineData("""{"error":"Bad Request","message":"'text' is required."}""")]
    [InlineData("""{"code":"something_else","limit":10000}""")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    public void LeavesEveryOtherFailureAlone(string body)
    {
        Assert.False(AiClientService.TryReadTextTooLongLimit(body, out var limit));
        Assert.Null(limit);
    }
}
