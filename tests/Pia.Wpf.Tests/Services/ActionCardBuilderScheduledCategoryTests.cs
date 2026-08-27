using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary><c>scheduled-research</c> is a built-in plugin, so its cards must not fall into the MCP bucket.</summary>
public class ActionCardBuilderScheduledCategoryTests
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

    private static PluginToolCall Call(string toolName, string? details = null) =>
        new(toolName, Guid.NewGuid(), "scheduled-research", $"scheduled-research: {toolName}", details,
            () => Task.FromResult<object?>(null));

    [Fact]
    public void ScheduledResearchCard_IsNotAnExternalToolCard()
    {
        var card = CreateBuilder().Build(Call("create_scheduled_research"), detokenize: false);

        Assert.Equal(ActionCardCategory.Scheduled, card.Category);
        Assert.NotEqual("ActionCard_Category_Mcp", card.Title);
        Assert.Equal("ActionCard_Action_Create ActionCard_Category_Scheduled", card.Title);
    }

    /// <summary>Its arguments are themselves a grant list, and both tiers are still offered — the Tool access row
    /// says so once one is ticked, rather than the weaker button being taken away.</summary>
    [Fact]
    public void ScheduledResearchCard_OffersBothGrantTiers()
    {
        var card = CreateBuilder().Build(Call("create_scheduled_research"), detokenize: false);

        Assert.Equal(4, card.Decisions.Count);
        Assert.Contains(card.Decisions, d => ReferenceEquals(d.Command, card.AllowForSessionCommand));
        Assert.Contains(card.Decisions, d => ReferenceEquals(d.Command, card.AlwaysAllowCommand));
        Assert.Equal(ToolGrantCaution.AuthorityAuthoring, CautionOf("create_scheduled_research"));
    }

    [Fact]
    public void ScheduledResearchCard_ParsesItsKeyValueDetails()
    {
        // What ScheduledJobToolHandler actually builds: "Label: value" lines, not JSON.
        var card = CreateBuilder().Build(
            Call("create_scheduled_research", "Name: Morning digest\nKind: Agent task\nRecurrence: Daily"),
            detokenize: false);

        Assert.True(card.Details.Count >= 2);
        Assert.Contains(card.Details, d => d.Label == "Name" && d.Value == "Morning digest");
        Assert.Contains(card.Details, d => d.Label == "Kind" && d.Value == "Agent task");
    }

    [Theory]
    [InlineData("update_scheduled_research", "ActionCard_Action_Update ActionCard_Category_Scheduled")]
    [InlineData("delete_scheduled_research", "ActionCard_Action_Delete ActionCard_Category_Scheduled")]
    public void UpdateAndDeleteScheduledResearch_UseTheirOwnVerbs(string toolName, string expectedTitle)
    {
        var card = CreateBuilder().Build(Call(toolName), detokenize: false);

        Assert.Equal(expectedTitle, card.Title);
    }

    [Fact]
    public void DeleteScheduledResearch_KeepsItsDestructiveWarning()
    {
        // Delete-like by name, so the warning resolves to the generic external-delete string: a
        // scheduling-specific one would mean a fourth locale triple.
        var card = CreateBuilder().Build(Call("delete_scheduled_research"), detokenize: false);

        Assert.True(card.IsDestructive);
        Assert.Equal("Msg_Assistant_PermanentDeleteExternal", card.WarningText);
    }

    /// <summary>These verbs shed uncommitted work yet carry no destructive stem, so the card styles them as
    /// destructive and the catalogue cautions about them — neither tier is withheld.</summary>
    [Theory]
    [InlineData("git_switch")]
    [InlineData("git_restore")]
    [InlineData("git_stash")]
    public void AnExternalGitVerb_IsOfferedBothTiers_AndCautionedAsWorkDiscarding(string toolName)
    {
        var pending = new PluginToolCall(
            toolName, Guid.NewGuid(), "some-mcp-server", $"some-mcp-server: {toolName}", null,
            () => Task.FromResult<object?>(null));

        var card = CreateBuilder().Build(pending, detokenize: false);

        Assert.Equal(ActionCardCategory.Mcp, card.Category);
        Assert.True(card.IsDestructive);
        Assert.Equal(4, card.Decisions.Count);

        // Not delete-like by name, so the caution comes from IsWorkDiscarding and nothing else.
        Assert.False(ToolPermissionService.IsDeleteLike(toolName));
        Assert.Equal(ToolGrantCaution.WorkDiscarding, CautionOf(toolName));
    }

    private static ToolGrantCaution CautionOf(string toolName) =>
        new ToolCatalogRow(new ToolCatalogEntry(
            Guid.NewGuid(), "some-mcp-server", toolName, "desc",
            IsExternalRoute: true, ServerDeclaredDestructive: false)).Caution;

    /// <summary>The card offers a tier exactly where the gate would honour one — both tiers, everywhere. This is
    /// what keeps the offer and the authority from drifting now that no offerability function couples them.</summary>
    [Theory]
    [InlineData("memory", "create_object")]
    [InlineData("memory", "forget")]
    [InlineData("todo", "create_todo")]
    [InlineData("todo", "delete_todo")]
    [InlineData("reminder", "create_reminder")]
    [InlineData("files", "write_file")]
    [InlineData("files", "delete_file")]
    [InlineData("git", "git_switch")]
    [InlineData("git", "git_commit")]
    [InlineData("scheduled-research", "create_scheduled_research")]
    [InlineData("linear", "search_issues")]
    [InlineData("linear", "delete_issue")]
    public void CardAndGate_AgreeThatBothTiersAreAlwaysHonoured(string pluginName, string toolName)
    {
        var toolClass = ToolClassifier.ClassifyPresumedExternal(pluginName);
        var pending = new PluginToolCall(
            toolName, Guid.NewGuid(), pluginName, $"{pluginName}: {toolName}", null,
            () => Task.FromResult<object?>(null));

        var card = CreateBuilder().Build(pending, detokenize: false);

        Assert.Contains(card.Decisions, d => ReferenceEquals(d.Command, card.AllowForSessionCommand));
        Assert.Contains(card.Decisions, d => ReferenceEquals(d.Command, card.AlwaysAllowCommand));

        Assert.Equal(ToolGateDecision.AutoApprovedStandingGrant, Interactive(standing: true).Decision);
        Assert.Equal(ToolGateDecision.AutoApprovedSessionGrant, Interactive(standing: false).Decision);

        ToolGateVerdict Interactive(bool standing) => ToolAutonomy.Resolve(new ToolGateInput(
            ToolGateSurface.Interactive, toolName, toolClass,
            ServerDeclaredDestructive: false, IsAllowlisted: false, HasSessionGrant: !standing,
            HasStandingGrant: standing, IsNamedGrant: false, HasNamedDenial: false, Policy: null, CanPark: false,
            IsTopLevelUserRun: false));
    }
}
