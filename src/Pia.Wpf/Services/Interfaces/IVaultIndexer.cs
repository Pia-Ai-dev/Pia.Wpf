namespace Pia.Services.Interfaces;

/// <summary>
/// Maintains the SQLite vector/FTS index (the <c>Chunks</c> + <c>ChunksFts</c> tables) over the
/// on-disk memory vault. The index is a disposable derivative of the vault files (recall path C3):
/// it can always be dropped and rebuilt from the vault, so embedding is content-hash-incremental to
/// avoid re-embedding sections whose <c>Heading</c>/<c>Body</c> have not changed.
/// </summary>
public interface IVaultIndexer
{
    /// <summary>Drop and rebuild the entire index from every <c>*.md</c> file in the vault.</summary>
    Task RebuildAllAsync();

    /// <summary>Re-index one vault file: embed only changed sections, prune removed ones.</summary>
    Task IndexFileAsync(string relativePath);

    /// <summary>Drop all index rows belonging to one vault file (e.g. after it is deleted).</summary>
    Task RemoveFileAsync(string relativePath);
}
