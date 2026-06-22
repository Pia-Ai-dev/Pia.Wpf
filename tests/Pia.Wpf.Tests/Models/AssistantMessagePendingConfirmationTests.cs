using System.ComponentModel;
using Microsoft.Extensions.AI;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Models;

public class AssistantMessagePendingConfirmationTests
{
    private static ActionCardInfo NewCard() => new()
    {
        Title = "t",
        Summary = "s",
        Category = ActionCardCategory.Todo,
        ToolName = "todo",
    };

    private static List<string> CapturePropertyChanges(AssistantMessage msg)
    {
        var raised = new List<string>();
        msg.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is { } name)
                raised.Add(name);
        };
        return raised;
    }

    [Fact]
    public void NoCards_HasPendingConfirmation_False()
    {
        var msg = new AssistantMessage(ChatRole.Assistant);
        Assert.False(msg.HasPendingConfirmation);
    }

    [Fact]
    public void AddPendingCard_FlipsTrue_AndRaisesPropertyChanged()
    {
        var msg = new AssistantMessage(ChatRole.Assistant);
        var raised = CapturePropertyChanges(msg);

        msg.ActionCards.Add(NewCard());

        Assert.True(msg.HasPendingConfirmation);
        Assert.Contains(nameof(AssistantMessage.HasPendingConfirmation), raised);
    }

    [Fact]
    public void AcceptCard_FlipsFalse_AndRaisesPropertyChanged()
    {
        var msg = new AssistantMessage(ChatRole.Assistant);
        var card = NewCard();
        msg.ActionCards.Add(card);
        var raised = CapturePropertyChanges(msg);

        card.AcceptCommand.Execute(null);

        Assert.False(msg.HasPendingConfirmation);
        Assert.Contains(nameof(AssistantMessage.HasPendingConfirmation), raised);
    }

    [Fact]
    public void DeclineCard_FlipsFalse()
    {
        var msg = new AssistantMessage(ChatRole.Assistant);
        var card = NewCard();
        msg.ActionCards.Add(card);

        card.DeclineCommand.Execute(null);

        Assert.False(msg.HasPendingConfirmation);
    }

    [Fact]
    public void MultipleCards_StaysTrueUntilAllResolved()
    {
        var msg = new AssistantMessage(ChatRole.Assistant);
        var first = NewCard();
        var second = NewCard();
        msg.ActionCards.Add(first);
        msg.ActionCards.Add(second);
        var raised = CapturePropertyChanges(msg);

        first.AcceptCommand.Execute(null);
        Assert.True(msg.HasPendingConfirmation);

        second.AcceptCommand.Execute(null);
        Assert.False(msg.HasPendingConfirmation);
        Assert.Contains(nameof(AssistantMessage.HasPendingConfirmation), raised);
    }

    [Fact]
    public void RemovedCard_DetachesSubscription()
    {
        var msg = new AssistantMessage(ChatRole.Assistant);
        var card = NewCard();
        msg.ActionCards.Add(card);

        msg.ActionCards.Remove(card);

        // The Remove itself raises HasPendingConfirmation once; snapshot the count after
        // that so we can prove mutating the detached card raises nothing further.
        var raised = new List<string>();
        msg.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AssistantMessage.HasPendingConfirmation))
                raised.Add(e.PropertyName);
        };

        card.State = ActionCardState.Accepted;

        Assert.Empty(raised);
    }
}
