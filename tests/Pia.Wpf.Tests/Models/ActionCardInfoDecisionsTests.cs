using Pia.Controls.Cards;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Models;

/// <summary>
/// Covers the <see cref="ActionCardInfo.Decisions"/> bar (Spec 2, design §7/§8). The button set is keyed
/// off <see cref="ActionCardInfo.IsAutoApprovable"/>, never <see cref="ActionCardInfo.IsDestructive"/>:
/// an auto-approvable card offers the triad [Decline (Default), Allow once (Primary), Always allow (Default)];
/// a non-eligible card offers the pair [Decline (Default), Allow once] with the Allow once button styled
/// Danger when the action is destructive. The decisions bind to the Decline/AllowOnce/AlwaysAllow commands.
/// </summary>
public class ActionCardInfoDecisionsTests
{
    private static ActionCardInfo NewCard(bool isAutoApprovable, bool isDestructive) => new()
    {
        Title = "t",
        Summary = "s",
        Category = ActionCardCategory.Todo,
        ToolName = "todo",
        IsAutoApprovable = isAutoApprovable,
        IsDestructive = isDestructive,
        DeclineLabel = "Decline",
        AllowOnceLabel = "Allow once",
        AlwaysAllowLabel = "Always allow",
    };

    [Fact]
    public void Decisions_AutoApprovableCard_AreDeclineAllowOnceAlwaysAllow()
    {
        var card = NewCard(isAutoApprovable: true, isDestructive: false);

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
        var card = NewCard(isAutoApprovable: false, isDestructive: false);

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
        var card = NewCard(isAutoApprovable: false, isDestructive: true);

        Assert.Equal(2, card.Decisions.Count);
        Assert.Equal(DecisionEmphasis.Danger, card.Decisions[1].Emphasis);
    }

    [Fact]
    public void Decisions_AutoApprovableCard_IgnoresDestructive_AllowOnceStaysPrimary()
    {
        // Eligibility wins over the destructive heuristic for the button set: an auto-approvable
        // card never styles Allow once as Danger (design §8 — Decisions key off IsAutoApprovable only).
        var card = NewCard(isAutoApprovable: true, isDestructive: true);

        Assert.Equal(3, card.Decisions.Count);
        Assert.Equal(DecisionEmphasis.Primary, card.Decisions[1].Emphasis);
    }
}
