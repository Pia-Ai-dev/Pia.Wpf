using System;
using System.Threading;
using System.Threading.Tasks;

namespace Pia.Infrastructure.Vault;

/// <summary>
/// Single async mutex backing <see cref="IVaultWriteGate"/>. Each vault write enters/exits; the
/// relocation takes the same lock exclusively for the swap window, so in-flight writes drain before
/// the move and new writes block until it completes. Vault writes are infrequent, so serializing them
/// is acceptable.
/// </summary>
public sealed class VaultWriteGate : IVaultWriteGate
{
    private readonly SemaphoreSlim _sem = new(1, 1);

    public Task<IDisposable> EnterWriteAsync(CancellationToken ct = default) => AcquireAsync(ct);
    public Task<IDisposable> EnterExclusiveAsync(CancellationToken ct = default) => AcquireAsync(ct);

    private async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _sem.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(_sem);
    }

    private sealed class Releaser(SemaphoreSlim sem) : IDisposable
    {
        private SemaphoreSlim? _sem = sem;
        public void Dispose() => Interlocked.Exchange(ref _sem, null)?.Release();
    }
}
