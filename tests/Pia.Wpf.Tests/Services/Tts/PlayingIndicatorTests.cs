using Pia.Services.Tts;
using Xunit;

namespace Pia.Tests.Services.Tts;

public sealed class PlayingIndicatorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_StaleReadParkedBeforeDelivery_CannotOutliveTheNewerTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var state = true;
        var parkNextRead = 0;
        var delivered = new List<bool>();
        using var aParked = new ManualResetEventSlim();
        using var releaseA = new ManualResetEventSlim();

        var indicator = new PlayingIndicator(
            () =>
            {
                var value = Volatile.Read(ref state);
                if (Interlocked.Exchange(ref parkNextRead, 0) == 1)
                {
                    aParked.Set();
                    Assert.True(releaseA.Wait(Timeout));
                }
                return value;
            },
            playing =>
            {
                lock (delivered)
                    delivered.Add(playing);
            });

        indicator.Notify();

        Volatile.Write(ref state, false);
        Volatile.Write(ref parkNextRead, 1);
        var a = Task.Run(indicator.Notify, ct);
        Assert.True(aParked.Wait(Timeout, ct));

        Volatile.Write(ref state, true);
        var bDone = 0;
        var b = new Thread(() =>
        {
            indicator.Notify();
            Volatile.Write(ref bDone, 1);
        }) { IsBackground = true };
        b.Start();

        // B must be blocked on the indicator (or, if unserialized, already finished) before A resumes.
        var deadline = DateTime.UtcNow + Timeout;
        while (Volatile.Read(ref bDone) == 0 && !b.ThreadState.HasFlag(ThreadState.WaitSleepJoin))
        {
            Assert.True(DateTime.UtcNow < deadline, "B neither blocked nor finished");
            Thread.Sleep(1);
        }

        releaseA.Set();
        await a.WaitAsync(Timeout, ct);
        Assert.True(b.Join(Timeout));

        lock (delivered)
        {
            Assert.True(delivered[^1]);
            Assert.Equal(Volatile.Read(ref state), delivered[^1]);
        }
    }

    [Fact]
    public void Notify_WithAnUnchangedState_DeliversNothing()
    {
        var count = 0;
        var indicator = new PlayingIndicator(() => false, _ => count++);

        indicator.Notify();

        Assert.Equal(0, count);
    }
}
