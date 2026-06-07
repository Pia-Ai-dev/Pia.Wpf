using NSubstitute;
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
    {
        var localization = Substitute.For<ILocalizationService>();
        localization[Arg.Any<string>()].Returns(ci => ci.Arg<string>());
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => $"{ci.ArgAt<string>(0)}({string.Join(",", ci.ArgAt<object[]>(1))})");

        tokenMap = Substitute.For<ITokenMapService>();
        return new ActionCardBuilder(localization, tokenMap);
    }

    private static PluginToolCall Call(string toolName, string pluginName, string description, string? details = null) =>
        new(toolName, pluginName, description, details, () => Task.FromResult<object?>(null));

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
        // remember maps to the Update action, NOT the default "Create Memory" title.
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
        // forget maps to the Delete action, NOT the default "Create Memory" title.
        Assert.Equal("ActionCard_Action_Delete ActionCard_Category_Memory", card.Title);
        Assert.NotEqual("ActionCard_Action_Create ActionCard_Category_Memory", card.Title);
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
}
