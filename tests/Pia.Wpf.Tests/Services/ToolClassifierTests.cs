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
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("notion")]
    [InlineData("some-mcp-server")]
    public void AnUnrecognisedPluginNameIsExternal(string? pluginName)
    {
        // A pending action can only come from a REGISTERED plugin, and every registered non-built-in plugin
        // is an MCP server — so External is the honest answer, and it preserves today's card shape exactly
        // (an external tool keeps its "Always allow" offer). Unknown is reserved for a class NAME read back
        // from a persisted document that this build does not recognise, which Covers() hardcodes to false.
        Assert.Equal(ToolClass.External, ToolClassifier.Classify(pluginName, isExternalRoute: false));
    }
}
