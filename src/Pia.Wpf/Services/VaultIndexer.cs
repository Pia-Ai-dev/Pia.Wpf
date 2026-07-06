using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Infrastructure.Vault;
using Pia.Logging;
using Pia.Models.Vault;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Content-hash incremental indexer for the memory vault. Each <c>## Heading</c> section becomes one
/// <c>Chunks</c> row keyed by (FilePath, Slug); its <c>ContentHash</c> is the SHA-256 of
/// <c>Heading + "\n" + Body</c>. On re-index, a section whose hash is unchanged is skipped (no
/// re-embed); changed sections are re-embedded and their <c>ChunksFts</c> row is refreshed so the
/// FTS rowid stays aligned with the owning <c>Chunks</c> rowid. The whole index is disposable and is
/// rebuilt from the vault by <see cref="RebuildAllAsync"/> (recall path C3).
/// </summary>
public class VaultIndexer : IVaultIndexer
{
    private readonly SqliteContext _context;
    private readonly IVaultStore _store;
    private readonly MarkdownVaultParser _parser;
    private readonly IEmbeddingService _embeddings;
    private readonly ILogger<VaultIndexer> _logger;

    public VaultIndexer(
        SqliteContext context,
        IVaultStore store,
        MarkdownVaultParser parser,
        IEmbeddingService embeddings,
        ILogger<VaultIndexer> logger)
    {
        _context = context;
        _store = store;
        _parser = parser;
        _embeddings = embeddings;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task RebuildAllAsync()
    {
        // C3: the index is a disposable derivative of the vault — drop it wholesale, then rebuild.
        var connection = _context.GetConnection();
        using (var clear = connection.CreateCommand())
        {
            clear.CommandText = "DELETE FROM Chunks; DELETE FROM ChunksFts;";
            await clear.ExecuteNonQueryAsync();
        }

        var files = await _store.EnumerateAsync("*.md");
        foreach (var relativePath in files)
        {
            await IndexFileAsync(relativePath);
        }

        _logger.LogInformation("Rebuilt vault index over {FileCount} file(s)", files.Count);
    }

    /// <inheritdoc />
    public async Task ReconcileAsync()
    {
        // Additive, content-hash-idempotent startup reconcile: index every vault file WITHOUT the wipe
        // RebuildAllAsync does, so a cold index is repopulated and files created while the app (and its
        // watcher) were closed get picked up. Must run BEFORE the watcher goes live — the SqliteContext
        // hands out a single shared connection that is not safe for concurrent use.
        //
        // Skipped when the embedding model is not on disk: indexing would call GenerateEmbeddingAsync,
        // which auto-downloads the model — we must not kick off that (large) download at startup. Once
        // the model is present (fetched on first real use), a later reconcile populates the index.
        if (!_embeddings.IsModelAvailable)
        {
            _logger.LogInformation("Vault reconcile skipped: embedding model not available yet");
            return;
        }

        var files = await _store.EnumerateAsync("*.md");

        foreach (var relativePath in files)
        {
            try
            {
                await IndexFileAsync(relativePath);
            }
            catch (Exception ex)
            {
                // One unreadable/unembeddable file must not abort the whole pass. It stays in the
                // present set below, so its existing chunks (if any) are preserved, not pruned.
                _logger.LogWarning(ex, "Vault reconcile failed for one file; continuing with the rest");
                _logger.SensitiveDebug("Vault reconcile failed on {Path}", relativePath);
            }
        }

        // Prune chunks for files deleted while the app was closed (the watcher never saw the Deleted
        // event). GUARD: never prune off an EMPTY enumeration — a transiently missing/relocating root or
        // a failed scaffold makes EnumerateAsync return [], and pruning against that would wipe the whole
        // index. Zero files => skip pruning; stale rows for genuinely-deleted files simply linger until
        // the next add or a RebuildAllAsync.
        if (files.Count > 0)
        {
            var present = new HashSet<string>(files.Select(NormalizeRelativePath), StringComparer.Ordinal);
            await PruneMissingFilesAsync(present);
        }
        else
        {
            _logger.LogInformation(
                "Vault reconcile: enumeration returned no files; skipping prune to protect the index");
        }

        _logger.LogInformation("Reconciled vault index over {FileCount} file(s)", files.Count);
    }

    /// <inheritdoc />
    public async Task IndexFileAsync(string relativePath)
    {
        // Canonicalize the Chunks key to forward-slash so rebuild-walk paths (native separators,
        // backslash on Windows from Path.GetRelativePath) and watcher paths (already forward-slash)
        // key the SAME file identically — otherwise Windows would form duplicate/orphan chunk rows.
        relativePath = NormalizeRelativePath(relativePath);

        // Pia's housekeeping documents (AGENTS/index/log) and the recoverable .archive/ snapshots must
        // never surface in recall. Filter centrally here so the watcher, RebuildAllAsync and
        // ReconcileAsync all agree, and drop any chunks an earlier (unfiltered) pass left for the path.
        if (!VaultPaths.IsRecallIndexable(relativePath))
        {
            await RemoveFileAsync(relativePath);
            return;
        }

        var doc = await _store.ReadAsync(relativePath);
        if (doc is null)
        {
            // The file vanished between enumeration and read; treat as a removal.
            await RemoveFileAsync(relativePath);
            return;
        }

        var connection = _context.GetConnection();
        var presentSlugs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var section in doc.Sections)
        {
            presentSlugs.Add(section.Slug);
            var contentHash = ComputeContentHash(section);

            if (await IsContentHashUnchangedAsync(connection, relativePath, section.Slug, contentHash))
            {
                // Content-hash skip: this section's Heading/Body are byte-identical to the indexed
                // copy, so the existing embedding and FTS row are still valid — do not re-embed.
                continue;
            }

            var embedding = await _embeddings.GenerateEmbeddingAsync($"{section.Heading}\n{section.Body}");
            var embeddingBytes = _embeddings.FloatsToBytes(embedding);
            await UpsertChunkAsync(connection, relativePath, section, contentHash, embeddingBytes);
            await RefreshFtsAsync(connection, relativePath, section);
        }

        // Freeform files (note/project/topic — the format ingest and remember("topic", …) write)
        // keep their content in the PREAMBLE, not in ## sections, so without this they produce zero
        // chunks and are invisible to recall. Emit ONE synthetic chunk for a non-empty preamble
        // under the reserved slug; heading = frontmatter title, else the filename.
        if (!string.IsNullOrWhiteSpace(doc.Preamble))
        {
            var heading = doc.Frontmatter.TryGetValue("title", out var title) && !string.IsNullOrWhiteSpace(title)
                ? title
                : Path.GetFileNameWithoutExtension(relativePath);
            var preambleSection = new VaultSection(heading, VaultSlug.PreambleSlug, doc.Preamble.Trim(), 0, 0);

            presentSlugs.Add(VaultSlug.PreambleSlug);
            var preambleHash = ComputeContentHash(preambleSection);
            if (!await IsContentHashUnchangedAsync(connection, relativePath, VaultSlug.PreambleSlug, preambleHash))
            {
                var preambleEmbedding = await _embeddings.GenerateEmbeddingAsync(
                    $"{preambleSection.Heading}\n{preambleSection.Body}");
                await UpsertChunkAsync(connection, relativePath, preambleSection, preambleHash,
                    _embeddings.FloatsToBytes(preambleEmbedding));
                await RefreshFtsAsync(connection, relativePath, preambleSection);
            }
        }

        // Prune sections that no longer exist in the file (renamed/removed headings).
        await PruneMissingSectionsAsync(connection, relativePath, presentSlugs);

        _logger.SensitiveDebug(
            "Indexed vault file {Path} with {SectionCount} section(s)", relativePath, doc.Sections.Count);
    }

