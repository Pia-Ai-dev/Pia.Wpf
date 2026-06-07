using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure.Vault;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services.Migration;

/// <summary>
/// One-shot, idempotent migration of the legacy SQLite <c>Memories</c> table into the on-disk memory
/// vault (format spec v1). See <see cref="IVaultMigrationRunner"/>.
///
/// <para>Records are written THROUGH <see cref="IMemoryService.RememberAsync"/> so the deterministic
/// upsert merges clear duplicates on the way in (dedup-on-write). Only confident (&gt;= 0.85) duplicates
/// auto-merge; an <see cref="UpsertBand.Ambiguous"/> result performs NO write, so to keep migration
/// LOSSLESS we retry once with a disambiguated subject to force a Create — a record is never dropped.
/// The model-assisted cluster-merge of ambiguous near-duplicates is intentionally DEFERRED.</para>
///
/// <para>Every original row is first snapshotted under <c>memory/.archive/{id}.json</c> so the legacy
/// payload is fully recoverable, and the legacy <c>Memories</c> table is left INTACT (dropping it and
/// retiring the CRUD API is a deferred follow-up).</para>
/// </summary>
public sealed class VaultMigrationRunner : IVaultMigrationRunner
{
    private readonly IMemoryService _memory;
    private readonly IVaultStore _store;
    private readonly IVaultIndexer _indexer;
    private readonly ISettingsService _settings;
    private readonly MemoryJsonRenderer _renderer;
    private readonly ILogger<VaultMigrationRunner> _logger;

    private static readonly JsonSerializerOptions ArchiveOptions = new() { WriteIndented = true };

    public VaultMigrationRunner(
        IMemoryService memory,
        IVaultStore store,
        IVaultIndexer indexer,
        ISettingsService settings,
        MemoryJsonRenderer renderer,
        ILogger<VaultMigrationRunner> logger)
    {
        _memory = memory;
        _store = store;
        _indexer = indexer;
        _settings = settings;
        _renderer = renderer;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MigrationReport> RunAsync()
    {
        // GUARD 1: this device already migrated.
        var settings = await _settings.GetSettingsAsync();
        if (settings.VaultVersion >= 1)
        {
            _logger.LogInformation("Vault migration skipped: VaultVersion already {Version}", settings.VaultVersion);
            return new MigrationReport(Skipped: true, 0, 0, 0);
        }

        // GUARD 2 (cross-device): a device that pulled an already-migrated vault must not re-migrate.
        var existing = await _store.EnumerateAsync("memory/*.md");
        if (existing.Count > 0)
        {
            _logger.LogInformation(
                "Vault migration skipped: vault already populated with {Count} memory file(s)", existing.Count);
            return new MigrationReport(Skipped: true, 0, 0, 0);
        }

        var rows = await _memory.GetAllObjectsAsync();
        var recordsWritten = 0;
        var archived = 0;

        foreach (var row in rows)
        {
            var mappedType = MapType(row.Type);

            try
            {
                recordsWritten += await MigrateRowAsync(row, mappedType);
            }
            catch (Exception ex)
            {
                // Never abort the whole migration on one bad row; log with a HASHED id (never raw content).
                _logger.LogWarning(ex,
                    "Vault migration skipped irregular row {RowHash} (type {Type})",
                    HashId(row.Id), mappedType);
            }

            // ARCHIVE the original row regardless, so it is fully recoverable.
            try
            {
                await ArchiveRowAsync(row);
                archived++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Vault migration failed to archive row {RowHash}", HashId(row.Id));
            }
        }

        // MARKER: record completion so the migration is idempotent.
        settings.VaultVersion = 1;
        await _settings.SaveSettingsAsync(settings);

        // Re-index so the freshly written vault files are searchable. The legacy table stays intact.
        await _indexer.RebuildAllAsync();

        _logger.LogInformation(
            "Vault migration complete: {Rows} row(s) -> {Records} record(s), {Archived} archived",
            rows.Count, recordsWritten, archived);

        return new MigrationReport(Skipped: false, rows.Count, recordsWritten, archived);
    }

    // Write one legacy row through the vault write path. Returns the number of records written.
    private async Task<int> MigrateRowAsync(MemoryObject row, string mappedType)
    {
        if (mappedType == MemoryObjectTypes.ContactList)
        {
            return await MigrateContactListAsync(row);
        }

        // personal_profile / preference / note / project (and skill/context already mapped to note).
        var subject = string.IsNullOrWhiteSpace(row.Label)
            ? MemoryObjectTypes.GetDisplayName(mappedType)
            : row.Label;
        var content = _renderer.RenderBody(row.Data);
        await RememberLosslessAsync(mappedType, subject, content, row.Id);
        return 1;
    }

    // A contact_list row's Data is a JSON array; each entry becomes its own ## section.
    private async Task<int> MigrateContactListAsync(MemoryObject row)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(row.Data);
        }
        catch (JsonException)
        {
            node = null;
        }

