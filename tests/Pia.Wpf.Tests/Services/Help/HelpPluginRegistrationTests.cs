using Pia.Models;
using Pia.Services;
using Pia.Services.Plugins;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services.Help;

/// <summary>
/// Locks the wiring that makes the help pack REACHABLE by the model: a preloaded, default-enabled
/// built-in whose adapter exposes pia_help and pia_settings, plus a system prompt that names both and
/// states the thing the model cannot infer — that it is running inside Pia and should stop guessing.
/// </summary>
public sealed class HelpPluginRegistrationTests
{
    private static SyncPlugin HelpConfig() => BuiltInPluginDefaults.Defaults[BuiltInPluginDefaults.HelpPluginId];

    private static HelpToolHandler Handler() => HelpToolHandlerTests.Handler(TargetLanguage.EN);

    [Fact]
    public void TheHelpPackIsAPreloadedDefaultEnabledBuiltIn()
    {
        var config = HelpConfig();

        Assert.True(config.IsPreloaded);
        Assert.True(config.IsActive);
        Assert.Equal("help", config.Name);
        Assert.Equal("builtin_tool_pack", config.Kind);
        Assert.Contains("\"handlerId\":\"help\"", config.ConfigJson, StringComparison.Ordinal);
        Assert.Contains("\"defaultEnabled\":true", config.ConfigJson, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHelpPluginIdIsPreloadedAndDoesNotCollide()
    {
        Assert.Contains(BuiltInPluginDefaults.HelpPluginId, BuiltInPluginDefaults.PreloadedPluginIds);
        Assert.Equal(
            BuiltInPluginDefaults.Defaults.Count,
            BuiltInPluginDefaults.Defaults.Keys.Distinct().Count());
    }

    [Fact]
    public void TheAdapterExposesBothToolsAndAPromptThatNamesThem()
    {
        var config = HelpConfig();
        var adapter = BuiltInPluginHandler.FromHelpHandler(Handler(), config);

        var toolNames = adapter.GetTools().Select(t => t.Name).ToList();
        Assert.Equal(["pia_help", "pia_settings"], toolNames);

        var prompt = adapter.GetSystemPromptAddition();
        Assert.NotNull(prompt);
        Assert.Contains("pia_help", prompt, StringComparison.Ordinal);
        Assert.Contains("pia_settings", prompt, StringComparison.Ordinal);
        Assert.Contains("desktop app", prompt, StringComparison.Ordinal);
    }

    /// <summary>The routing text is the whole per-turn cost of this feature; a cap is what stops it
    /// growing back into the capability dump it replaces.</summary>
    [Fact]
    public void TheRoutingTextStaysSmallEnoughToCarryEveryTurn()
    {
        var prompt = BuiltInPluginHandler.FromHelpHandler(Handler(), HelpConfig()).GetSystemPromptAddition();

        Assert.NotNull(prompt);
        Assert.True(prompt.Length < 500, $"the help prompt addition is {prompt.Length} characters; keep it under 500");
        Assert.True(AssistantPromptComposer.SelfKnowledgeLeadIn.Length < 320,
            $"the tool-tree lead-in is {AssistantPromptComposer.SelfKnowledgeLeadIn.Length} characters; keep it under 320");
    }

    [Fact]
    public async Task TheAdapterHasNoPendingActionsBecauseBothToolsOnlyRead()
    {
        var adapter = BuiltInPluginHandler.FromHelpHandler(Handler(), HelpConfig());

        var (result, pending) = await adapter.HandleToolCallAsync(
            new Microsoft.Extensions.AI.FunctionCallContent("1", "pia_help", new Dictionary<string, object?>()), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Null(pending);
    }

    /// <summary>Both tools run inline, so a grant tier is never consulted for them — offering one in the
    /// permission UI would invite a choice that authorizes nothing.</summary>
    [Theory]
    [InlineData("pia_help")]
    [InlineData("pia_settings")]
    public void BothToolsAreKnownToBeReadOnly(string toolName)
    {
        Assert.True(ToolPermissionService.IsReadOnlyBuiltIn(toolName));
    }
}
