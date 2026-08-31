namespace Pia.Infrastructure.Vault;

/// <summary>
/// Path predicates over the vault tree. A <em>record file</em> is a user-facing memory document — the
/// source of truth for the assistant's recall and the Vault view — as opposed to Pia's housekeeping
/// documents (<c>AGENTS.md</c>, <c>index.md</c>, <c>log.md</c>,
/// <c>templates.md</c>), the recoverable <c>.archive/</c>
/// snapshots, or the <c>sources/</c> RAW layer (read-only except for a corrective <c>update_source</c>).
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
        "memory/templates.md",
    };

    /// <summary>
    /// True iff <paramref name="relativePath"/> (vault-root-relative; either <c>/</c> or <c>\</c>
    /// separators) is a user-facing memory record: a <c>.md</c> file under <c>memory/</c> that is neither
    /// a housekeeping document nor dot-prefixed at any segment. Files under <c>sources/</c> are excluded
    /// because they are not under <c>memory/</c>.
    ///
    /// <para>Dot segments are denied on the same terms as <see cref="IsRecallIndexable"/>, and for a sharper
    /// reason than tidiness: the migration's populated-vault guard counts record files, so one foreign
    /// <c>memory/.stversions/note.md</c> would make an empty vault look populated and strand the legacy
    /// JSON unmigrated.</para>
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
            && !HasDotSegment(normalized)
            && !Housekeeping.Contains(normalized);
    }

    /// <summary>
    /// True iff <paramref name="relativePath"/> is a <c>.md</c> file whose content should surface in
    /// recall (the <c>Chunks</c>/<c>ChunksFts</c> index): everything EXCEPT Pia's housekeeping documents
    /// (<c>AGENTS.md</c>, <c>index.md</c>, <c>log.md</c>,
    /// <c>templates.md</c>), the <c>sources/</c> RAW layer, and anything with
    /// a dot-prefixed path segment — Pia's own <c>.archive/</c> snapshots, folders other tools drop into the
    /// vault (Obsidian's <c>.obsidian/</c> config and <c>.trash/</c> deleted notes, a stray <c>.git/</c>,
    /// Syncthing's <c>.stversions/</c>), and dot-prefixed files such as <c>memory/.draft.md</c>. Unlike
    /// <see cref="IsRecordFile"/> this is a denylist — not a <c>memory/</c> allowlist — because records may
    /// live at the vault root too.
    ///
    /// <para>The <c>sources/</c> layer is excluded so raw ingest inputs are never embedded directly: only
    /// the LLM-synthesized topic pages under <c>memory/topics/</c> reach recall. This also keeps <c>.md</c>
    /// sources symmetric with non-<c>.md</c> sources — otherwise a <c>sources/foo.md</c> would be both
    /// embedded raw (via the vault watcher) AND synthesized into topic pages, duplicating it in recall.</para>
    ///
    /// <para>Applied centrally in the indexer so the watcher, rebuild, and reconcile all agree on what
    /// recall contains.</para>
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
            && !normalized.StartsWith("sources/", StringComparison.OrdinalIgnoreCase)
            && !HasDotSegment(normalized);
    }

    private static bool HasDotSegment(string normalizedPath)
    {
        foreach (var segment in normalizedPath.Split('/'))
        {
            if (segment.Length > 0 && segment[0] == '.')
            {
                return true;
            }
        }

        return false;
    }
}