        if (node is not JsonArray array)
        {
            // Not the expected array shape — preserve the whole payload as a single contact record
            // rather than dropping it (lossless).
            var fallbackSubject = string.IsNullOrWhiteSpace(row.Label)
                ? MemoryObjectTypes.GetDisplayName(MemoryObjectTypes.ContactList)
                : row.Label;
            await RememberLosslessAsync(
                MemoryObjectTypes.ContactList, fallbackSubject, _renderer.RenderBody(row.Data), row.Id);
            return 1;
        }

        var written = 0;
        foreach (var entry in array)
        {
            if (entry is null)
            {
                continue;
            }

            var subject = EntrySubject(entry, row.Label);
            var content = _renderer.RenderBody(entry.ToJsonString());
            await RememberLosslessAsync(MemoryObjectTypes.ContactList, subject, content, row.Id);
            written++;
        }

        return written;
    }

    // subject = entry["name"] ?? entry["label"] ?? row.Label (spec mapping for contact entries).
    private static string EntrySubject(JsonNode entry, string rowLabel)
    {
        if (entry is JsonObject obj)
        {
            if (TryGetString(obj, "name", out var name))
            {
                return name;
            }

            if (TryGetString(obj, "label", out var label))
            {
                return label;
            }
        }

        return string.IsNullOrWhiteSpace(rowLabel) ? "Contact" : rowLabel;
    }

    private static bool TryGetString(JsonObject obj, string key, out string value)
    {
        value = string.Empty;
        if (obj.TryGetPropertyValue(key, out var node) &&
            node is JsonValue jv &&
            jv.TryGetValue(out string? s) &&
            !string.IsNullOrWhiteSpace(s))
        {
            value = s;
            return true;
        }

        return false;
    }

    // LOSSLESS GUARANTEE: write through RememberAsync (so confident duplicates merge). If the resolver
    // returns Ambiguous it performed NO write — retry ONCE with a disambiguated subject to FORCE a Create
    // so the record is never dropped. The model-assisted ambiguous cluster-merge is DEFERRED.
    private async Task RememberLosslessAsync(string type, string subject, string content, Guid rowId)
    {
        var outcome = await _memory.RememberAsync(type, subject, content);
        if (outcome.Band == UpsertBand.Ambiguous)
        {
            var disambiguated = $"{subject} ({rowId.ToString("N")[..6]})";
            await _memory.RememberAsync(type, disambiguated, content);
            _logger.LogInformation(
                "Vault migration force-created an ambiguous record under a disambiguated subject");
        }
    }

    // Snapshot the original row so it is fully recoverable after migration.
    private async Task ArchiveRowAsync(MemoryObject row)
    {
        var snapshot = new
        {
            id = row.Id,
            type = row.Type,
            label = row.Label,
            data = row.Data,
            createdAt = row.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            updatedAt = row.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
            lastAccessedAt = row.LastAccessedAt.ToString("O", CultureInfo.InvariantCulture),
        };

        var json = JsonSerializer.Serialize(snapshot, ArchiveOptions);
        await _store.WriteAtomicAsync($"memory/.archive/{row.Id}.json", json);
    }

    // Map a legacy type to its canonical vault type (spec §7 / C6): skill/context -> note; others unchanged.
    private static string MapType(string type) => type switch
    {
        MemoryObjectTypes.Skill or MemoryObjectTypes.Context => MemoryObjectTypes.Note,
        _ => type,
    };

    // Stable, content-free identifier for privacy-safe migration logs (never log raw content/labels).
    private static string HashId(Guid id)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(id.ToString("N")));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }
}
