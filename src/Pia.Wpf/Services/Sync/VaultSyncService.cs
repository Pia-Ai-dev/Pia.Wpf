using Microsoft.Extensions.Logging;
using Pia.Infrastructure.Sync;
using Pia.Infrastructure.Vault;
using Pia.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services.Sync;

/// <summary>
/// Merge-on-pull reconciler for Pia-managed vault files (memory-vault format spec §10). On each pull it
/// runs a section-aware 3-way merge of the incoming remote content against the local copy and the
/// retained last-synced base, writes the resolved file atomically, and advances the base snapshot. The
/// server stays last-writer-wins and zero-knowledge; all merge logic lives here.
/// </summary>
public sealed class VaultSyncService : IVaultSyncService
{
    private readonly SectionMergeEngine _merge;
    private readonly SyncBaseStore _baseStore;
    private readonly IVaultStore _store;
    private readonly MarkdownVaultParser _parser;
    private readonly ILogger<VaultSyncService> _logger;

    public VaultSyncService(
        SectionMergeEngine merge,
        SyncBaseStore baseStore,
        IVaultStore store,
        MarkdownVaultParser parser,
        ILogger<VaultSyncService> logger)
    {
        _merge = merge;
        _baseStore = baseStore;
        _store = store;
        _parser = parser;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Base-advance policy: after reconciling, the base snapshot is advanced to the <b>resolved</b>
    /// content (the bytes now living locally), not to the raw <c>remoteContent</c>. This keeps the
    /// retained base equal to the local file, so on the next pull any not-yet-pushed local edits that
    /// were auto-merged in this round are correctly treated as the common ancestor — they do not
    /// resurface as spurious diffs/conflicts against the next remote. (Standard 3-way state: base ==
    /// last reconciled local copy.) On a first pull (no base) or when there is no local file, the
    /// remote is written verbatim and the base is set to it.
    /// </remarks>
    public async Task<string> ReconcileOnPullAsync(Guid id, string path, string remoteContent)
    {
        var @base = await _baseStore.ReadBaseAsync(id);
        var local = await _store.ReadAsync(path);

        // First sync for this file (no retained base) OR no local copy (nothing to merge against):
        // take the remote verbatim. This is the common fast path and avoids a degenerate merge.
        if (@base is null || local is null)
        {
            _logger.SensitiveDebug(
                "Vault pull {Id}: no {Missing} — writing remote verbatim to {Path}",
                id, @base is null ? "base" : "local copy", path);

            await _store.WriteAtomicAsync(path, remoteContent);
            await _baseStore.WriteBaseAsync(id, remoteContent);
            return remoteContent;
        }

        // 3-way merge: base vs local-on-disk vs incoming remote (spec §10.1 oracle).
        var baseDoc = _parser.Parse(@base);
        var remoteDoc = _parser.Parse(remoteContent);
        var result = _merge.Merge(baseDoc, local, remoteDoc);

        await _store.WriteAtomicAsync(path, result.Text);
        // Advance base to the resolved content (see remarks): base == the local file we just wrote.
        await _baseStore.WriteBaseAsync(id, result.Text);

        if (result.ConflictedSlugs.Count > 0)
        {
            _logger.LogInformation(
                "Vault pull {Id}: merged with {Count} conflicted section(s)", id, result.ConflictedSlugs.Count);
        }
        else
        {
            _logger.SensitiveDebug("Vault pull {Id}: clean merge written to {Path}", id, path);
        }

        return result.Text;
    }
}
