namespace Pia.Services.Interfaces;

/// <summary>
/// Client-side reconciliation of an incoming vault-file pull against the local copy and the retained
/// last-synced base, via the section-aware 3-way merge (memory-vault format spec §10). The server stays
/// last-writer-wins and zero-knowledge; all merge logic is here.
/// </summary>
public interface IVaultSyncService
{
    /// <summary>
    /// Reconcile a pulled vault file (<paramref name="remoteContent"/> at <paramref name="path"/>,
    /// identified by frontmatter <paramref name="id"/>) with the local copy and retained base, write
    /// the resolved content to the vault, advance the base snapshot, and return the resolved content.
    /// </summary>
    Task<string> ReconcileOnPullAsync(Guid id, string path, string remoteContent);
}
