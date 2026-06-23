using Pia.Controls.Cards;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Models;

/// <summary>
/// Covers the binary <see cref="ActionCardInfo.Decisions"/> bar (design §6): a Decline (Default)
/// followed by an Accept whose emphasis is Primary normally and Danger when the action is destructive.
/// The decisions bind to the existing Decline/Accept commands; the gate/TCS is untouched.
/// </summary>
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
        AcceptLabel = "Accept",
    };

    [Fact]
    public void Decisions_NormalCard_AreDeclineDefaultThenAcceptPrimary()
    {
        var card = NewCard(isDestructive: false);

        Assert.Equal(2, card.Decisions.Count);

        Assert.Equal("Decline", card.Decisions[0].Label);
        Assert.Equal(DecisionEmphasis.Default, card.Decisions[0].Emphasis);
        Assert.Same(card.DeclineCommand, card.Decisions[0].Command);

        Assert.Equal("Accept", card.Decisions[1].Label);
        Assert.Equal(DecisionEmphasis.Primary, card.Decisions[1].Emphasis);
        Assert.Same(card.AcceptCommand, card.Decisions[1].Command);
    }

    [Fact]
    public void Decisions_DestructiveCard_AcceptEmphasisIsDanger()
    {
        var card = NewCard(isDestructive: true);

        Assert.Equal(DecisionEmphasis.Danger, card.Decisions[1].Emphasis);
    }
}
