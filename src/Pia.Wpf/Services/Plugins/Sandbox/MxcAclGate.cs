namespace Pia.Services.Plugins.Sandbox;

/// <summary>One MXC spawn or disposal at a time, process-wide: both rewrite ACLs below every root.</summary>
internal static class MxcAclGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Handle();
    }

    private sealed class Handle : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                Gate.Release();
        }
    }
}
