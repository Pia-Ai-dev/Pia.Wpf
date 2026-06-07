using System;
using System.IO;

namespace Pia.Infrastructure.Vault;

/// <summary>
/// Resolves the memory-vault root directory (format spec v1, §1). Mirrors <c>SqliteContext</c>'s path
/// pattern: a parameterless ctor that defaults to <c>%LOCALAPPDATA%\Pia\Vault</c> and an explicit-path
/// ctor for tests. Lives in Infrastructure so it carries no dependency on Pia.Services settings types.
/// </summary>
public sealed class VaultPathProvider
{
    public string VaultRoot { get; }

    public VaultPathProvider() : this(DefaultRoot())
    {
    }

    public VaultPathProvider(string root)
    {
        VaultRoot = root;
    }

    private static string DefaultRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Pia", "Vault");
    }
}
