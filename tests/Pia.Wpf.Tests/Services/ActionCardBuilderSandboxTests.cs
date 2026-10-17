using NSubstitute;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Plugins.Sandbox;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>The sandbox line is information on the card; it must never change what the gate decides.</summary>
public class ActionCardBuilderSandboxTests
{
    private static ActionCardBuilder CreateBuilder()
    {
        var localization = Substitute.For<ILocalizationService>();
        localization[Arg.Any<string>()].Returns(ci => ci.Arg<string>());
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => $"{ci.ArgAt<string>(0)}({string.Join(",", ci.ArgAt<object[]>(1))})");
        localization.Format("ActionCard_Title_Format", Arg.Any<object[]>())
            .Returns(ci => string.Join(" ", ci.ArgAt<object[]>(1)));

        return new ActionCardBuilder(localization, Substitute.For<ITokenMapService>());
    }

    private static PluginToolCall McpCall(McpSandboxPolicy? sandbox) =>
        new("files__delete_file", Guid.NewGuid(), "files", "files: delete_file", """{"path":"a.txt"}""",
            () => Task.FromResult<object?>(null), ServerDeclaredDestructive: true, Sandbox: sandbox);

    [Fact]
    public void ASandboxedCall_CarriesTheFolderCountsAndNetworkOff()
    {
        var card = CreateBuilder().Build(
            McpCall(new McpSandboxPolicy([@"C:\a", @"C:\b"], [@"C:\c"], AllowNetwork: false)), detokenize: false);

        Assert.Equal("ActionCard_Sandbox_NetworkOff(2,1)", card.SandboxText);
        Assert.True(card.HasSandbox);
    }

    [Fact]
    public void ASandboxedCallWithNetworkAllowed_SaysSo()
    {
        var card = CreateBuilder().Build(
            McpCall(new McpSandboxPolicy([], [], AllowNetwork: true)), detokenize: false);

        Assert.Equal("ActionCard_Sandbox_NetworkOn(0,0)", card.SandboxText);
    }

    [Fact]
    public void AnUncontainedCall_HasNoSandboxLine()
    {
        var card = CreateBuilder().Build(McpCall(sandbox: null), detokenize: false);

        Assert.Null(card.SandboxText);
        Assert.False(card.HasSandbox);
    }

    [Fact]
    public void TheSandboxLine_DoesNotChangeCategoryDestructivenessOrWarning()
    {
        var builder = CreateBuilder();

        var contained = builder.Build(McpCall(new McpSandboxPolicy([@"C:\a"], [], false)), detokenize: false);
        var uncontained = builder.Build(McpCall(sandbox: null), detokenize: false);

        Assert.Equal(uncontained.Category, contained.Category);
        Assert.Equal(uncontained.IsDestructive, contained.IsDestructive);
        Assert.Equal(uncontained.WarningText, contained.WarningText);
        Assert.Equal(uncontained.Title, contained.Title);
    }
}
