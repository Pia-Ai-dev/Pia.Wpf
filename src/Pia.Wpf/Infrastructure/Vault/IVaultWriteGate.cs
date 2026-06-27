using System;
using System.Threading;
using System.Threading.Tasks;

namespace Pia.Infrastructure.Vault;

/// <summary>
/// Quiesces vault writes during a folder relocation. All memory writes funnel through
/// <see cref="VaultStore.WriteAtomicAsync"/>, which holds a write lease; the relocation takes an
/// exclusive lease for the swap window so in-flight writes drain and new writes block until the move
/// completes. Lives in Infrastructure so <see cref="VaultStore"/> can use it without a Services dependency.
/// </summary>
public interface IVaultWriteGate
{
    Task<IDisposable> EnterWriteAsync(CancellationToken ct = default);
    Task<IDisposable> EnterExclusiveAsync(CancellationToken ct = default);
}
