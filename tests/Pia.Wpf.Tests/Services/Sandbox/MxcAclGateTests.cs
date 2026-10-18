using Pia.Services.Plugins.Sandbox;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

/// <summary>The gate is process-wide, so every path releases what it took: a leak would stall the container tests.</summary>
public sealed class MxcAclGateTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ASecondHolder_WaitsUntilTheFirstHandleIsDisposed()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await MxcAclGate.EnterAsync(ct).WaitAsync(Patience, ct);
        var second = MxcAclGate.EnterAsync(ct);
        try
        {
            await Task.Delay(100, ct);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            first.Dispose();
            (await second.WaitAsync(Patience, ct)).Dispose();
        }
    }

    [Fact]
    public async Task DisposingAHandleTwice_ReleasesTheGateOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await MxcAclGate.EnterAsync(ct).WaitAsync(Patience, ct);
        first.Dispose();
        first.Dispose();

        var again = await MxcAclGate.EnterAsync(ct).WaitAsync(Patience, ct);
        var third = MxcAclGate.EnterAsync(ct);
        try
        {
            await Task.Delay(100, ct);
            Assert.False(third.IsCompleted);
        }
        finally
        {
            again.Dispose();
            (await third.WaitAsync(Patience, ct)).Dispose();
        }
    }

    [Fact]
    public async Task CancellingWhileWaiting_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cancel = new CancellationTokenSource();
        var first = await MxcAclGate.EnterAsync(ct).WaitAsync(Patience, ct);
        var waiting = MxcAclGate.EnterAsync(cancel.Token);
        try
        {
            Assert.False(waiting.IsCompleted);

            await cancel.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        finally
        {
            await cancel.CancelAsync();
            first.Dispose();
            if (waiting.IsCompletedSuccessfully)
                (await waiting).Dispose();
        }
    }
}
