using NSubstitute;
using Pia.Controls.Cards;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>The localization mock echoes each key, so a wrong key surfaces directly in the asserted value.</summary>
public class ActionCardBuilderTests
{
    private static ActionCardBuilder CreateBuilder(out ITokenMapService tokenMap)
    {
        var localization = Substitute.For<ILocalizationService>();
        localization[Arg.Any<string>()].Returns(ci => ci.Arg<string>());
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => $"{ci.ArgAt<string>(0)}({string.Join(",", ci.ArgAt<object[]>(1))})");

        tokenMap = Substitute.For<ITokenMapService>();
        return new ActionCardBuilder(localization, tokenMap);
    }

    private static PluginToolCall Call(string toolName, string pluginName, string description, string? details = null, Guid pluginId = default) =>
        new(toolName, pluginId, pluginName, description, details, () => Task.FromResult<object?>(null));

    [Fact]
    public void Build_CreateMemory_MapsTitleCategoryAndSummary()
    {
        var builder = CreateBuilder(out _);

        var card = builder.Build(Call("create_object", "memory", "Remember the WiFi password", "{\"key\":\"value\"}"), detokenize: false);

        Assert.Equal(ActionCardCategory.Memory, card.Category);
        Assert.False(card.IsDestructive);
        Assert.Null(card.WarningText);
        Assert.Equal("ActionCard_Action_Create ActionCard_Category_Memory", card.Title);
        Assert.Equal("Remember the WiFi password", card.Summary);
        Assert.NotEmpty(card.Details);
    }

    [Fact]
    public void Build_DeleteTodo_IsDestructiveWithWarning()
    {
        var builder = CreateBuilder(out _);

        var card = builder.Build(Call("delete_todo", "todo", "Delete the groceries todo"), detokenize: false);

        Assert.Equal(ActionCardCategory.Todo, card.Category);
        Assert.True(card.IsDestructive);
        Assert.Equal("Msg_Assistant_PermanentDeleteTodo", card.WarningText);
        Assert.Equal("ActionCard_Action_Delete ActionCard_Category_Todo", card.Title);
        Assert.Empty(card.Details); // no Details payload provided
    }

    [Fact]
    public void Build_Remember_IsUpdateMemoryTitleAndNotDestructive()
    {
        var builder = CreateBuilder(out _);

        var card = builder.Build(Call("remember", "memory", "Remember John's email", "{\"key\":\"value\"}"), detokenize: false);

        Assert.Equal(ActionCardCategory.Memory, card.Category);
        Assert.False(card.IsDestructive);
        Assert.Null(card.WarningText);
        Assert.Equal("ActionCard_Action_Update ActionCard_Category_Memory", card.Title);
        Assert.NotEqual("ActionCard_Action_Create ActionCard_Category_Memory", card.Title);
    }

    [Fact]
    public void Build_Forget_IsDestructiveWithDeleteTitleAndWarning()
    {
        var builder = CreateBuilder(out _);

        var card = builder.Build(Call("forget", "memory", "Forget John's contact"), detokenize: false);

        Assert.Equal(ActionCardCategory.Memory, card.Category);
        Assert.True(card.IsDestructive);
        Assert.Equal("Msg_Assistant_PermanentDeleteMemory", card.WarningText);
        Assert.Equal("ActionCard_Action_Delete ActionCard_Category_Memory", card.Title);
        Assert.NotEqual("ActionCard_Action_Create ActionCard_Category_Memory", card.Title);
    }

    [Fact]
    public void Build_McpTool_IsExternalCategory_Grantable_AndParsesJsonArgs()
    {
        var builder = CreateBuilder(out _);

        var card = builder.Build(Call("search_issues", "linear", "linear: search_issues", "{\"query\":\"bug\"}"), detokenize: false);

        Assert.Equal(ActionCardCategory.Mcp, card.Category);
        Assert.Equal("ActionCard_Category_Mcp", card.Title);
        Assert.False(card.IsDestructive);
        Assert.NotEmpty(card.Details);        // JSON args parsed for display
    }

    /// <summary>The warning is styling, not authority: the card still offers both grant tiers.</summary>
    [Fact]
    public void Build_DestructiveMcpTool_StillOffersBothGrantTiers_AndWarns()
    {
        var builder = CreateBuilder(out _);

        var card = builder.Build(Call("delete_issue", "linear", "linear: delete_issue"), detokenize: false);

        Assert.Equal(ActionCardCategory.Mcp, card.Category);
        Assert.True(card.IsDestructive);
        Assert.Equal("Msg_Assistant_PermanentDeleteExternal", card.WarningText);
        Assert.Contains(card.Decisions, d => d.Label == "ActionCard_AllowForSession");
        Assert.Contains(card.Decisions, d => d.Label == "ActionCard_AlwaysAllow");
        // The red "Allow once" keys off IsDestructive alone; no tier is withheld to signal it.
        Assert.Equal(DecisionEmphasis.Danger, card.Decisions[1].Emphasis);
    }

    [Fact]
    public void Build_WhenDetokenizeFalse_DoesNotTouchTokenMap()
    {
        var builder = CreateBuilder(out var tokenMap);

        builder.Build(Call("create_reminder", "reminder", "Remind me at 3pm"), detokenize: false);

        tokenMap.DidNotReceiveWithAnyArgs().Detokenize(default!);
    }

    [Theory]
    [InlineData("recall", "Msg_Assistant_StatusSearchingMemory")]
    [InlineData("remember", "Msg_Assistant_StatusUpdatingMemory")]
    [InlineData("forget", "Msg_Assistant_StatusDeletingMemory")]
    [InlineData("delete_todo", "Msg_Assistant_StatusDeletingTodo")]
    [InlineData("totally_unknown_tool", "Msg_Assistant_StatusProcessing")]
    public void ResolveStatusText_MapsKnownToolsAndFallsBack(string toolName, string expectedKey)
    {
        var builder = CreateBuilder(out _);
        Assert.Equal(expectedKey, builder.ResolveStatusText(toolName));
    }

    [Theory]
    [InlineData("recall")]
    [InlineData("remember")]
    [InlineData("forget")]
    public void ResolveStatusText_MemoryVerbs_AreNotGeneric(string toolName)
    {
        var builder = CreateBuilder(out _);
        Assert.NotEqual("Msg_Assistant_StatusProcessing", builder.ResolveStatusText(toolName));
    }

    [Theory]
    [InlineData("memory", "Msg_Assistant_MemoryUpdated")]
    [InlineData("todo", "Msg_Assistant_TodoUpdated")]
    [InlineData("reminder", "Msg_Assistant_ReminderUpdated")]
    [InlineData("files", "Msg_Assistant_StatusProcessing")]
    public void ResolveSuccessTitle_MapsKnownPluginsAndFallsBack(string pluginName, string expectedKey)
    {
        var builder = CreateBuilder(out _);
        Assert.Equal(expectedKey, builder.ResolveSuccessTitle(pluginName));
    }

    [Fact]
    public void Build_CarriesPluginId_WithAllFourDecisions()
    {
        var builder = CreateBuilder(out _);
        var pluginId = Guid.NewGuid();

        var card = builder.Build(Call("create_todo", "todo", "Create a todo", pluginId: pluginId), detokenize: false);

        Assert.Equal(pluginId, card.PluginId);
        Assert.False(card.IsAutoApproved);

        // Four buttons: the grant tiers ascend in durability after Allow once, so nobody is pushed straight to
        // the permanent one.
        Assert.Equal(4, card.Decisions.Count);
        Assert.Equal("ActionCard_Decline", card.Decisions[0].Label);
        Assert.Equal(DecisionEmphasis.Default, card.Decisions[0].Emphasis);
        Assert.Equal("ActionCard_AllowOnce", card.Decisions[1].Label);
        Assert.Equal(DecisionEmphasis.Primary, card.Decisions[1].Emphasis);
        Assert.Equal("ActionCard_AllowForSession", card.Decisions[2].Label);
        Assert.Equal(DecisionEmphasis.Default, card.Decisions[2].Emphasis);
        Assert.Equal("ActionCard_AlwaysAllow", card.Decisions[3].Label);
        Assert.Equal(DecisionEmphasis.Default, card.Decisions[3].Emphasis);
    }

    /// <summary>Both grant tiers are on every card, an irreversible tool included: withholding the WEAKER of the
    /// two only pushed a user toward the permanent one.</summary>
    [Theory]
    [InlineData("write_file", "files")]
    [InlineData("delete_file", "files")]
    [InlineData("git_stash", "git")]
    [InlineData("create_scheduled_research", "scheduled-research")]
    public void Build_OffersBothGrantTiers_OnEveryCard(string toolName, string pluginName)
    {
        var builder = CreateBuilder(out _);

        var card = builder.Build(Call(toolName, pluginName, "do the thing"), detokenize: false);

        Assert.Equal(4, card.Decisions.Count);
        Assert.Equal("ActionCard_Decline", card.Decisions[0].Label);
        Assert.Equal("ActionCard_AllowOnce", card.Decisions[1].Label);
        Assert.Equal("ActionCard_AllowForSession", card.Decisions[2].Label);
        Assert.Equal("ActionCard_AlwaysAllow", card.Decisions[3].Label);
    }

    [Fact]
    public void Build_AutoApproved_ReturnsPreResolvedAcceptedCard()
    {
        var builder = CreateBuilder(out _);

        var card = builder.Build(Call("create_todo", "todo", "Create a todo"), detokenize: false, autoApprovedAs: ToolGateDecision.AutoApprovedStandingGrant);

        Assert.Equal(ActionCardState.Accepted, card.State);
        Assert.True(card.IsAutoApproved);
        Assert.NotEmpty(card.AutoApprovedStatusText);
        Assert.Equal(card.AutoApprovedStatusText, card.ResolvedStatusText);
    }

    [Theory]
    [InlineData(ToolGateDecision.AutoApprovedStandingGrant, "ActionCard_AutoApproved")]
    [InlineData(ToolGateDecision.AutoApprovedSessionGrant, "ActionCard_AutoApprovedForSession")]
    [InlineData(ToolGateDecision.AutoApprovedPolicy, "ActionCard_AutoApprovedByAutonomy")]
    [InlineData(ToolGateDecision.GrantedByName, "ActionCard_AutoApprovedByRunGrant")]
    [InlineData(ToolGateDecision.AutoApprovedAllowlist, "ActionCard_AutoApproved")]
    public void Build_AutoApproved_NamesTheTierThatApproved(ToolGateDecision decision, string expectedKey)
    {
        var builder = CreateBuilder(out _);

        var card = builder.Build(Call("create_todo", "todo", "Create a todo"), detokenize: false, autoApprovedAs: decision);

        // The title is spelled out rather than read off the card, so a wrong {0} cannot move both sides at once.
        Assert.Equal("ActionCard_Action_Create ActionCard_Category_Todo", card.Title);
        Assert.Equal($"{expectedKey}(ActionCard_Action_Create ActionCard_Category_Todo)", card.AutoApprovedStatusText);
    }

    [Fact]
    public void Build_AutoApprovedByPolicy_DoesNotClaimAStandingGrant()
    {
        var builder = CreateBuilder(out _);

        // The reported defect: autonomy runs write_file, the card says "you always allow", and Tool access is
        // blank because no standing grant was ever written. Equality, not a substring — the autonomy key
        // CONTAINS the standing-grant key, so DoesNotContain would fail on the correct string.
        var card = builder.Build(
            Call("write_file", "files", "Write a file"), detokenize: false,
            autoApprovedAs: ToolGateDecision.AutoApprovedPolicy);

        Assert.Equal($"ActionCard_AutoApprovedByAutonomy({card.Title})", card.AutoApprovedStatusText);
        Assert.NotEqual($"ActionCard_AutoApproved({card.Title})", card.AutoApprovedStatusText);
    }
}
