using Pia.Models.Vault;

namespace Pia.Services.Interfaces;

/// <summary>
/// Read-only view over the vault's <c>sources/</c> RAW layer for the Vault view: every
/// staged file joined with its ingest provenance (how many <c>memory/topics/</c> pages record it in
/// their <c>sources:</c> frontmatter). Purely observational — nothing here mutates the vault.
/// </summary>
public interface IVaultSourcesService
{
    /// <summary>All files under <c>sources/</c> (any extension), name-ordered; empty when the folder is absent.</summary>
    Task<IReadOnlyList<VaultSourceItem>> ListSourcesAsync();
}
