using Pia.Helpers;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <inheritdoc cref="IObsidianService" />
public class ObsidianService : IObsidianService
{
    /// <inheritdoc />
    public bool IsAvailable => ObsidianLauncher.IsAvailable;

    /// <inheritdoc />
    public VaultRegistrationState GetRegistrationState(string? vaultRoot)
        => ObsidianLauncher.GetRegistrationState(vaultRoot);

    /// <inheritdoc />
    public bool IsObsidianRunning() => ObsidianLauncher.IsObsidianRunning();

    /// <inheritdoc />
    public bool TryRegisterVault(string vaultRoot) => ObsidianLauncher.TryRegisterVault(vaultRoot);

    /// <inheritdoc />
    public void OpenVault(string? vaultRoot) => ObsidianLauncher.OpenVault(vaultRoot);

    /// <inheritdoc />
    public void OpenNote(string? vaultRoot, string? pathUnderRoot)
        => ObsidianLauncher.OpenNote(vaultRoot, pathUnderRoot);
}
