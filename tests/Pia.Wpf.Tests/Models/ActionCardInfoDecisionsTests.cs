using Pia.Controls.Cards;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Models;

/// <summary>
/// Covers the <see cref="ActionCardInfo.Decisions"/> bar (Spec 2, design §7/§8; hermes #15 for the third
/// tier). Decline first, then Allow once, then the GRANT tiers in ascending durability — each keyed off its
/// OWN offerability flag and never off <see cref="ActionCardInfo.IsDestructive"/>:
/// <see cref="ActionCardInfo.IsSessionGrantable"/> adds "Allow this session",
/// <see cref="ActionCardInfo.IsAutoApprovable"/> adds "Always allow". A card offering neither shows the pair,
/// with Allow once styled Danger when the action is destructive.
/// </summary>
public class ActionCardInfoDecisionsTests
{
    // isSessionGrantable is NOT defaulted: hermes #15 made it a dimension of the button set, and a defaulted
    // parameter would have let every case below keep asserting the old counts while silently never exercising
    // the new axis.
    private static ActionCardInfo NewCard(bool isAutoApprovable, bool isDestructive, bool isSessionGrantable) => new()
    {
        Title = "t",
        Summary = "s",
        Category = ActionCardCategory.Todo,
        ToolName = "todo",
        IsAutoApprovable = isAutoApprovable,
        IsSessionGrantable = isSessionGrantable,
        IsDestructive = isDestructive,
        DeclineLabel = "Decline",
        AllowOnceLabel = "Allow once",
        AlwaysAllowLabel = "Always allow",
        AllowForSessionLabel = "Allow this session",
    };

    [Fact]
    public void Decisions_AutoApprovableCard_AreDeclineAllowOnceAlwaysAllow()
    {
        var card = NewCard(isAutoApprovable: true, isDestructive: false, isSessionGrantable: false);

        Assert.Equal(3, card.Decisions.Count);

        Assert.Equal("Decline", card.Decisions[0].Label);
        Assert.Equal(DecisionEmphasis.Default, card.Decisions[0].Emphasis);
        Assert.Same(card.DeclineCommand, card.Decisions[0].Command);

        Assert.Equal("Allow once", card.Decisions[1].Label);
        Assert.Equal(DecisionEmphasis.Primary, card.Decisions[1].Emphasis);
        Assert.Same(card.AllowOnceCommand, card.Decisions[1].Command);

        Assert.Equal("Always allow", card.Decisions[2].Label);
        Assert.Equal(DecisionEmphasis.Default, card.Decisions[2].Emphasis);
        Assert.Same(card.AlwaysAllowCommand, card.Decisions[2].Command);
    }

    [Fact]
    public void Decisions_NonEligibleCard_AreDeclineThenAllowOncePrimary_NoAlwaysAllow()
    {
        var card = NewCard(isAutoApprovable: false, isDestructive: false, isSessionGrantable: false);

        Assert.Equal(2, card.Decisions.Count);

        Assert.Equal("Decline", card.Decisions[0].Label);
        Assert.Equal(DecisionEmphasis.Default, card.Decisions[0].Emphasis);
        Assert.Same(card.DeclineCommand, card.Decisions[0].Command);

        Assert.Equal("Allow once", card.Decisions[1].Label);
        Assert.Equal(DecisionEmphasis.Primary, card.Decisions[1].Emphasis);
        Assert.Same(card.AllowOnceCommand, card.Decisions[1].Command);
    }

    [Fact]
    public void Decisions_NonEligibleDestructiveCard_AllowOnceEmphasisIsDanger()
    {
        var card = NewCard(isAutoApprovable: false, isDestructive: true, isSessionGrantable: false);

        Assert.Equal(2, card.Decisions.Count);
        Assert.Equal(DecisionEmphasis.Danger, card.Decisions[1].Emphasis);
    }

    [Fact]
    public void Decisions_AutoApprovableCard_IgnoresDestructive_AllowOnceStaysPrimary()
    {
        // Eligibility wins over the destructive heuristic for the button set: an auto-approvable
        // card never styles Allow once as Danger (design §8 — Decisions key off IsAutoApprovable only).
        var card = NewCard(isAutoApprovable: true, isDestructive: true, isSessionGrantable: false);

        Assert.Equal(3, card.Decisions.Count);
        Assert.Equal(DecisionEmphasis.Primary, card.Decisions[1].Emphasis);
    }

    // --------------------------------------------- hermes #15: the third tier on the bar

    /// <summary>
    /// T-SESS-6. An allowlisted or external tool (e.g. <c>create_todo</c>) offers all three tiers, in
    /// ascending durability: once → this session → always. The ORDER is asserted, not just the count — the bar
    /// is how a user tells the tiers apart, and "always" sitting where "this session" is expected is the one
    /// mistake that cannot be undone by clicking again.
    /// </summary>
    [Fact]
    public void Decisions_AllTiersOfferable_AreDeclineAllowOnceThisSessionAlwaysAllow()
    {
        var card = NewCard(isAutoApprovable: true, isDestructive: false, isSessionGrantable: true);

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

    /// <summary>
    /// T-SESS-7, the gap #15 closes. <c>write_file</c> is session-grantable and NOT standing-grantable, so its
    /// card gains the middle tier and still refuses to offer a permanent grant.
    /// </summary>
    [Fact]
    public void Decisions_SessionGrantableButNotAutoApprovable_OffersThisSession_AndNeverAlwaysAllow()
    {
        var card = NewCard(isAutoApprovable: false, isDestructive: false, isSessionGrantable: true);

        Assert.Equal(3, card.Decisions.Count);
        Assert.Equal("Allow this session", card.Decisions[2].Label);
        Assert.Same(card.AllowForSessionCommand, card.Decisions[2].Command);
        Assert.DoesNotContain(card.Decisions, d => d.Label == "Always allow");
    }

    /// <summary>
    /// T-SESS-8. Pressing it resolves the card ONCE with the session decision, and a second press changes
    /// nothing (first-press-wins, like the other three) — so a double-click cannot turn one grant into two
    /// decisions on a card the gate has already read.
    /// </summary>
    [Fact]
    public async Task AllowForSession_ResolvesWithTheSessionDecision_AndIsFirstPressWins()
    {
        var card = NewCard(isAutoApprovable: false, isDestructive: false, isSessionGrantable: true);
        var wait = card.WaitForUserDecisionAsync();

        card.AllowForSessionCommand.Execute(null);
        card.DeclineCommand.Execute(null); // ignored: already resolved

        Assert.Equal(ToolDecision.AllowForSession, await wait);
        Assert.Equal(ActionCardState.Accepted, card.State);
        Assert.False(card.IsExpanded);
        Assert.False(card.IsDiffExpanded);
    }
}
