using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Controls.Cards;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The git confirmation-card mapping: category, per-tool title/action, and the TOOLNAME-based
/// destructive predicate with a distinct warning per destructive tool. The localization mock echoes
/// each key, so a wrong key surfaces directly in the asserted value. Also locks that git write tools stay
/// out of the voice allowlist (via the real ToolPermissionService).
/// </summary>
public sealed class GitActionCardTests
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

    private static PluginToolCall GitCall(string toolName, string description, string? details = null) =>
        new(toolName, Guid.NewGuid(), "git", description, details, () => Task.FromResult<object?>(null));

    [Theory]
    [InlineData("git_init", "ActionCard_Action_Initialize ActionCard_Category_Git")]
    [InlineData("git_add", "ActionCard_Action_Stage ActionCard_Category_Git")]
    [InlineData("git_commit", "ActionCard_Action_Commit ActionCard_Category_Git")]
    [InlineData("git_switch", "ActionCard_Action_Switch ActionCard_Category_Git")]
    [InlineData("git_restore", "ActionCard_Action_Restore ActionCard_Category_Git")]
    [InlineData("git_stash", "ActionCard_Action_Stash ActionCard_Category_Git")]
    public void Build_MapsGitCategoryAndPerToolTitle(string toolName, string expectedTitle)
    {
        var card = CreateBuilder().Build(GitCall(toolName, "do the git thing"), detokenize: false);

        Assert.Equal(ActionCardCategory.Git, card.Category);
        Assert.Equal(expectedTitle, card.Title);
    }

    [Theory]
    [InlineData("git_init")]
    [InlineData("git_add")]
    [InlineData("git_commit")]
    public void Build_NonDestructiveGitTools_HaveNoWarning(string toolName)
    {
        var card = CreateBuilder().Build(GitCall(toolName, "do the git thing"), detokenize: false);

        Assert.False(card.IsDestructive);
        Assert.Null(card.WarningText);
    }

    [Theory]
    [InlineData("git_switch", "Msg_Assistant_GitSwitchWarning")]
    [InlineData("git_restore", "Msg_Assistant_GitRestoreWarning")]
    [InlineData("git_stash", "Msg_Assistant_GitStashWarning")]
    public void Build_DestructiveGitTools_AreDestructiveWithDistinctWarning(string toolName, string expectedWarningKey)
    {
        var card = CreateBuilder().Build(GitCall(toolName, "do the git thing"), detokenize: false);

        Assert.True(card.IsDestructive);
        Assert.Equal(expectedWarningKey, card.WarningText);
    }

    [Fact]
    public void Build_GitCommit_RendersCommandDetail()
    {
        var card = CreateBuilder().Build(
            GitCall("git_commit", "Commit staged changes", "Command: git commit --no-verify -m \"fix bug\""),
            detokenize: false);

        Assert.NotEmpty(card.Details);
        Assert.Contains(card.Details, d => d.Value.Contains("git commit --no-verify"));
    }

    [Fact]
    public void Build_DestructiveGitTool_OffersBothGrantTiers_WithADangerAllowOnce()
    {
        var card = CreateBuilder().Build(GitCall("git_restore", "Discard changes"), detokenize: false);

        Assert.Equal(4, card.Decisions.Count);
        Assert.Contains(card.Decisions, d => d.Label == "ActionCard_AllowForSession");
        Assert.Contains(card.Decisions, d => d.Label == "ActionCard_AlwaysAllow");
        Assert.Equal(DecisionEmphasis.Danger, card.Decisions[1].Emphasis);
    }

    [Theory]
    [InlineData("git_status")]
    [InlineData("git_commit")]
    [InlineData("git_stash")]
    public void ResolveStatusText_GitTools_UseRunningGit(string toolName)
        => Assert.Equal("Msg_Assistant_StatusRunningGit", CreateBuilder().ResolveStatusText(toolName));

    [Fact]
    public void ResolveSuccessTitle_Git_IsGitUpdated()
        => Assert.Equal("Msg_Assistant_GitUpdated", CreateBuilder().ResolveSuccessTitle("git"));

    [Theory]
    [InlineData("git_init")]
    [InlineData("git_add")]
    [InlineData("git_commit")]
    [InlineData("git_switch")]
    [InlineData("git_restore")]
    [InlineData("git_stash")]
    public void GitWriteTools_AreNotAutoApproveEligible(string toolName)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        var permissions = new ToolPermissionService(settings, new SessionToolGrantStore());

        Assert.False(permissions.IsAutoApproveEligible(toolName));
    }
}
