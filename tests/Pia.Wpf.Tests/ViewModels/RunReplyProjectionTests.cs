using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

public class RunReplyProjectionTests
{
    private static readonly DateTime T0 = new(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid RunId = Guid.NewGuid();
    private static readonly Guid StepA = Guid.NewGuid();
    private static readonly Guid StepB = Guid.NewGuid();

    private static AssistantMessage Msg(int minutes, ChatRole? role = null) =>
        new(Guid.NewGuid(), role ?? ChatRole.Assistant, "x", T0.AddMinutes(minutes));

    private static Dictionary<Guid, RunMessageRecord> Records(params (AssistantMessage Message, Guid? Step)[] rows) =>
        rows.ToDictionary(r => r.Message.Id, r => new RunMessageRecord(r.Message.Id, RunId, r.Step, 0, T0));

    [Fact]
    public void StepReplies_MoveIntoTheBlock_AndTheLastOneStaysTheResult()
    {
        var goal = Msg(0, ChatRole.User);
        var a = Msg(1);
        var b = Msg(2);

        var projected = RunReplyProjection.Project([goal, a, b], Records((a, StepA), (b, StepB)), new HashSet<Guid> { RunId });

        var reply = Assert.Single(projected[RunId]);
        Assert.Equal(new RunReply(StepA, a), reply);
    }

    [Fact]
    public void WithoutABlockShown_NothingMoves()
    {
        var a = Msg(1);
        var b = Msg(2);

        var projected = RunReplyProjection.Project([a, b], Records((a, StepA), (b, StepB)), new HashSet<Guid>());

        Assert.Empty(projected);
    }

    [Fact]
    public void AReplyWithoutAStep_StaysABubble_ButStillCountsAsTheLatest()
    {
        var a = Msg(1);
        var question = Msg(2);

        var projected = RunReplyProjection.Project([a, question], Records((a, StepA), (question, null)),
            new HashSet<Guid> { RunId });

        Assert.Equal([new RunReply(StepA, a)], projected[RunId]);
    }

    [Fact]
    public void AMessageWithoutARecord_IsNeverMoved()
    {
        var a = Msg(1);
        var typed = Msg(2, ChatRole.User);
        var b = Msg(3);

        var projected = RunReplyProjection.Project([a, typed, b], Records((a, StepA), (b, StepB)),
            new HashSet<Guid> { RunId });

        Assert.DoesNotContain(projected[RunId], r => r.Message == typed);
    }

    [Fact]
    public void AHiddenMessage_StillAnchorsItsBlock()
    {
        var goal = Msg(0, ChatRole.User);
        var a = Msg(1);
        var block = new object();

        var items = RunBlockPlacement.Interleave([goal, a], [(block, new RunBlockSlot(a.Id, Before: true))], [],
            new HashSet<Guid> { a.Id });

        Assert.Equal([goal, block], items);
    }
}
