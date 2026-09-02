using System.Collections.ObjectModel;
using Microsoft.Extensions.AI;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Models;

/// <summary>
/// Covers the <see cref="AssistantMessage.CardRows"/> projection: a step's accepted diffs fold into one
/// <see cref="FileChangeSet"/>, pending and declined cards stay standalone, and the append-ordered
/// <see cref="AssistantMessage.ActionCards"/> the tool-summary code reads is never disturbed.
/// </summary>
public class AssistantMessageCardRowsTests
{
    private static ActionCardInfo DiffCard(string path, int added = 1, int removed = 0)
    {
        var lines = new ObservableCollection<DiffLine>();
        for (var i = 0; i < added; i++) lines.Add(new DiffLine(DiffLineKind.Added, "+"));
        for (var i = 0; i < removed; i++) lines.Add(new DiffLine(DiffLineKind.Removed, "-"));

        return new ActionCardInfo
        {
            Title = "t",
            Summary = "s",
            Category = ActionCardCategory.Files,
            ToolName = "write_file",
            FilePath = path,
            DiffLines = lines,
        };
    }

    private static ActionCardInfo AcceptedDiffCard(string path, int added = 1, int removed = 0)
    {
        var card = DiffCard(path, added, removed);
        card.AllowOnceCommand.Execute(null);
        return card;
    }

    private static ActionCardInfo PlainCard() => new()
    {
        Title = "t",
        Summary = "s",
        Category = ActionCardCategory.Todo,
        ToolName = "todo",
    };

    private static AssistantMessage MessageWith(params ActionCardInfo[] cards)
    {
        var msg = new AssistantMessage(ChatRole.Assistant);
        foreach (var card in cards)
            msg.ActionCards.Add(card);
        return msg;
    }

    [Fact]
    public void OneAcceptedDiff_StaysStandalone()
    {
        var card = AcceptedDiffCard("a.cs");
        var msg = MessageWith(card);

        Assert.Equal([card], msg.CardRows);
    }

    [Fact]
    public void SecondAcceptedDiff_FoldsBothIntoOneSet()
    {
        var first = AcceptedDiffCard("a.cs", added: 12, removed: 3);
        var second = AcceptedDiffCard("b.cs", added: 41, removed: 8);
        var msg = MessageWith(first, second);

        var set = Assert.IsType<FileChangeSet>(Assert.Single(msg.CardRows));
        Assert.Equal([first, second], set.Cards);
        Assert.Equal(2, set.FileCount);
        Assert.Equal(53, set.TotalAdded);
        Assert.Equal(11, set.TotalRemoved);
    }

    [Fact]
    public void TheSet_SitsWhereTheFirstFoldedCardWas()
    {
        var plainBefore = PlainCard();
        var first = AcceptedDiffCard("a.cs");
        var plainBetween = PlainCard();
        var second = AcceptedDiffCard("b.cs");
        var msg = MessageWith(plainBefore, first, plainBetween, second);

        Assert.Collection(msg.CardRows,
            row => Assert.Same(plainBefore, row),
            row => Assert.IsType<FileChangeSet>(row),
            row => Assert.Same(plainBetween, row));
    }

    [Fact]
    public void PendingDiffs_StayStandaloneUntilAccepted()
    {
        var first = DiffCard("a.cs");
        var second = DiffCard("b.cs");
        var msg = MessageWith(first, second);

        Assert.Equal([first, second], msg.CardRows);

        first.AllowOnceCommand.Execute(null);
        Assert.Equal([first, second], msg.CardRows);

        second.AllowOnceCommand.Execute(null);
        var set = Assert.IsType<FileChangeSet>(Assert.Single(msg.CardRows));
        Assert.Equal([first, second], set.Cards);
    }

    [Fact]
    public void DeclinedDiffs_AreNeverFolded()
    {
        var first = DiffCard("a.cs", added: 5);
        var second = DiffCard("b.cs", added: 7);
        var msg = MessageWith(first, second);

        first.DeclineCommand.Execute(null);
        second.DeclineCommand.Execute(null);

        Assert.Equal([first, second], msg.CardRows);
    }

    [Fact]
    public void ADeclinedDiff_StaysOutOfAnExistingSet()
    {
        var accepted1 = AcceptedDiffCard("a.cs", added: 4);
        var accepted2 = AcceptedDiffCard("b.cs", added: 6);
        var declined = DiffCard("c.cs", added: 100);
        var msg = MessageWith(accepted1, accepted2, declined);
        declined.DeclineCommand.Execute(null);

        Assert.Collection(msg.CardRows,
            row => Assert.IsType<FileChangeSet>(row),
            row => Assert.Same(declined, row));

        var set = (FileChangeSet)msg.CardRows[0];
        Assert.Equal(10, set.TotalAdded);
    }

    [Fact]
    public void NonDiffCards_AreNeverFolded()
    {
        var a = PlainCard();
        var b = PlainCard();
        a.AllowOnceCommand.Execute(null);
        b.AllowOnceCommand.Execute(null);
        var msg = MessageWith(a, b);

        Assert.Equal([a, b], msg.CardRows);
    }

    [Fact]
    public void FoldState_SurvivesALaterCardArriving()
    {
        var msg = MessageWith(AcceptedDiffCard("a.cs"), AcceptedDiffCard("b.cs"));
        var set = (FileChangeSet)msg.CardRows[0];
        set.IsExpanded = true;

        msg.ActionCards.Add(AcceptedDiffCard("c.cs"));

        Assert.Same(set, Assert.Single(msg.CardRows));
        Assert.True(set.IsExpanded);
        Assert.Equal(3, set.Cards.Count);
    }

    [Fact]
    public void ActionCards_KeepArrivalOrderAndTheirLastElement()
    {
        var first = AcceptedDiffCard("a.cs");
        var second = AcceptedDiffCard("b.cs");
        var last = PlainCard();
        var msg = MessageWith(first, second, last);

        Assert.Equal([first, second, last], msg.ActionCards);
        Assert.Same(last, msg.ActionCards[^1]);
    }

    [Fact]
    public void TheSameCardInstanceTwice_DoesNotBreakTheProjection()
    {
        // ChatSession's card builder mints one instance per call, but the gate tests substitute a
        // builder that hands back the same card for both calls of a tool.
        var card = PlainCard();
        var msg = MessageWith(card, card);

        Assert.Equal([card, card], msg.CardRows);
    }

    [Fact]
    public void FoldedCards_StayReachableThroughActionCards()
    {
        var card = AcceptedDiffCard("a.cs");
        var msg = MessageWith(card, AcceptedDiffCard("b.cs"));

        Assert.Contains(card, msg.ActionCards);
        Assert.DoesNotContain(card, msg.CardRows);
    }
}
