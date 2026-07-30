using Pia.Models;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// 04 D4: one class derivation for both gates AND the card. Before this, the gates asked the plugin ROUTE
/// (<c>IsMcpTool</c>) while the card switched on the plugin NAME with <c>_ =&gt; Mcp</c> — and the built-in
/// plugin <c>scheduled-research</c> was missing from that switch (04 §0.6).
/// </summary>
public class ToolClassifierTests
{
    [Fact]
    public void RouteWinsOverName()
    {
        // A server-defined tool whose plugin is called "files" must not inherit the built-in file class.
        Assert.Equal(ToolClass.External, ToolClassifier.Classify("files", isExternalRoute: true));
        Assert.Equal(ToolClass.External, ToolClassifier.Classify("memory", isExternalRoute: true));
    }

    [Theory]
    [InlineData("memory", ToolClass.Memory)]
    [InlineData("todo", ToolClass.Todo)]
    [InlineData("reminder", ToolClass.Reminder)]
    [InlineData("files", ToolClass.Files)]
    [InlineData("git", ToolClass.Git)]
    // THE §0.6 fix: a built-in scheduling plugin, previously classified as an external MCP tool.
    [InlineData("scheduled-research", ToolClass.Scheduling)]
    [InlineData("ingest", ToolClass.Ingest)]
    public void EveryBuiltInPluginNameMapsToANamedClass(string pluginName, ToolClass expected)
    {
        Assert.Equal(expected, ToolClassifier.Classify(pluginName, isExternalRoute: false));
        Assert.Equal(expected, ToolClassifier.ClassifyPresumedExternal(pluginName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("notion")]
    [InlineData("some-mcp-server")]
    public void AnUnrecognisedPluginNameIsUnknown_NotExternal(string? pluginName)
    {
        // Externality at a GATE is a ROUTE property and nothing else. Making an unrecognised NAME external
        // here would be a semantic change with a real hole behind it: a built-in plugin renamed through
        // ApplyServerMetadata would become grantable-as-external by name. Measured, not theorised —
        // BackgroundAssistantTurnRunnerTests.GrantedBuiltInDeleteFile_StillExecutes_TheFloorIsExternalOnly
        // goes red on an External fallback, because its fake pending action's plugin is called "plugin".
        Assert.Equal(ToolClass.Unknown, ToolClassifier.Classify(pluginName, isExternalRoute: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("notion")]
    [InlineData("some-mcp-server")]
    public void TheCardsNameOnlyGuessPresumesExternal(string? pluginName)
    {
        // The card builder has no route to consult and has always presumed "not a built-in name ⇒ external",
        // which is what puts the "Always allow" offer on a genuine MCP tool's card. Kept as a SEPARATE entry
        // point so a gate can never reach it (pinned by ToolAutonomyRuleTests).
        Assert.Equal(ToolClass.External, ToolClassifier.ClassifyPresumedExternal(pluginName));
    }
}
