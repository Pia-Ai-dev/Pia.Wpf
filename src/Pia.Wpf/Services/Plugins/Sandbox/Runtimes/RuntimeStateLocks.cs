using System.Collections.Concurrent;

namespace Pia.Services.Plugins.Sandbox.Runtimes;

/// <summary>One lock per runtime state folder, so nothing deletes or rewrites a folder a start is still filling.</summary>
public static class RuntimeStateLocks
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<IDisposable> AcquireAsync(string stateDirectory, CancellationToken cancellationToken)
    {
        var gate = GateFor(stateDirectory);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Handle(gate);
    }

    /// <returns>Null when the folder's lock is held.</returns>
    public static IDisposable? TryAcquire(string stateDirectory)
    {
        var gate = GateFor(stateDirectory);
        return gate.Wait(0) ? new Handle(gate) : null;
    }

    private static SemaphoreSlim GateFor(string stateDirectory) =>
        Gates.GetOrAdd(SandboxLaunchSpecBuilder.Normalize(stateDirectory), _ => new SemaphoreSlim(1, 1));

    private sealed class Handle(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
    }
}
