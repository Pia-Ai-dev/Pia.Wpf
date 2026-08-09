using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
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
        var permissions = Substitute.For<IToolPermissionService>();
        return new ActionCardBuilder(localization, tokenMap, permissions);
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

    [Fact]
    public void ScheduledResearchCard_OffersNoAlwaysAllowButton()
    {
        var card = CreateBuilder().Build(Call("create_scheduled_research"), detokenize: false);

        Assert.False(card.IsAutoApprovable);
        // Neither grant tier: this tool's arguments are themselves a grant list, so one click would authorize
        // every later job-authoring call in the process.
        Assert.Equal(2, card.Decisions.Count);
        Assert.DoesNotContain(card.Decisions, d => ReferenceEquals(d.Command, card.AlwaysAllowCommand));
        Assert.DoesNotContain(card.Decisions, d => ReferenceEquals(d.Command, card.AllowForSessionCommand));
        Assert.False(card.IsSessionGrantable);
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

    /// <summary>These verbs shed uncommitted work yet carry no destructive stem, so the shared resolver alone
    /// would offer a standing grant; the exclusion is the card's own.</summary>
    [Theory]
    [InlineData("git_switch")]
    [InlineData("git_restore")]
    [InlineData("git_stash")]
    public void AnExternalGitVerb_IsNeverOfferedAStandingGrant(string toolName)
    {
        var pending = new PluginToolCall(
            toolName, Guid.NewGuid(), "some-mcp-server", $"some-mcp-server: {toolName}", null,
            () => Task.FromResult<object?>(null));

        var card = CreateBuilder().Build(pending, detokenize: false);

        Assert.Equal(ActionCardCategory.Mcp, card.Category);
        Assert.True(card.IsDestructive);
        Assert.False(card.IsAutoApprovable);
        Assert.Equal(2, card.Decisions.Count);   // the pair, never the triad
        Assert.DoesNotContain(card.Decisions, d => ReferenceEquals(d.Command, card.AlwaysAllowCommand));

        // The shared floor says yes here; only the card's wider exclusion says no.
        Assert.False(ToolPermissionService.IsDeleteLike(toolName));
        Assert.True(ToolAutonomy.IsStandingGrantOfferable(ToolClass.External, toolName, isAllowlisted: false));
    }

    /// <summary>Proves only that the card routes through the shared resolver, not what that resolver decides.</summary>
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
    public void CardAndGate_AgreeOnEligibility(string pluginName, string toolName)
    {
        var localization = Substitute.For<ILocalizationService>();
        localization[Arg.Any<string>()].Returns(ci => ci.Arg<string>());
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => ci.ArgAt<string>(0));
        var permissions = Substitute.For<IToolPermissionService>();
        permissions.IsAutoApproveEligible("create_todo").Returns(true);
        permissions.IsAutoApproveEligible("create_reminder").Returns(true);
        permissions.IsAutoApproveEligible("create_object").Returns(true);
        var builder = new ActionCardBuilder(localization, Substitute.For<ITokenMapService>(), permissions);

        var pending = new PluginToolCall(
            toolName, Guid.NewGuid(), pluginName, $"{pluginName}: {toolName}", null,
            () => Task.FromResult<object?>(null));

        var card = builder.Build(pending, detokenize: false);

        var expected = ToolAutonomy.IsStandingGrantOfferable(
            ToolClassifier.ClassifyPresumedExternal(pluginName), toolName,
            permissions.IsAutoApproveEligible(toolName));

        Assert.Equal(expected, card.IsAutoApprovable);
    }
}
