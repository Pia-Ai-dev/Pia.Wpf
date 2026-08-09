using Pia.Models;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>One class derivation for both the gates and the card, so the two can never disagree about a
/// tool.</summary>
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
    // A built-in scheduling plugin, once classified as an external MCP tool.
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
        // Externality at a GATE is a ROUTE property: making an unrecognised NAME external here would let a
        // built-in plugin renamed through ApplyServerMetadata become grantable-as-external by name.
        Assert.Equal(ToolClass.Unknown, ToolClassifier.Classify(pluginName, isExternalRoute: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("notion")]
    [InlineData("some-mcp-server")]
    public void TheCardsNameOnlyGuessPresumesExternal(string? pluginName)
    {
        // The card builder has no route to consult, so it presumes "not a built-in name ⇒ external"; kept a
        // separate entry point so a gate can never reach it.
        Assert.Equal(ToolClass.External, ToolClassifier.ClassifyPresumedExternal(pluginName));
    }
}
