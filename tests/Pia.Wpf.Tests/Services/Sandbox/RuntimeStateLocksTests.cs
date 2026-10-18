using System.IO;
using Pia.Services.Plugins.Sandbox.Runtimes;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class RuntimeStateLocksTests
{
    private static string UniqueStateDirectory() =>
        Path.Combine(Path.GetTempPath(), "pia-locks-" + Guid.NewGuid().ToString("N"), "files.runtime");

    [Fact]
    public void TryAcquire_ReturnsNullWhileHeld_AndAHandleAfterRelease()
    {
        var state = UniqueStateDirectory();
        var first = RuntimeStateLocks.TryAcquire(state);
        Assert.NotNull(first);

        Assert.Null(RuntimeStateLocks.TryAcquire(state));

        first.Dispose();
        using var second = RuntimeStateLocks.TryAcquire(state);
        Assert.NotNull(second);
    }

    [Fact]
    public void Keys_IgnoreCaseAndATrailingSeparator()
    {
        var state = UniqueStateDirectory();
        using var held = RuntimeStateLocks.TryAcquire(state);
        Assert.NotNull(held);

        Assert.Null(RuntimeStateLocks.TryAcquire(state.ToUpperInvariant()));
        Assert.Null(RuntimeStateLocks.TryAcquire(state + Path.DirectorySeparatorChar));
        Assert.Null(RuntimeStateLocks.TryAcquire(state.ToLowerInvariant() + "/"));
    }

    [Fact]
    public void DifferentFolders_DoNotShareALock()
    {
        using var a = RuntimeStateLocks.TryAcquire(UniqueStateDirectory());
        using var b = RuntimeStateLocks.TryAcquire(UniqueStateDirectory());

        Assert.NotNull(a);
        Assert.NotNull(b);
    }

    [Fact]
    public void DisposingTwice_ReleasesOnce()
    {
        var state = UniqueStateDirectory();
        var first = RuntimeStateLocks.TryAcquire(state)!;
        first.Dispose();
        using var second = RuntimeStateLocks.TryAcquire(state);
        Assert.NotNull(second);

        first.Dispose();

        Assert.Null(RuntimeStateLocks.TryAcquire(state));
    }

    [Fact]
    public async Task AcquireAsync_WaitsForTheRelease()
    {
        var state = UniqueStateDirectory();
        var held = RuntimeStateLocks.TryAcquire(state)!;

        var pending = RuntimeStateLocks.AcquireAsync(state, TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);

        held.Dispose();
        using var acquired = await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Null(RuntimeStateLocks.TryAcquire(state));
    }

    [Fact]
    public async Task AcquireAsync_HonoursCancellation_WithoutTakingTheLock()
    {
        var state = UniqueStateDirectory();
        var held = RuntimeStateLocks.TryAcquire(state)!;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var pending = RuntimeStateLocks.AcquireAsync(state, cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Null(RuntimeStateLocks.TryAcquire(state));

        held.Dispose();
        using var after = RuntimeStateLocks.TryAcquire(state);
        Assert.NotNull(after);
    }
}
