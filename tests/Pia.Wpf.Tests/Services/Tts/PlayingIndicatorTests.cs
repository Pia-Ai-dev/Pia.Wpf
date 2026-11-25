using Pia.Services.Tts;
using Xunit;

namespace Pia.Tests.Services.Tts;

public sealed class PlayingIndicatorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_StaleFalseBlockedInDelivery_IsFollowedByTheNewerTrue()
    {
        var state = true;
        var observed = new List<bool>();
        using var deliveringFalse = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var indicator = new PlayingIndicator(
            () => Volatile.Read(ref state),
            playing =>
            {
                lock (observed)
                    observed.Add(playing);
                if (!playing)
                {
                    deliveringFalse.Set();
                    Assert.True(release.Wait(Timeout));
                }
            });

        indicator.Notify();

        Volatile.Write(ref state, false);
        var a = Task.Run(indicator.Notify, TestContext.Current.CancellationToken);
        Assert.True(deliveringFalse.Wait(Timeout, TestContext.Current.CancellationToken));

        Volatile.Write(ref state, true);
        var b = Task.Run(indicator.Notify, TestContext.Current.CancellationToken);

        release.Set();
        await Task.WhenAll(a, b).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        lock (observed)
        {
            Assert.Equal([true, false, true], observed);
            Assert.Equal(Volatile.Read(ref state), observed[^1]);
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
