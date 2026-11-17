using System.Collections.ObjectModel;
using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

public class RunBlockPlacementTests
{
    private static readonly DateTime T0 = new(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc);

    // Messages carry local time, as hydrated chats do; runs carry UTC, as the store does.
    private static AssistantMessage Msg(int minutes, ChatRole? role = null) =>
        new(Guid.NewGuid(), role ?? ChatRole.Assistant, "x", T0.AddMinutes(minutes).ToLocalTime());

    [Fact]
    public void ABlock_SitsBeforeTheFirstMessageTheJobWrote()
    {
        var goal = Msg(0, ChatRole.User);
        var reply = Msg(5);
        var slot = RunBlockPlacement.Place(reply.Id, null, T0.AddMinutes(1), true, [goal, reply]);

        Assert.Equal(new RunBlockSlot(reply.Id, Before: true), slot);
    }

    [Fact]
    public void WithItsFirstMessageGone_TheLastOneAnchorsIt()
    {
        var last = Msg(5);
        var slot = RunBlockPlacement.Place(Guid.NewGuid(), last.Id, T0, false, [Msg(0), last]);

        Assert.Equal(new RunBlockSlot(last.Id, Before: true), slot);
    }

    [Fact]
    public void AJobThatWroteNothing_SitsAfterTheLastMessageOlderThanIt()
    {
        var goal = Msg(0, ChatRole.User);
        var later = Msg(10);
        var slot = RunBlockPlacement.Place(null, null, T0.AddMinutes(1), true, [goal, later]);

        Assert.Equal(new RunBlockSlot(goal.Id, Before: false), slot);
    }

    [Fact]
    public void AFinishedJobWithNoMessagesLeft_HasNoPlace()
    {
        Assert.Null(RunBlockPlacement.Place(Guid.NewGuid(), Guid.NewGuid(), T0, false, [Msg(0)]));
    }

    [Fact]
    public void ALiveJobInAnEmptyChat_HasNoAnchor()
    {
        Assert.Null(RunBlockPlacement.Place(null, null, T0, true, []));
    }

    [Fact]
    public void Interleave_PutsBlocksBesideTheirAnchors_AndLeavesOutAnchorsOutsideTheWindow()
    {
        var a = Msg(0);
        var b = Msg(1);
        var hidden = Msg(-5);
        object before = "before-b", after = "after-a", outside = "outside", tail = "tail";

        var items = RunBlockPlacement.Interleave([a, b],
            [(before, new RunBlockSlot(b.Id, true)), (after, new RunBlockSlot(a.Id, false)),
             (outside, new RunBlockSlot(hidden.Id, true))],
            [tail]);

        Assert.Equal([a, after, before, b, tail], items);
    }

    [Fact]
    public void Apply_KeepsUnchangedItems_AndEditsOnlyTheDifference()
    {
        var a = Msg(0);
        var b = Msg(1);
        var c = Msg(2);
        object block = "block";
        var target = new ObservableCollection<object> { a, b };
        var changes = 0;
        target.CollectionChanged += (_, _) => changes++;

        RunBlockPlacement.Apply(target, [a, block, b, c]);
        Assert.Equal([a, block, b, c], target);
        Assert.Equal(2, changes);

        RunBlockPlacement.Apply(target, [a, b, c, block]);
        Assert.Equal([a, b, c, block], target);

        RunBlockPlacement.Apply(target, [c]);
        Assert.Equal([c], target);
    }

    [Fact]
    public void FinishedRunsToShow_KeepsOnlyFinishedPlannedRunsWithAMessageLeft()
    {
        var kept = Msg(1);
        var chatId = Guid.NewGuid();
        AgentRun Run(AgentRunState state, Guid? first, RunShape shape = RunShape.Planned, Guid? parent = null) => new()
        {
            Id = Guid.NewGuid(), ChatId = chatId, State = state, RunShape = shape, FirstMessageId = first,
            ParentRunId = parent, CreatedAt = T0,
        };

        var shown = Run(AgentRunState.Completed, kept.Id);
        var runs = new[]
        {
            shown,
            Run(AgentRunState.Completed, Guid.NewGuid()),                // its messages were deleted
            Run(AgentRunState.Cancelled, null),                          // never wrote anything
            Run(AgentRunState.Paused, kept.Id),                          // unfinished: restored as the active run
            Run(AgentRunState.Completed, kept.Id, RunShape.SingleTurn),
            Run(AgentRunState.Completed, kept.Id, parent: Guid.NewGuid()),
        };

        Assert.Equal([shown.Id], ChatSessionManager.FinishedRunsToShow(runs, [Msg(0), kept]));
    }
}
