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
        using var bEntered = new ManualResetEventSlim();

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
        var b = Task.Run(() =>
        {
            bEntered.Set();
            indicator.Notify();
        }, ct);
        Assert.True(bEntered.Wait(Timeout, ct));
        await Task.Delay(100, ct);

        releaseA.Set();
        await Task.WhenAll(a, b).WaitAsync(Timeout, ct);

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
