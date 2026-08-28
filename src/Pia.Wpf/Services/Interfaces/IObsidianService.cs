using Pia.Helpers;

namespace Pia.Services.Interfaces;

/// <summary>
/// Abstraction over the installed Obsidian so ViewModels can be tested without a real install, a real
/// process list, or a real registry file on the developer's profile. Implemented over
/// <see cref="Pia.Helpers.ObsidianLauncher"/>; the pure predicates there (IsMarkdownNote) stay static.
/// </summary>
public interface IObsidianService
{
    /// <summary>True when an installed Obsidian could be located.</summary>
    bool IsAvailable { get; }

    /// <summary>Whether Obsidian resolves <paramref name="vaultRoot"/>, could be told to, or cannot be asked.</summary>
    VaultRegistrationState GetRegistrationState(string? vaultRoot);

    /// <summary>True when an Obsidian process is running, so its registry must not be edited.</summary>
    bool IsObsidianRunning();

    /// <summary>Adds <paramref name="vaultRoot"/> to Obsidian's vault list. False when nothing was written.</summary>
    bool TryRegisterVault(string vaultRoot);

    /// <summary>Opens <paramref name="vaultRoot"/> as a vault.</summary>
    void OpenVault(string? vaultRoot);

    /// <summary>Opens one note, addressed vault-relative.</summary>
    void OpenNote(string? vaultRoot, string? pathUnderRoot);
}
