using System.Text.Json.Nodes;
using Pia.Services.Providers.Http;
using Xunit;

namespace Pia.Tests.Services.Providers;

public class AnthropicRequestHandlerTests
{
    private const string Body = """
    {"model":"claude-opus-5","max_tokens":1024,
     "system":[{"type":"text","text":"you are helpful"}],
     "messages":[{"role":"user","content":[{"type":"text","text":"hi"}]}]}
    """;

    [Fact]
    public void Rewrite_NothingEnabled_LeavesTheBodyAlone()
    {
        Assert.Null(AnthropicRequestHandler.Rewrite(Body, false, false));
    }

    [Fact]
    public void Rewrite_WebSearch_AppendsTheNativeTool()
    {
        var obj = Parse(AnthropicRequestHandler.Rewrite(Body, enableWebSearch: true, enablePromptCache: false));

        var tool = ((JsonArray)obj["tools"]!)[0]!;
        Assert.Equal("web_search_20250305", tool["type"]!.GetValue<string>());
        Assert.Equal("web_search", tool["name"]!.GetValue<string>());
    }

    [Fact]
    public void Rewrite_PromptCache_BreaksOnLastSystemBlockAndLastMessage()
    {
        var obj = Parse(AnthropicRequestHandler.Rewrite(Body, enableWebSearch: false, enablePromptCache: true));

        Assert.Equal("ephemeral", obj["system"]![0]!["cache_control"]!["type"]!.GetValue<string>());
        var lastBlock = ((JsonArray)((JsonArray)obj["messages"]!)[^1]!["content"]!)[^1]!;
        Assert.Equal("ephemeral", lastBlock["cache_control"]!["type"]!.GetValue<string>());
    }

    // A ttl would ask for the hour-long variant, which costs twice as much to write.
    [Fact]
    public void Rewrite_PromptCache_OmitsTtlSoTheFiveMinuteWindowApplies()
    {
        var obj = Parse(AnthropicRequestHandler.Rewrite(Body, false, enablePromptCache: true));

        Assert.Null(obj["system"]![0]!["cache_control"]!["ttl"]);
    }

    [Fact]
    public void Rewrite_PromptCacheWithoutASystemPrompt_BreaksOnTheLastToolInstead()
    {
        const string noSystem = """
        {"model":"claude-opus-5","max_tokens":1024,
         "tools":[{"name":"get_weather","input_schema":{"type":"object"}}],
         "messages":[{"role":"user","content":[{"type":"text","text":"hi"}]}]}
        """;

        var obj = Parse(AnthropicRequestHandler.Rewrite(noSystem, false, enablePromptCache: true));

        Assert.Equal("ephemeral", ((JsonArray)obj["tools"]!)[^1]!["cache_control"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Rewrite_Effort_SetsAdaptiveThinkingAndOutputConfig()
    {
        var obj = Parse(AnthropicRequestHandler.Rewrite(Body, false, false, effort: "xhigh"));

        Assert.Equal("adaptive", obj["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal("summarized", obj["thinking"]!["display"]!.GetValue<string>());
        Assert.Equal("xhigh", obj["output_config"]!["effort"]!.GetValue<string>());
    }

    [Fact]
    public void Rewrite_DisableThinking_WinsOverEffort()
    {
        var obj = Parse(AnthropicRequestHandler.Rewrite(Body, false, false, effort: "high", disableThinking: true));

        Assert.Equal("disabled", obj["thinking"]!["type"]!.GetValue<string>());
        Assert.Null(obj["output_config"]);
    }

    [Fact]
    public void Rewrite_MalformedBody_IsLeftAlone()
    {
        Assert.Null(AnthropicRequestHandler.Rewrite("not json", true, true));
    }

    private static JsonObject Parse(string? json)
    {
        Assert.NotNull(json);
        return (JsonObject)JsonNode.Parse(json!)!;
    }
}
