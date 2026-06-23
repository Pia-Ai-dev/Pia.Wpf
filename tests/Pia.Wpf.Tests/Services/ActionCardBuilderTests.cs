using NSubstitute;
using Pia.Controls.Cards;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Covers the tool-call → UI-string mapping extracted from AssistantViewModel:
/// confirmation-card shape, delete warnings, and the status/success lookups.
/// The localization mock echoes each key, so a wrong key surfaces directly in
/// the asserted value (guarding the moved string literals).
/// </summary>
public class ActionCardBuilderTests
{
    private static ActionCardBuilder CreateBuilder(out ITokenMapService tokenMap)
        => CreateBuilder(out tokenMap, out _);

    private static ActionCardBuilder CreateBuilder(out ITokenMapService tokenMap, out IToolPermissionService permissions)
    {
        var localization = Substitute.For<ILocalizationService>();
        localization[Arg.Any<string>()].Returns(ci => ci.Arg<string>());
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => $"{ci.ArgAt<string>(0)}({string.Join(",", ci.ArgAt<object[]>(1))})");

        tokenMap = Substitute.For<ITokenMapService>();
        permissions = Substitute.For<IToolPermissionService>();
        // Eligibility classification is exercised by ToolPermissionServiceTests; the builder
        // tests stub just the names they need to verify the triad-vs-pair mapping.
        permissions.IsAutoApproveEligible("create_todo").Returns(true);
        permissions.IsAutoApproveEligible("write_file").Returns(false);
        permissions.IsAutoApproveEligible("delete_file").Returns(false);
        return new ActionCardBuilder(localization, tokenMap, permissions);
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
    public void Build_WhenDetokenizeFalse_DoesNotTouchTokenMap()
    {
        var builder = CreateBuilder(out var tokenMap);

        builder.Build(Call("create_reminder", "reminder", "Remind me at 3pm"), detokenize: false);

        tokenMap.DidNotReceiveWithAnyArgs().Detokenize(default!);
    }

    [Theory]
    [InlineData("query_memory", "Msg_Assistant_StatusSearchingMemory")]
    [InlineData("delete_todo", "Msg_Assistant_StatusDeletingTodo")]
    [InlineData("totally_unknown_tool", "Msg_Assistant_StatusProcessing")]
    public void ResolveStatusText_MapsKnownToolsAndFallsBack(string toolName, string expectedKey)
    {
        var builder = CreateBuilder(out _);
        Assert.Equal(expectedKey, builder.ResolveStatusText(toolName));
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
    public void Build_EligibleTool_CarriesPluginIdAndIsAutoApprovable_WithTriadDecisions()
    {
        var builder = CreateBuilder(out _, out _);
        var pluginId = Guid.NewGuid();

        var card = builder.Build(Call("create_todo", "todo", "Create a todo", pluginId: pluginId), detokenize: false);

        Assert.Equal(pluginId, card.PluginId);
        Assert.True(card.IsAutoApprovable);
        Assert.False(card.IsAutoApproved);

        // Triad: Decline (Default), Allow once (Primary), Always allow (Default).
        Assert.Equal(3, card.Decisions.Count);
        Assert.Equal("ActionCard_Decline", card.Decisions[0].Label);
        Assert.Equal(DecisionEmphasis.Default, card.Decisions[0].Emphasis);
        Assert.Equal("ActionCard_AllowOnce", card.Decisions[1].Label);
        Assert.Equal(DecisionEmphasis.Primary, card.Decisions[1].Emphasis);
        Assert.Equal("ActionCard_AlwaysAllow", card.Decisions[2].Label);
        Assert.Equal(DecisionEmphasis.Default, card.Decisions[2].Emphasis);
    }

    [Theory]
    [InlineData("write_file", "files")]
    [InlineData("delete_file", "files")]
    public void Build_IneligibleTool_HasDeclineAndAllowOncePair_NoAlwaysAllow(string toolName, string pluginName)
    {
        var builder = CreateBuilder(out _, out _);

        var card = builder.Build(Call(toolName, pluginName, "do the thing"), detokenize: false);

        Assert.False(card.IsAutoApprovable);
        Assert.Equal(2, card.Decisions.Count);
        Assert.Equal("ActionCard_Decline", card.Decisions[0].Label);
        Assert.Equal("ActionCard_AllowOnce", card.Decisions[1].Label);
        Assert.DoesNotContain(card.Decisions, d => d.Label == "ActionCard_AlwaysAllow");
    }

    [Fact]
    public void Build_AutoApproved_ReturnsPreResolvedAcceptedCard()
    {
        var builder = CreateBuilder(out _, out _);

        var card = builder.Build(Call("create_todo", "todo", "Create a todo"), detokenize: false, autoApproved: true);

        Assert.Equal(ActionCardState.Accepted, card.State);
        Assert.True(card.IsAutoApproved);
        Assert.NotEmpty(card.AutoApprovedStatusText);
        Assert.Equal(card.AutoApprovedStatusText, card.ResolvedStatusText);
    }
}
