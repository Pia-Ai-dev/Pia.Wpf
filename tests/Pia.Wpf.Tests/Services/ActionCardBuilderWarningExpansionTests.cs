using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>A warning lives in the card's details, so a collapsed pending card would let "Allow once" pass it unread.</summary>
public class ActionCardBuilderWarningExpansionTests
{
    private static ActionCardBuilder CreateBuilder()
    {
        var localization = Substitute.For<ILocalizationService>();
        localization[Arg.Any<string>()].Returns(ci => ci.Arg<string>());
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => ci.ArgAt<string>(0));
        return new ActionCardBuilder(localization, Substitute.For<ITokenMapService>());
    }

    private static PluginToolCall Call(string toolName, string pluginName, string? warning = null) =>
        new(toolName, Guid.NewGuid(), pluginName, "summary", "Label: value",
            () => Task.FromResult<object?>(null), Warning: warning);

    [Fact]
    public void AKnowledgeBaseCardWithAWarning_ArrivesExpanded()
    {
        var card = CreateBuilder().Build(Call("upload_kb_document", "kb-manager", "stored unencrypted"), detokenize: false);

        Assert.True(card.IsPending);
        Assert.True(card.IsExpanded);
    }

    [Fact]
    public void ACardWithoutAWarning_StaysCollapsed()
    {
        var card = CreateBuilder().Build(Call("create_todo", "todo"), detokenize: false);

        Assert.False(card.HasWarning);
        Assert.False(card.IsExpanded);
    }

    [Theory]
    [InlineData("delete_file", "files")]
    [InlineData("git_restore", "git")]
    public void AnExistingWarningCard_ArrivesExpanded(string tool, string plugin)
    {
        var card = CreateBuilder().Build(Call(tool, plugin), detokenize: false);

        Assert.True(card.HasWarning);
        Assert.True(card.IsExpanded);
    }

    [Fact]
    public void AnAutoApprovedCardWithAWarning_StaysCollapsed_BecauseNobodyIsAsked()
    {
        var card = CreateBuilder().Build(Call("upload_kb_document", "kb-manager", "stored unencrypted"),
            detokenize: false, autoApprovedAs: ToolGateDecision.AutoApprovedStandingGrant);

        Assert.True(card.IsResolved);
        Assert.False(card.IsExpanded);
    }

    [Fact]
    public void ResolvingAnExpandedWarningCard_CollapsesIt()
    {
        var card = CreateBuilder().Build(Call("delete_kb_document", "kb-manager", "shared"), detokenize: false);

        card.AllowOnceCommand.Execute(null);

        Assert.False(card.IsExpanded);
    }
}
