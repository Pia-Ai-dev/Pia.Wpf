using System.IO;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure.Vault;
using Pia.Logging;
using Pia.Models.Vault;
using Pia.Services.Interfaces;

namespace Pia.Services.Wiki;

/// <summary>
/// Enumerates the vault's <c>sources/</c> RAW layer (any extension — sources are not Pia-managed
/// markdown, so this walks the filesystem rather than <see cref="IVaultStore.EnumerateAsync"/>) and
/// joins each file against the provenance that <see cref="IngestService"/> records in topic-page
/// <c>sources:</c> frontmatter, so the Vault view can show which raw documents were compiled into the
/// wiki and which are still waiting. Read-only and best-effort: hand-edited frontmatter degrades a
/// file's status to "not ingested" rather than failing the listing.
/// </summary>
public sealed class VaultSourcesService : IVaultSourcesService
{
    private readonly IVaultStore _store;
    private readonly ILogger<VaultSourcesService> _logger;

    public VaultSourcesService(IVaultStore store, ILogger<VaultSourcesService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<IReadOnlyList<VaultSourceItem>> ListSourcesAsync()
    {
        var sourcesDir = Path.Combine(_store.Root, "sources");
        if (!Directory.Exists(sourcesDir))
        {
            return [];
        }

        var pagesBySource = await CountTopicPagesBySourceAsync();

        var items = new List<VaultSourceItem>();
        foreach (var file in Directory.EnumerateFiles(sourcesDir, "*", SearchOption.AllDirectories))
        {
            try
            {
                var info = new FileInfo(file);
                var relative = "sources/" + Path.GetRelativePath(sourcesDir, file).Replace('\\', '/');
                items.Add(new VaultSourceItem(
                    relative,
                    info.Name,
                    info.Length,
                    info.LastWriteTime,
                    SourcesProvenance.IsTextSource(relative),
                    pagesBySource.GetValueOrDefault(relative)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A locked/vanished file must not take down the whole listing.
                _logger.SensitiveDebug("Skipping unreadable source file {File}", file);
            }
        }

        items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return items;
    }

    // Reverse provenance index: source ref -> number of memory/topics pages whose `sources:`
    // frontmatter records it. OrdinalIgnoreCase because ingest stores the model-provided spelling of
    // the ref, which may differ in casing from the on-disk name on a case-insensitive filesystem.
    private async Task<Dictionary<string, int>> CountTopicPagesBySourceAsync()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in await _store.EnumerateAsync("memory/topics/*.md"))
        {
            var doc = await _store.ReadAsync(path);
            if (doc is null)
            {
                continue;
            }

            foreach (var reference in SourcesProvenance.ReadSourceRefs(doc.RawText))
            {
                counts[reference] = counts.GetValueOrDefault(reference) + 1;
            }
        }

        return counts;
    }
}
