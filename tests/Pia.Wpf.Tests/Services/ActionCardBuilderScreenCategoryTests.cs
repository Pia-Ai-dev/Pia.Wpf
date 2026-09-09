using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary><c>screen</c> is a built-in plugin, so its cards must not fall into the MCP bucket and have their
/// key/value details parsed as JSON.</summary>
public class ActionCardBuilderScreenCategoryTests
{
    private static ActionCardBuilder CreateBuilder()
    {
        var localization = Substitute.For<ILocalizationService>();
        localization[Arg.Any<string>()].Returns(ci => ci.Arg<string>());
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => $"{ci.ArgAt<string>(0)}({string.Join(",", ci.ArgAt<object[]>(1))})");

        var tokenMap = Substitute.For<ITokenMapService>();
        return new ActionCardBuilder(localization, tokenMap);
    }

    private static PluginToolCall Call(string toolName, string? details = null, string pluginName = "screen") =>
        new(toolName, Guid.NewGuid(), pluginName, $"{pluginName}: {toolName}", details,
            () => Task.FromResult<object?>(null));

    [Fact]
    public void ScreenCaptureCard_IsAScreenCard_NotAnExternalToolCard()
    {
        var card = CreateBuilder().Build(Call("screen_capture"), detokenize: false);

        Assert.Equal(ActionCardCategory.Screen, card.Category);
        Assert.Equal("ActionCard_Action_Capture ActionCard_Category_Screen", card.Title);
    }

    [Fact]
    public void ScreenCaptureCard_IsNotDestructive_AndCarriesNoWarning()
    {
        var card = CreateBuilder().Build(Call("screen_capture"), detokenize: false);

        Assert.False(card.IsDestructive);
        Assert.Null(card.WarningText);
    }

    /// <summary>The handler must build "Label: value" lines, not JSON — a Screen card takes the key/value
    /// branch, which renders no rows at all for a JSON payload.</summary>
    [Fact]
    public void ScreenCaptureCard_ParsesKeyValueDetails()
    {
        var card = CreateBuilder().Build(
            Call("screen_capture", "Target: window\nProcess: outlook\nSize: 1568x880"), detokenize: false);

        Assert.Contains(card.Details, d => d.Label == "Target" && d.Value == "window");
        Assert.Contains(card.Details, d => d.Label == "Process" && d.Value == "outlook");
    }

    [Fact]
    public void ScreenCaptureCard_WithTheAuthoritativeClass_IgnoresThePluginName()
    {
        var card = CreateBuilder().Build(
            Call("screen_capture", pluginName: "renamed"), detokenize: false, toolClass: ToolClass.Screen);

        Assert.Equal(ActionCardCategory.Screen, card.Category);
    }

    [Fact]
    public void ScreenCaptureStatusAndSuccessText_AreTheirOwnStrings()
    {
        var builder = CreateBuilder();

        Assert.Equal("Msg_Assistant_StatusCapturingScreen", builder.ResolveStatusText("screen_capture"));
        Assert.Equal("Msg_Assistant_StatusListingScreenTargets", builder.ResolveStatusText("screen_list_targets"));
        Assert.Equal("Msg_Assistant_ScreenCaptured", builder.ResolveSuccessTitle("screen"));
    }
}
