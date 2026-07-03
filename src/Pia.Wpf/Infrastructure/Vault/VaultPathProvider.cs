using System;
using System.IO;

namespace Pia.Infrastructure.Vault;

/// <summary>
/// Resolves the memory-vault root directory (format spec v1, §1). The root is now runtime-mutable
/// (<see cref="SetRoot"/>) because the vault is derived from the relocatable assistant files folder
/// (<c>&lt;folder&gt;\Vault</c>): a startup coordinator points it at the configured folder, and a
/// folder relocation re-points it live. The parameterless ctor keeps the legacy
/// <c>%LOCALAPPDATA%\Pia\Vault</c> as a pre-coordinator fallback; an explicit-path ctor serves tests.
/// Lives in Infrastructure so it carries no dependency on Pia.Services settings types.
/// </summary>
public sealed class VaultPathProvider
{
    // volatile: VaultStore.Root reads this without a lock; a relocation writes it on another thread.
    private volatile string _vaultRoot;

    public string VaultRoot => _vaultRoot;

    public VaultPathProvider() : this(DefaultRoot())
    {
    }

    public VaultPathProvider(string root)
    {
        _vaultRoot = root;
    }

    /// <summary>
    /// Re-point the vault root at runtime (startup derivation / folder relocation). Readers
    /// (<see cref="VaultStore.Root"/>) observe the new value on their next access via the volatile field.
    /// </summary>
    public void SetRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Vault root must be non-empty.", nameof(root));
        _vaultRoot = root;
    }

    private static string DefaultRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Pia", "Vault");
    }
}
