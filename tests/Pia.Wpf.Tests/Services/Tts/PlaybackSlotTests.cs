using Pia.Services.Tts;
using Xunit;

namespace Pia.Tests.Services.Tts;

public sealed class PlaybackSlotTests
{
    [Fact]
    public void Begin_CancelsThePreviousOwner()
    {
        var slot = new PlaybackSlot();
        var a = slot.Begin(TestContext.Current.CancellationToken);
        var b = slot.Begin(TestContext.Current.CancellationToken);

        Assert.True(a.IsCancellationRequested);
        Assert.False(b.IsCancellationRequested);
    }

    [Fact]
    public void End_OfASupersededOwner_LeavesTheCurrentOneCancellable()
    {
        var slot = new PlaybackSlot();
        var a = slot.Begin(TestContext.Current.CancellationToken);
        var b = slot.Begin(TestContext.Current.CancellationToken);

        Assert.False(slot.End(a));
        slot.Cancel();
        Assert.True(b.IsCancellationRequested);
    }

    [Fact]
    public void End_OfTheCurrentOwner_ReturnsTrue_AndCancelAfterwardsIsANoOp()
    {
        var slot = new PlaybackSlot();
        var a = slot.Begin(TestContext.Current.CancellationToken);

        Assert.True(slot.End(a));
        slot.Cancel();
    }
}
