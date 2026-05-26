using Pia.Services.Providers.Http;
using Xunit;

namespace Pia.Wpf.Tests.Unit.Providers;

public class MistralThinkingResponseHandlerTests
{
    private const string ResponseWithThinking = """
        {
          "choices": [{
            "message": {
              "role": "assistant",
              "content": [
                {"type":"thinking","thinking":"let me think..."},
                {"type":"text","text":"ok"}
              ]
            }
          }]
        }
        """;

    private const string ResponseWithoutThinking = """
        {
          "choices": [{
            "message": {
              "role": "assistant",
              "content": "ok"
            }
          }]
        }
        """;

    [Fact]
    public void StripThinkingParts_RemovesThinkingEntries_LeavesText()
    {
        var result = MistralThinkingResponseHandler.StripThinkingParts(ResponseWithThinking);

        Assert.NotNull(result);
        Assert.DoesNotContain("thinking", result);
        Assert.Contains("\"text\"", result);
        Assert.Contains("ok", result);
    }

    [Fact]
    public void StripThinkingParts_ReturnsNull_WhenNoThinkingPresent()
    {
        var result = MistralThinkingResponseHandler.StripThinkingParts(ResponseWithoutThinking);

        Assert.Null(result);
    }

    [Fact]
    public void StripThinkingParts_ReturnsNull_ForEmptyBody()
    {
        Assert.Null(MistralThinkingResponseHandler.StripThinkingParts(""));
        Assert.Null(MistralThinkingResponseHandler.StripThinkingParts("not-json"));
    }

    [Fact]
    public void StripThinkingParts_HandlesMultipleThinkingParts()
    {
        var body = """
            {
              "choices": [{
                "message": {
                  "content": [
                    {"type":"thinking","thinking":"first"},
                    {"type":"thinking","thinking":"second"},
                    {"type":"text","text":"answer"}
                  ]
                }
              }]
            }
            """;

        var result = MistralThinkingResponseHandler.StripThinkingParts(body);

        Assert.NotNull(result);
        Assert.DoesNotContain("thinking", result);
        Assert.Contains("answer", result);
    }
}
