using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// 04 §0.6: <c>scheduled-research</c> is a BUILT-IN plugin (BuiltInPluginDefaults) that was missing from the
/// card builder's plugin-name switch, so its cards fell into the <c>_ =&gt; Mcp</c> bucket. The gate defended
/// (eligibility was false, and AlwaysAllow silently degraded to AllowOnce) but the UI lied three ways: it was
/// titled "External tool", it rendered an "Always allow" button on a built-in scheduling tool, and it parsed
/// key/value detail TEXT with a JSON parser so no detail rows appeared at all. Nothing pinned any of it.
/// </summary>
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
        // The user-visible half of §0.6: a pair (Decline / Allow once), not the triad.
        Assert.Equal(2, card.Decisions.Count);
        Assert.DoesNotContain(card.Decisions, d => ReferenceEquals(d.Command, card.AlwaysAllowCommand));
    }

    [Fact]
    public void ScheduledResearchCard_ParsesItsKeyValueDetails()
    {
        // What ScheduledJobToolHandler actually builds: "Label: value" lines, not JSON. Under the old Mcp
        // categorization these went through JsonHelper.ParseToDetails and yielded nothing.
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
        // isDestructive is unchanged by this batch: delete_scheduled_research is delete-like by name, so its
        // warning still resolves through the isDelete branch to the generic external-delete string. Recorded
        // rather than "fixed" — a scheduling-specific warning would be a fourth locale triple.
        var card = CreateBuilder().Build(Call("delete_scheduled_research"), detokenize: false);

        Assert.True(card.IsDestructive);
        Assert.Equal("Msg_Assistant_PermanentDeleteExternal", card.WarningText);
    }

    /// <summary>
    /// T-CARD-5, the regression guard for the divergence 04 R16 names: the card's own eligibility copy and the
    /// gate's used to be two independent expressions over two different notions of "MCP".
    /// </summary>
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

        // The class the builder guesses from the name alone is the one the gate would derive for a non-MCP
        // route; both then run the same IsStandingGrantOfferable.
        var expected = ToolAutonomy.IsStandingGrantOfferable(
            ToolClassifier.ClassifyPresumedExternal(pluginName), toolName,
            permissions.IsAutoApproveEligible(toolName));

        Assert.Equal(expected, card.IsAutoApprovable);
    }
}
