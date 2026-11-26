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

    [Fact]
    public void End_OfASupersededPlayingOwner_KeepsTheNewerOwnerPlaying()
    {
        var slot = new PlaybackSlot();
        var a = slot.Begin(TestContext.Current.CancellationToken);
        Assert.True(slot.Start(a));
        var b = slot.Begin(TestContext.Current.CancellationToken);
        Assert.True(slot.Start(b));

        Assert.False(slot.End(a));
        Assert.True(slot.IsPlaying);

        Assert.True(slot.End(b));
        Assert.False(slot.IsPlaying);
    }

    [Fact]
    public void End_OfASupersededOwner_BeforeTheNewerOneStarts_LetsItStartPlaying()
    {
        var slot = new PlaybackSlot();
        var a = slot.Begin(TestContext.Current.CancellationToken);
        Assert.True(slot.Start(a));
        var b = slot.Begin(TestContext.Current.CancellationToken);

        Assert.False(slot.End(a));
        Assert.True(slot.Start(b));
        Assert.True(slot.IsPlaying);

        Assert.True(slot.End(b));
        Assert.False(slot.IsPlaying);
    }

    [Fact]
    public void Start_OfASupersededOwner_DoesNotMarkPlaying()
    {
        var slot = new PlaybackSlot();
        var a = slot.Begin(TestContext.Current.CancellationToken);
        slot.Begin(TestContext.Current.CancellationToken);

        Assert.False(slot.Start(a));
        Assert.False(slot.IsPlaying);
    }

    [Fact]
    public async Task End_OfTheOldOwner_RacingANewOwnersStart_LeavesTheNewOwnerPlaying()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeout = TimeSpan.FromSeconds(10);

        for (var i = 0; i < 500; i++)
        {
            var slot = new PlaybackSlot();
            var a = slot.Begin(ct);
            slot.Start(a);
            CancellationTokenSource? b = null;
            using var barrier = new Barrier(2);

            var endA = Task.Run(() =>
            {
                Assert.True(barrier.SignalAndWait(timeout, ct));
                slot.End(a);
            }, ct);
            var startB = Task.Run(() =>
            {
                Assert.True(barrier.SignalAndWait(timeout, ct));
                b = slot.Begin(ct);
                Assert.True(slot.Start(b));
            }, ct);

            await Task.WhenAll(endA, startB).WaitAsync(timeout, ct);

            Assert.True(slot.IsPlaying, $"iteration {i}");
            Assert.True(slot.End(b!));
            Assert.False(slot.IsPlaying);
        }
    }

    [Fact]
    public void Cancel_ReportsNotPlaying()
    {
        var slot = new PlaybackSlot();
        var a = slot.Begin(TestContext.Current.CancellationToken);
        slot.Start(a);

        slot.Cancel();

        Assert.False(slot.IsPlaying);
        Assert.True(a.IsCancellationRequested);
    }
}
