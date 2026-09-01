using System.IO;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure.Vault;
using Pia.Logging;

namespace Pia.Services.Wiki;

/// <summary>
/// Startup repair for topic pages whose frontmatter cannot be parsed — a corrupt <c>title:</c> value
/// once made a page unreadable, and because ingest reads a page before rewriting it, the write that
/// would have fixed it never ran. Each such page is moved to <c>memory/.archive/</c> (never deleted —
/// it may hold a hand-written preamble) and the ingest state of every source that touched it is
/// dropped, so the reconcile scan re-synthesizes it from the raw sources.
/// </summary>
public sealed class VaultRepairService
{
    private const string ArchiveDir = "memory/.archive";

    private readonly IVaultStore _store;
    private readonly VaultIndexService _index;
    private readonly IngestStateStore _state;
    private readonly ILogger<VaultRepairService> _logger;

    public VaultRepairService(
        IVaultStore store,
        VaultIndexService index,
        IngestStateStore state,
        ILogger<VaultRepairService> logger)
    {
        _store = store;
        _index = index;
        _state = state;
        _logger = logger;
    }

    /// <summary>Returns the number of pages repaired.</summary>
    public async Task<int> RepairUnparseableTopicPagesAsync(CancellationToken ct = default)
    {
        var poisoned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in await _store.EnumerateAsync("memory/topics/*.md"))
        {
            ct.ThrowIfCancellationRequested();
            var relative = path.Replace('\\', '/');
            var doc = await _store.ReadAsync(relative);

            // Frontmatter present in the text but absent after parsing = the block did not parse.
            // A page with no block at all is hand-added, not ingest-managed — leave it alone.
            if (doc is not null
                && doc.Frontmatter.Count == 0
                && doc.RawText.TrimStart().StartsWith("---", StringComparison.Ordinal))
            {
                poisoned[relative] = doc.RawText;
            }
        }

        if (poisoned.Count == 0)
        {
            return 0;
        }

        foreach (var (relative, rawText) in poisoned)
        {
            await _store.WriteAtomicAsync($"{ArchiveDir}/{Path.GetFileName(relative)}", rawText);
            await _store.DeleteAsync(relative);
            await _index.RemoveEntryAsync(relative);
            _logger.SensitiveDebug("Vault repair archived unparseable topic page {Path}", relative);
        }

        foreach (var entry in await _state.ListAsync())
        {
            if (entry.TouchedPages.Any(p => poisoned.ContainsKey(p.Replace('\\', '/'))))
            {
                await _state.DeleteAsync(entry.SourceRef);
            }
        }

        _logger.LogWarning(
            "Vault repair archived {Count} unparseable topic page(s); their sources will re-ingest",
            poisoned.Count);
        return poisoned.Count;
    }
}
