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

    // Opus 5.5 rejects "disabled" at every effort and has no off, so effort None becomes its lowest effort.
    [Theory]
    [InlineData("claude-opus-5-5")]
    [InlineData("claude-opus-5-5-20261001")]
    public void Rewrite_DisableThinkingOnOpus55_OmitsThinkingAndSendsLowEffort(string model)
    {
        var obj = Parse(AnthropicRequestHandler.Rewrite(WithModel(model), false, false, effort: "high", disableThinking: true));

        Assert.Null(obj["thinking"]);
        Assert.Equal("low", obj["output_config"]!["effort"]!.GetValue<string>());
    }

    // Sonnet 5.5 rejects "disabled" and names between_tools, which takes no other thinking field.
    [Fact]
    public void Rewrite_DisableThinkingOnSonnet55_SendsBetweenToolsAlone()
    {
        var obj = Parse(AnthropicRequestHandler.Rewrite(WithModel("claude-sonnet-5-5"), false, false, disableThinking: true));

        var thinking = Assert.IsType<JsonObject>(obj["thinking"]);
        Assert.Equal("between_tools", thinking["type"]!.GetValue<string>());
        Assert.Single(thinking);
        Assert.Null(obj["output_config"]);
    }

    // Haiku 5.5 and older models accept "disabled".
    [Theory]
    [InlineData("claude-haiku-5-5")]
    [InlineData("claude-opus-5")]
    [InlineData("claude-sonnet-5")]
    public void Rewrite_DisableThinkingOnOtherModels_StaysDisabled(string model)
    {
        var obj = Parse(AnthropicRequestHandler.Rewrite(WithModel(model), false, false, disableThinking: true));

        Assert.Equal("disabled", obj["thinking"]!["type"]!.GetValue<string>());
        Assert.Null(obj["output_config"]);
    }

    private static string WithModel(string model) => Body.Replace("claude-opus-5", model, StringComparison.Ordinal);

    // Opus 5.5 and Sonnet 5.5 answer "any" and "tool" with a 400, with or without thinking.
    [Theory]
    [InlineData("claude-opus-5-5", """{"type":"any"}""")]
    [InlineData("claude-sonnet-5-5", """{"type":"tool","name":"get_weather"}""")]
    [InlineData("claude-sonnet-5-5-20260901", """{"type":"any"}""")]
    public void Rewrite_ForcedToolChoiceOnA55Model_AsksForAutoAndKeepsAdaptiveThinking(string model, string toolChoice)
    {
        var obj = Parse(AnthropicRequestHandler.Rewrite(WithToolChoice(toolChoice, model), false, false, effort: "high"));

        Assert.Equal("auto", obj["tool_choice"]!["type"]!.GetValue<string>());
        Assert.Null(obj["tool_choice"]!["name"]);
        Assert.Equal("adaptive", obj["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal("high", obj["output_config"]!["effort"]!.GetValue<string>());
    }

    [Fact]
    public void Rewrite_ForcedToolChoiceOnA55Model_KeepsTheChoiceFlags()
    {
        var obj = Parse(AnthropicRequestHandler.Rewrite(
            WithToolChoice("""{"type":"any","disable_parallel_tool_use":true}""", "claude-opus-5-5"), false, false));

        Assert.Equal("auto", obj["tool_choice"]!["type"]!.GetValue<string>());
        Assert.True(obj["tool_choice"]!["disable_parallel_tool_use"]!.GetValue<bool>());
    }

    // Opus 5 and Haiku 5.5 take a forced choice with adaptive thinking on.
    [Theory]
    [InlineData("claude-opus-5", "high")]
    [InlineData("claude-haiku-5-5", "high")]
    [InlineData("claude-opus-5", null)]
    public void Rewrite_ForcedToolChoiceElsewhere_IsKept(string model, string? effort)
    {
        var json = AnthropicRequestHandler.Rewrite(WithToolChoice("""{"type":"any"}""", model), false, false, effort: effort);

        var choice = json is null ? "any" : Parse(json)["tool_choice"]!["type"]!.GetValue<string>();
        Assert.Equal("any", choice);
    }

    [Fact]
    public void Rewrite_AutoToolChoiceWithEffort_KeepsAdaptiveThinking()
    {
        var obj = Parse(AnthropicRequestHandler.Rewrite(WithToolChoice("""{"type":"auto"}"""), false, false, effort: "high"));

        Assert.Equal("adaptive", obj["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal("high", obj["output_config"]!["effort"]!.GetValue<string>());
    }

    [Fact]
    public void Rewrite_ForcedToolChoiceOnA55ModelWithoutEffort_AsksForAutoAndAddsNoThinking()
    {
        var obj = Parse(AnthropicRequestHandler.Rewrite(WithToolChoice("""{"type":"any"}""", "claude-opus-5-5"), false, false));

        Assert.Equal("auto", obj["tool_choice"]!["type"]!.GetValue<string>());
        Assert.Null(obj["thinking"]);
    }

    [Fact]
    public void Rewrite_MalformedBody_IsLeftAlone()
    {
        Assert.Null(AnthropicRequestHandler.Rewrite("not json", true, true));
    }

    private static string WithToolChoice(string toolChoice, string model = "claude-opus-5") => $$$"""
        {"model":"{{{model}}}","max_tokens":1024,
         "tools":[{"name":"get_weather","input_schema":{"type":"object"}}],
         "tool_choice":{{{toolChoice}}},
         "messages":[{"role":"user","content":[{"type":"text","text":"hi"}]}]}
        """;

    private static JsonObject Parse(string? json)
    {
        Assert.NotNull(json);
        return (JsonObject)JsonNode.Parse(json!)!;
    }
}
