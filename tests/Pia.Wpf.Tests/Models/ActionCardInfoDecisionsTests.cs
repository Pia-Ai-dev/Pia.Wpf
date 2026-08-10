using Pia.Controls.Cards;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Models;

/// <summary>The decision bar is the same four buttons on every card. <see cref="ActionCardInfo.IsDestructive"/>
/// only styles Allow once — it withholds no tier.</summary>
public class ActionCardInfoDecisionsTests
{
    private static ActionCardInfo NewCard(bool isDestructive) => new()
    {
        Title = "t",
        Summary = "s",
        Category = ActionCardCategory.Todo,
        ToolName = "todo",
        IsDestructive = isDestructive,
        DeclineLabel = "Decline",
        AllowOnceLabel = "Allow once",
        AlwaysAllowLabel = "Always allow",
        AllowForSessionLabel = "Allow this session",
    };

    /// <summary>The ORDER is asserted, not just the count: "always" sitting where "this session" is expected
    /// is the one mistake a user cannot undo by clicking again.</summary>
    [Fact]
    public void Decisions_AreDeclineAllowOnceThisSessionAlwaysAllow()
    {
        var card = NewCard(isDestructive: false);

        Assert.Equal(4, card.Decisions.Count);

        Assert.Equal("Decline", card.Decisions[0].Label);
        Assert.Same(card.DeclineCommand, card.Decisions[0].Command);

        Assert.Equal("Allow once", card.Decisions[1].Label);
        Assert.Equal(DecisionEmphasis.Primary, card.Decisions[1].Emphasis);
        Assert.Same(card.AllowOnceCommand, card.Decisions[1].Command);

        Assert.Equal("Allow this session", card.Decisions[2].Label);
        Assert.Equal(DecisionEmphasis.Default, card.Decisions[2].Emphasis);
        Assert.Same(card.AllowForSessionCommand, card.Decisions[2].Command);

        Assert.Equal("Always allow", card.Decisions[3].Label);
        Assert.Equal(DecisionEmphasis.Default, card.Decisions[3].Emphasis);
        Assert.Same(card.AlwaysAllowCommand, card.Decisions[3].Command);
    }

    /// <summary>A destructive tool is offered BOTH grant tiers — the Tool access row carries a caution once one is
    /// ticked instead of the button being withheld — so Danger keys off the destructive flag alone.</summary>
    [Fact]
    public void Decisions_DestructiveCard_OffersBothGrantTiers_AndStylesAllowOnceAsDanger()
    {
        var card = NewCard(isDestructive: true);

        Assert.Equal(4, card.Decisions.Count);
        Assert.Equal(DecisionEmphasis.Danger, card.Decisions[1].Emphasis);
        Assert.Same(card.AllowForSessionCommand, card.Decisions[2].Command);
        Assert.Same(card.AlwaysAllowCommand, card.Decisions[3].Command);
    }

    /// <summary>First-press-wins, so a double-click cannot turn one grant into two decisions on a card the gate
    /// has already read.</summary>
    [Fact]
    public async Task AllowForSession_ResolvesWithTheSessionDecision_AndIsFirstPressWins()
    {
        var card = NewCard(isDestructive: false);
        var wait = card.WaitForUserDecisionAsync();

        card.AllowForSessionCommand.Execute(null);
        card.DeclineCommand.Execute(null); // ignored: already resolved

        Assert.Equal(ToolDecision.AllowForSession, await wait);
        Assert.Equal(ActionCardState.Accepted, card.State);
        Assert.False(card.IsExpanded);
        Assert.False(card.IsDiffExpanded);
    }
}
