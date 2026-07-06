namespace Pia.Infrastructure.Vault;

/// <summary>
/// Path predicates over the vault tree. A <em>record file</em> is a user-facing memory document — the
/// source of truth for the assistant's recall and the Memory view — as opposed to Pia's housekeeping
/// documents (<c>AGENTS.md</c>, <c>index.md</c>, <c>log.md</c>), the recoverable <c>.archive/</c>
/// snapshots, or the immutable <c>sources/</c> RAW layer.
///
/// <para><see cref="VaultStore.EnumerateAsync"/> is NOT a real glob — <c>"memory/*.md"</c> walks the
/// whole <c>memory/</c> subtree (<see cref="System.IO.SearchOption.AllDirectories"/>) and returns
/// scaffolding files too. Any caller that lists memories (the migration's populated-vault guard, the
/// view's section list) MUST filter the enumeration through <see cref="IsRecordFile"/>.</para>
/// </summary>
public static class VaultPaths
{
    // Exact, vault-root-relative paths of Pia's housekeeping documents (never user records). Matched by
    // FULL path, not bare basename, so a user note like memory/notes/index.md is NOT excluded.
    private static readonly HashSet<string> Housekeeping = new(StringComparer.OrdinalIgnoreCase)
    {
        "memory/AGENTS.md",
        "memory/index.md",
        "memory/log.md",
    };

    /// <summary>
    /// True iff <paramref name="relativePath"/> (vault-root-relative; either <c>/</c> or <c>\</c>
    /// separators) is a user-facing memory record: a <c>.md</c> file under <c>memory/</c> that is neither
    /// a housekeeping document nor under <c>memory/.archive/</c>. Files under <c>sources/</c> are excluded
    /// because they are not under <c>memory/</c>.
    /// </summary>
    public static bool IsRecordFile(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return false;
        }

        // Normalize to forward slashes so a Windows EnumerateAsync result ("memory\notes\foo.md") and a
        // hand-written vault path ("memory/notes/foo.md") compare identically.
        var normalized = relativePath.Replace('\\', '/');

        return normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            && normalized.StartsWith("memory/", StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith("memory/.archive/", StringComparison.OrdinalIgnoreCase)
            && !Housekeeping.Contains(normalized);
    }

    /// <summary>
    /// True iff <paramref name="relativePath"/> is a <c>.md</c> file whose content should surface in
    /// recall (the <c>Chunks</c>/<c>ChunksFts</c> index): everything EXCEPT Pia's housekeeping documents
    /// (<c>AGENTS.md</c>, <c>index.md</c>, <c>log.md</c>) and the recoverable <c>.archive/</c> snapshots.
    /// Unlike <see cref="IsRecordFile"/> this KEEPS the <c>sources/</c> RAW layer (ingest deliberately
    /// made it recallable) and is a denylist — not a <c>memory/</c> allowlist — because records may live
    /// at the vault root too. Applied centrally in the indexer so the watcher, rebuild, and reconcile
    /// all agree on what recall contains.
    /// </summary>
    public static bool IsRecallIndexable(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return false;
        }

        var normalized = relativePath.Replace('\\', '/');

        return normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            && !Housekeeping.Contains(normalized)
            && !normalized.StartsWith(".archive/", StringComparison.OrdinalIgnoreCase)
            && !normalized.Contains("/.archive/", StringComparison.OrdinalIgnoreCase);
    }
}