    /// <inheritdoc />
    public async Task RemoveFileAsync(string relativePath)
    {
        relativePath = NormalizeRelativePath(relativePath);

        var connection = _context.GetConnection();

        // Drop the FTS rows first (their rowids reference the Chunks rows we are about to delete).
        using (var dropFts = connection.CreateCommand())
        {
            dropFts.CommandText = """
                DELETE FROM ChunksFts
                WHERE rowid IN (SELECT rowid FROM Chunks WHERE FilePath = $p);
                """;
            dropFts.Parameters.AddWithValue("$p", relativePath);
            await dropFts.ExecuteNonQueryAsync();
        }

        using var dropChunks = connection.CreateCommand();
        dropChunks.CommandText = "DELETE FROM Chunks WHERE FilePath = $p;";
        dropChunks.Parameters.AddWithValue("$p", relativePath);
        await dropChunks.ExecuteNonQueryAsync();
    }

    private static async Task<bool> IsContentHashUnchangedAsync(
        SqliteConnection connection, string filePath, string slug, string contentHash)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ContentHash FROM Chunks WHERE FilePath = $p AND Slug = $s;";
        cmd.Parameters.AddWithValue("$p", filePath);
        cmd.Parameters.AddWithValue("$s", slug);
        var existing = await cmd.ExecuteScalarAsync() as string;
        return existing is not null && string.Equals(existing, contentHash, StringComparison.Ordinal);
    }

    private static async Task UpsertChunkAsync(
        SqliteConnection connection,
        string filePath,
        VaultSection section,
        string contentHash,
        byte[] embeddingBytes)
    {
        using var cmd = connection.CreateCommand();
        // (FilePath, Slug) is the PRIMARY KEY, so ON CONFLICT updates the existing chunk in place
        // (keeping its rowid stable, which the FTS row alignment relies on).
        cmd.CommandText = """
            INSERT INTO Chunks (FilePath, Heading, Slug, ContentHash, Embedding, IndexedAt)
            VALUES ($p, $h, $s, $hash, $emb, $at)
            ON CONFLICT (FilePath, Slug) DO UPDATE SET
                Heading = excluded.Heading,
                ContentHash = excluded.ContentHash,
                Embedding = excluded.Embedding,
                IndexedAt = excluded.IndexedAt;
            """;
        cmd.Parameters.AddWithValue("$p", filePath);
        cmd.Parameters.AddWithValue("$h", section.Heading);
        cmd.Parameters.AddWithValue("$s", section.Slug);
        cmd.Parameters.AddWithValue("$hash", contentHash);
        cmd.Parameters.AddWithValue("$emb", embeddingBytes);
        cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task RefreshFtsAsync(
        SqliteConnection connection, string filePath, VaultSection section)
    {
        // The Chunks row exists now (upserted above); read its rowid so the FTS row aligns to it.
        long rowId;
        using (var rowIdCmd = connection.CreateCommand())
        {
            rowIdCmd.CommandText = "SELECT rowid FROM Chunks WHERE FilePath = $p AND Slug = $s;";
            rowIdCmd.Parameters.AddWithValue("$p", filePath);
            rowIdCmd.Parameters.AddWithValue("$s", section.Slug);
            rowId = Convert.ToInt64(await rowIdCmd.ExecuteScalarAsync());
        }

        // contentless_delete=1 lets us delete by rowid; replace any stale row, then re-insert.
        using (var deleteFts = connection.CreateCommand())
        {
            deleteFts.CommandText = "DELETE FROM ChunksFts WHERE rowid = $rid;";
            deleteFts.Parameters.AddWithValue("$rid", rowId);
            await deleteFts.ExecuteNonQueryAsync();
        }

        using var insertFts = connection.CreateCommand();
        insertFts.CommandText = """
            INSERT INTO ChunksFts (rowid, FilePath, Heading, Body)
            VALUES ($rid, $p, $h, $b);
            """;
        insertFts.Parameters.AddWithValue("$rid", rowId);
        insertFts.Parameters.AddWithValue("$p", filePath);
        insertFts.Parameters.AddWithValue("$h", section.Heading);
        insertFts.Parameters.AddWithValue("$b", section.Body);
        await insertFts.ExecuteNonQueryAsync();
    }

    // Remove every chunk whose owning file is no longer on disk. Callers MUST pass a non-empty present
    // set built from a successful enumeration (see the guard in ReconcileAsync) — an empty set here
    // would delete the entire index.
    private async Task PruneMissingFilesAsync(HashSet<string> presentFiles)
    {
        var connection = _context.GetConnection();

        var indexedFiles = new List<string>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT DISTINCT FilePath FROM Chunks;";
            using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                indexedFiles.Add(reader.GetString(0));
            }
        }

        foreach (var filePath in indexedFiles)
        {
            // Chunks keys are canonical forward-slash; presentFiles is normalized the same way.
            if (!presentFiles.Contains(filePath))
            {
                await RemoveFileAsync(filePath);
                _logger.SensitiveDebug("Vault reconcile pruned chunks for deleted file {Path}", filePath);
            }
        }
    }

    private static async Task PruneMissingSectionsAsync(
        SqliteConnection connection, string filePath, HashSet<string> presentSlugs)
    {
        var staleRows = new List<(long RowId, string Slug)>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT rowid, Slug FROM Chunks WHERE FilePath = $p;";
            select.Parameters.AddWithValue("$p", filePath);
            using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var rowId = reader.GetInt64(0);
                var slug = reader.GetString(1);
                if (!presentSlugs.Contains(slug))
                {
                    staleRows.Add((rowId, slug));
                }
            }
        }

        foreach (var (rowId, slug) in staleRows)
        {
            using (var deleteFts = connection.CreateCommand())
            {
                deleteFts.CommandText = "DELETE FROM ChunksFts WHERE rowid = $rid;";
                deleteFts.Parameters.AddWithValue("$rid", rowId);
                await deleteFts.ExecuteNonQueryAsync();
            }

            using var deleteChunk = connection.CreateCommand();
            deleteChunk.CommandText = "DELETE FROM Chunks WHERE FilePath = $p AND Slug = $s;";
            deleteChunk.Parameters.AddWithValue("$p", filePath);
            deleteChunk.Parameters.AddWithValue("$s", slug);
            await deleteChunk.ExecuteNonQueryAsync();
        }
    }

    private static string ComputeContentHash(VaultSection section)
    {
        var payload = Encoding.UTF8.GetBytes($"{section.Heading}\n{section.Body}");
        var hash = SHA256.HashData(payload);
        return Convert.ToHexString(hash);
    }

    // Canonical vault-relative key: forward slashes regardless of caller/OS separator. VaultStore
    // resolves either separator on read, so reads are unaffected; this only fixes the Chunks key.
    private static string NormalizeRelativePath(string relativePath)
        => relativePath.Replace('\\', '/');
}
