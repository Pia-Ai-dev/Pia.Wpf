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
    public async Task IndexFileAsync(string relativePath)
    {
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

        // Prune sections that no longer exist in the file (renamed/removed headings).
        await PruneMissingSectionsAsync(connection, relativePath, presentSlugs);

        _logger.SensitiveDebug(
            "Indexed vault file {Path} with {SectionCount} section(s)", relativePath, doc.Sections.Count);
    }

    /// <inheritdoc />
    public async Task RemoveFileAsync(string relativePath)
    {
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
}
