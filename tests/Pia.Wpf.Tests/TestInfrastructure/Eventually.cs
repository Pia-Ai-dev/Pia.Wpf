using Xunit;

namespace Pia.Tests.TestInfrastructure;

// Waits for a condition a background continuation will satisfy. Replaces a hand-rolled 200 x Task.Delay(10) poll,
// whose 2s of wall clock could go entirely to CPU contention during a full-suite run — failing a condition that
// would have been satisfied.
internal static class Eventually
{
    // A hang guard rather than a race budget: with no xunit timeout configured, a condition that never becomes
    // true would otherwise wedge the whole run instead of reporting which one it was.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(5);

    /// <param name="description">Names the condition, so an expiry says what never became true.</param>
    public static async Task TrueAsync(Func<bool> condition, string description, CancellationToken ct)
    {
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(ct);
        guard.CancelAfter(HangGuard);

        while (!condition())
        {
            if (guard.IsCancellationRequested)
            {
                // A cancelled RUN is not a failed condition, so let that surface as cancellation.
                ct.ThrowIfCancellationRequested();
                Assert.Fail($"Waited {HangGuard.TotalSeconds:0}s for {description}, which never became true.");
            }

            try
            {
                await Task.Delay(PollInterval, guard.Token);
            }
            catch (OperationCanceledException)
            {
                // Re-check once more before reporting: the guard can expire on a condition that just flipped.
            }
        }
    }

    // A negative control has nothing to wait FOR, so it needs a window instead of a condition. Unlike the idiom
    // above this one cannot fail spuriously — a window cut short by contention can only let a test pass.
    private static readonly TimeSpan SettleWindow = TimeSpan.FromMilliseconds(500);

    /// <summary>Gives a racing continuation a real chance to satisfy <paramref name="unwanted"/>, returning early
    /// once it does so the caller's assertion reports without burning the rest of the window.</summary>
    public static async Task SettleAsync(Func<bool> unwanted, CancellationToken ct)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(SettleWindow);

        while (!unwanted() && !window.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, window.Token);
            }
            catch (OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
            }
        }
    }
}
