using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Infrastructure.Vault;
using Pia.Models;
using Pia.Models.Vault;
using Pia.Services.Interfaces;
using Pia.Services.Search;
using Pia.Services.Similarity;

namespace Pia.Services;

public class MemoryService : IMemoryService
{
    private readonly SqliteContext _context;
    private readonly ILogger<MemoryService> _logger;
    private readonly IEmbeddingService _embeddingService;
    private readonly SyncDeleteTrackerService _deleteTracker;
    private readonly IVaultStore _vaultStore;
    private readonly ISectionUpsertService _sectionUpsert;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public MemoryService(SqliteContext context, ILogger<MemoryService> logger, IEmbeddingService embeddingService, SyncDeleteTrackerService deleteTracker, IVaultStore vaultStore, ISectionUpsertService sectionUpsert)
    {
        _context = context;
        _logger = logger;
        _embeddingService = embeddingService;
        _deleteTracker = deleteTracker;
        _vaultStore = vaultStore;
        _sectionUpsert = sectionUpsert;
    }

    public async Task<MemoryObject> CreateObjectAsync(string type, string label, string jsonData)
    {
        var memory = new MemoryObject
        {
            Type = type,
            Label = label,
            Data = jsonData,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow
        };

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Memories (Id, Type, Label, Data, CreatedAt, UpdatedAt, LastAccessedAt)
            VALUES (@Id, @Type, @Label, @Data, @CreatedAt, @UpdatedAt, @LastAccessedAt)
            """;

        command.Parameters.AddWithValue("@Id", memory.Id.ToString());
        command.Parameters.AddWithValue("@Type", memory.Type);
        command.Parameters.AddWithValue("@Label", memory.Label);
        command.Parameters.AddWithValue("@Data", memory.Data);
        command.Parameters.AddWithValue("@CreatedAt", memory.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("@UpdatedAt", memory.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("@LastAccessedAt", memory.LastAccessedAt.ToString("O"));

        await command.ExecuteNonQueryAsync();

        _logger.LogInformation("Created memory object {Id} of type {Type}: {Label}", memory.Id, type, label);
        return memory;
    }

    public async Task<MemoryObject> ImportObjectAsync(MemoryObject memory)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Memories (Id, Type, Label, Data, CreatedAt, UpdatedAt, LastAccessedAt)
            VALUES (@Id, @Type, @Label, @Data, @CreatedAt, @UpdatedAt, @LastAccessedAt)
            """;

        command.Parameters.AddWithValue("@Id", memory.Id.ToString());
        command.Parameters.AddWithValue("@Type", memory.Type);
        command.Parameters.AddWithValue("@Label", memory.Label);
        command.Parameters.AddWithValue("@Data", memory.Data);
        command.Parameters.AddWithValue("@CreatedAt", memory.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("@UpdatedAt", memory.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("@LastAccessedAt", memory.LastAccessedAt.ToString("O"));

        await command.ExecuteNonQueryAsync();

        _logger.LogInformation("Imported memory object {Id} of type {Type}: {Label}", memory.Id, memory.Type, memory.Label);
        return memory;
    }

    public async Task<MemoryObject?> GetObjectAsync(Guid id)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Type, Label, Data, Embedding, CreatedAt, UpdatedAt, LastAccessedAt
            FROM Memories WHERE Id = @Id
            """;
        command.Parameters.AddWithValue("@Id", id.ToString());

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return MapMemoryObject(reader);
        }

        return null;
    }

    public async Task UpdateObjectAsync(Guid id, string jsonMergePatch)
    {
        var existing = await GetObjectAsync(id);
        if (existing is null)
            throw new InvalidOperationException($"Memory object {id} not found");

        var existingNode = JsonNode.Parse(existing.Data) ?? new JsonObject();
        var patchNode = JsonNode.Parse(jsonMergePatch) ?? new JsonObject();

        MergeJson(existingNode.AsObject(), patchNode.AsObject());

        var mergedData = existingNode.ToJsonString(JsonOptions);
        var now = DateTime.UtcNow;

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Memories SET Data = @Data, UpdatedAt = @UpdatedAt
            WHERE Id = @Id
            """;

        command.Parameters.AddWithValue("@Id", id.ToString());
        command.Parameters.AddWithValue("@Data", mergedData);
        command.Parameters.AddWithValue("@UpdatedAt", now.ToString("O"));

        await command.ExecuteNonQueryAsync();

        _logger.LogInformation("Updated memory object {Id}", id);
    }

    public async Task UpdateObjectDataAsync(Guid id, string label, string jsonData)
    {
        var now = DateTime.UtcNow;

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Memories SET Label = @Label, Data = @Data, UpdatedAt = @UpdatedAt
            WHERE Id = @Id
            """;

        command.Parameters.AddWithValue("@Id", id.ToString());
        command.Parameters.AddWithValue("@Label", label);
        command.Parameters.AddWithValue("@Data", jsonData);
        command.Parameters.AddWithValue("@UpdatedAt", now.ToString("O"));

        await command.ExecuteNonQueryAsync();

        _logger.LogInformation("Updated memory object data {Id}: {Label}", id, label);
    }

    public async Task AppendToListAsync(Guid id, string jsonEntry)
    {
        var existing = await GetObjectAsync(id);
        if (existing is null)
            throw new InvalidOperationException($"Memory object {id} not found");

        var existingNode = JsonNode.Parse(existing.Data);
        var entryNode = JsonNode.Parse(jsonEntry);

        if (existingNode is JsonArray array)
        {
            array.Add(entryNode);
        }
        else if (existingNode is JsonObject obj)
        {
            // Look for the first array property and append to it
            var arrayProperty = obj.FirstOrDefault(p => p.Value is JsonArray);
            if (arrayProperty.Value is JsonArray innerArray)
            {
                innerArray.Add(entryNode);
            }
            else
            {
                // Create an "items" array if none exists
                var newArray = new JsonArray { entryNode };
                obj["items"] = newArray;
            }
        }

        var updatedData = existingNode!.ToJsonString(JsonOptions);
        var now = DateTime.UtcNow;

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Memories SET Data = @Data, UpdatedAt = @UpdatedAt
            WHERE Id = @Id
            """;

        command.Parameters.AddWithValue("@Id", id.ToString());
        command.Parameters.AddWithValue("@Data", updatedData);
        command.Parameters.AddWithValue("@UpdatedAt", now.ToString("O"));

        await command.ExecuteNonQueryAsync();

        _logger.LogInformation("Appended entry to memory object {Id}", id);
    }

    public async Task DeleteObjectAsync(Guid id)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Memories WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());

        await command.ExecuteNonQueryAsync();
        _deleteTracker.TrackDeletion("memories", id);

        _logger.LogInformation("Deleted memory object {Id}", id);
    }

    public async Task<IReadOnlyList<MemoryObject>> GetObjectsByTypeAsync(string type)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Type, Label, Data, Embedding, CreatedAt, UpdatedAt, LastAccessedAt
            FROM Memories WHERE Type = @Type
            ORDER BY UpdatedAt DESC
            """;
        command.Parameters.AddWithValue("@Type", type);

        return await ReadMemoryObjects(command);
    }

    public async Task<IReadOnlyList<MemoryObject>> GetAllObjectsAsync()
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Type, Label, Data, Embedding, CreatedAt, UpdatedAt, LastAccessedAt
            FROM Memories ORDER BY UpdatedAt DESC
            """;

        return await ReadMemoryObjects(command);
    }

    public async Task<IReadOnlyList<MemoryObject>> SearchAsync(string query)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();

        var conditions = new List<string>();

        // Full phrase match
        conditions.Add("(Label LIKE @FullQuery OR Data LIKE @FullQuery)");
        command.Parameters.AddWithValue("@FullQuery", $"%{query}%");

        // Per-token matches (skip short words that match too broadly)
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < terms.Length; i++)
        {
            if (terms[i].Length <= 3) continue;
            conditions.Add($"(Label LIKE @Term{i} OR Data LIKE @Term{i})");
            command.Parameters.AddWithValue($"@Term{i}", $"%{terms[i]}%");
        }

        command.CommandText = $"""
            SELECT Id, Type, Label, Data, Embedding, CreatedAt, UpdatedAt, LastAccessedAt
            FROM Memories
            WHERE {string.Join(" OR ", conditions)}
            ORDER BY UpdatedAt DESC
            LIMIT 20
            """;

        return await ReadMemoryObjects(command);
    }

    public async Task<IReadOnlyList<MemoryObject>> FullTextSearchAsync(string query)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();

        // FTS5 search with ranking
        command.CommandText = """
            SELECT m.Id, m.Type, m.Label, m.Data, m.Embedding, m.CreatedAt, m.UpdatedAt, m.LastAccessedAt
            FROM MemoriesFts fts
            JOIN Memories m ON fts.Id = m.Id
            WHERE MemoriesFts MATCH @Query
            ORDER BY rank
            LIMIT 20
            """;

        // Escape FTS5 special characters, use exact + prefix matching
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ftsTerms = new List<string>();
        foreach (var term in terms)
        {
            var escaped = EscapeFtsQuery(term);
            ftsTerms.Add($"\"{escaped}\"");
            if (escaped.Length >= 3)
                ftsTerms.Add($"{escaped}*");
            // Compound word decomposition: generate sub-token prefixes
            foreach (var sub in GenerateSubTokenPrefixes(escaped))
                ftsTerms.Add($"{sub}*");
        }
        var ftsQuery = string.Join(" OR ", ftsTerms);
        command.Parameters.AddWithValue("@Query", ftsQuery);

        try
        {
            return await ReadMemoryObjects(command);
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "FTS search failed for query: {Query}", query);
            // Fall back to LIKE search
            return await SearchAsync(query);
        }
    }

    public async Task<IReadOnlyList<MemoryObject>> VectorSearchAsync(
        float[] queryEmbedding, int topK = 5, float threshold = 0.3f)
    {
        var allObjects = await GetAllObjectsWithEmbeddingsAsync();

        var ranked = VectorSearchHelper.RankByCosine(
            allObjects,
            m => m.Embedding is null ? null : _embeddingService.BytesToFloats(m.Embedding),
            queryEmbedding,
            topK,
            threshold).ToList();

        return ranked.AsReadOnly();
    }

    public async Task<IReadOnlyList<MemoryObject>> HybridSearchAsync(
        string query, float[]? queryEmbedding = null, int topK = 10)
    {
        var resultDict = new Dictionary<Guid, (MemoryObject Memory, float Score)>();

        // Tier 1: Structured LIKE search
        var structuredResults = await SearchAsync(query);
        foreach (var m in structuredResults)
        {
            resultDict[m.Id] = (m, 0.6f); // Base score for structured match
        }

        // Tier 2: FTS5 full-text search
        var ftsResults = await FullTextSearchAsync(query);
        foreach (var m in ftsResults)
        {
            if (resultDict.TryGetValue(m.Id, out var existing))
            {
                resultDict[m.Id] = (m, Math.Max(existing.Score, 0.7f)); // Boost for FTS match
            }
            else
            {
                resultDict[m.Id] = (m, 0.7f);
            }
        }

        // Tier 2.5: Fuzzy label matching (scores all memories, dedup via Math.Max)
        var fuzzyResults = await FuzzyLabelSearchAsync(query);
        foreach (var (memory, score) in fuzzyResults)
        {
            if (resultDict.TryGetValue(memory.Id, out var existing))
            {
                resultDict[memory.Id] = (memory, Math.Max(existing.Score, score));
            }
            else
            {
                resultDict[memory.Id] = (memory, score);
            }
        }

        // Tier 3: Vector similarity search (lower threshold for multilingual support)
        if (queryEmbedding is not null)
        {
            var vectorResults = await VectorSearchAsync(queryEmbedding, topK, threshold: 0.2f);
            foreach (var m in vectorResults)
            {
                var vectorScore = 0.8f; // Base vector score
                if (resultDict.TryGetValue(m.Id, out var existing))
                {
                    resultDict[m.Id] = (m, Math.Max(existing.Score, vectorScore));
                }
                else
                {
                    resultDict[m.Id] = (m, vectorScore);
                }
            }
        }

        // Deduplicate and rank
        var merged = resultDict.Values
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => x.Memory)
            .ToList();

        return merged.AsReadOnly();
    }

    /// <summary>
    /// Whole-vault hybrid recall over the <c>Chunks</c> index (every <c>## Heading</c> section of every
    /// vault file, regardless of folder). Mirrors the tier weights of <see cref="HybridSearchAsync"/>
    /// (LIKE 0.6 / FTS 0.7 / fuzzy / vector 0.8) but scopes to chunks rather than the legacy Memories
    /// table. Hits are merged per (FilePath, Slug) taking the max tier score, ranked descending, and the
    /// snippet is the first ~200 chars of the section body read back from the vault.
    /// </summary>
    public async Task<IReadOnlyList<RecallHit>> RecallAsync(string query, int topK = 10)
    {
        // Keyed by (FilePath, Slug) so the same section matched by multiple tiers keeps its best score.
        var scored = new Dictionary<(string FilePath, string Slug), (string Heading, float Score)>();

        void Merge(string filePath, string heading, string slug, float score)
        {
            var key = (filePath, slug);
            if (scored.TryGetValue(key, out var existing))
            {
                scored[key] = (existing.Heading, Math.Max(existing.Score, score));
            }
            else
            {
                scored[key] = (heading, score);
            }
        }

        var connection = _context.GetConnection();

        // Tier 1: LIKE 0.6 over headings.
        using (var like = connection.CreateCommand())
        {
            like.CommandText =
                "SELECT FilePath, Heading, Slug FROM Chunks WHERE Heading LIKE '%' || @q || '%';";
            like.Parameters.AddWithValue("@q", query);
            using var reader = await like.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                Merge(reader.GetString(0), reader.GetString(1), reader.GetString(2), 0.6f);
            }
        }

        // Tier 2: FTS 0.7 over the contentless ChunksFts (rowid aligned to Chunks.rowid).
        var ftsQuery = BuildFtsQuery(query);
        if (!string.IsNullOrWhiteSpace(ftsQuery))
        {
            try
            {
                using var fts = connection.CreateCommand();
                fts.CommandText = """
                    SELECT c.FilePath, c.Heading, c.Slug
                    FROM Chunks c
                    JOIN (SELECT rowid FROM ChunksFts WHERE ChunksFts MATCH @q) f
                      ON c.rowid = f.rowid;
                    """;
                fts.Parameters.AddWithValue("@q", ftsQuery);
                using var reader = await fts.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    Merge(reader.GetString(0), reader.GetString(1), reader.GetString(2), 0.7f);
                }
            }
            catch (SqliteException ex)
            {
                // A malformed MATCH query must degrade gracefully (the other tiers still run).
                _logger.LogWarning(ex, "Vault FTS recall failed; falling back to other tiers");
            }
        }

        // Tier 2.5: fuzzy Jaro-Winkler over chunk headings (same scoring shape as FuzzyLabelSearchAsync).
        var allChunks = await GetAllChunkHeadingsAsync(connection);
        var queryTokens = query.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 3)
            .ToArray();
        if (queryTokens.Length > 0)
        {
            foreach (var chunk in allChunks)
            {
                var headingTokens = chunk.Heading.ToLowerInvariant()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);

                float bestScore = 0f;
                foreach (var qt in queryTokens)
                {
                    foreach (var ht in headingTokens)
                    {
                        var jwScore = (float)JaroWinkler.Similarity(qt, ht);
                        if (jwScore > bestScore) bestScore = jwScore;

                        if (ht.Contains(qt) || qt.Contains(ht))
                        {
                            if (0.80f > bestScore) bestScore = 0.80f;
                        }
                    }
                }

                if (bestScore >= 0.75f)
                {
                    Merge(chunk.FilePath, chunk.Heading, chunk.Slug, 0.5f + (bestScore - 0.75f) * 0.6f);
                }
            }
        }

        // Tier 3: vector 0.8 — embed the query and cosine-compare against each chunk embedding.
        var queryEmbedding = await _embeddingService.GenerateEmbeddingAsync(query);
        foreach (var chunk in allChunks)
        {
            if (chunk.Embedding is null) continue;
            var chunkVector = _embeddingService.BytesToFloats(chunk.Embedding);
            if (chunkVector.Length != queryEmbedding.Length) continue;
            var similarity = VectorSearchHelper.CosineSimilarity(queryEmbedding, chunkVector);
            if (similarity >= 0.2f)
            {
                Merge(chunk.FilePath, chunk.Heading, chunk.Slug, 0.8f);
            }
        }

        // Rank by score descending; build snippets lazily, skipping any section that has since vanished.
        var ranked = scored
            .Select(kv => (kv.Key.FilePath, kv.Key.Slug, kv.Value.Heading, kv.Value.Score))
            .OrderByDescending(x => x.Score)
            .ToList();

        var hits = new List<RecallHit>();
        foreach (var (filePath, slug, heading, score) in ranked)
        {
            var snippet = await BuildSnippetAsync(filePath, slug);
            if (snippet is null)
            {
                // File or section is gone (index lagging the vault); skip rather than emit an empty hit.
                continue;
            }

            hits.Add(new RecallHit(filePath, heading, snippet, score));
            if (hits.Count >= topK) break;
        }

        return hits.AsReadOnly();
    }

    // ---- Vault write path (format spec v1 §2/§4/§6/§7) ----

    // Structured types map to one shared document; records are ## headings within it.
    private static readonly Dictionary<string, string> StructuredPaths = new(StringComparer.Ordinal)
    {
        ["personal_profile"] = "memory/profile.md",
        ["contact_list"] = "memory/contacts.md",
        ["preference"] = "memory/preferences.md",
    };

    // Freeform/compiled types map to one file each under a per-type directory (slug = filename).
    private static readonly Dictionary<string, string> FreeformDirs = new(StringComparer.Ordinal)
    {
        ["note"] = "memory/notes",
        ["project"] = "memory/projects",
        ["topic"] = "memory/topics",
    };

    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ";

    /// <inheritdoc />
    public async Task<RememberOutcome> RememberAsync(
        string type, string subject, string content, bool createOnAmbiguous = false)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(content);

        if (StructuredPaths.TryGetValue(type, out var structuredPath))
        {
            return await RememberStructuredAsync(type, structuredPath, subject, content, createOnAmbiguous);
        }

        if (FreeformDirs.TryGetValue(type, out var dir))
        {
            // Freeform is never ambiguous (exists -> Edit / not -> Create), so the flag is a no-op here.
            return await RememberFreeformAsync(type, dir, subject, content);
        }

        throw new ArgumentException($"Unknown memory type '{type}' (spec §7).", nameof(type));
    }

    /// <inheritdoc />
    public async Task<RememberOutcome> ResolveRememberAsync(string type, string subject, string content)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(content);

        if (StructuredPaths.TryGetValue(type, out var structuredPath))
        {
            return await ResolveStructuredAsync(structuredPath, subject, content);
        }

        if (FreeformDirs.TryGetValue(type, out var dir))
        {
            // Freeform: one file per item. The file either exists (Edit) or not (Create); no ambiguity.
            var path = $"{dir}/{VaultSlug.Slugify(subject)}.md";
            var existing = await _vaultStore.ReadAsync(path);
            var band = existing is null ? UpsertBand.Create : UpsertBand.Edit;
            return new RememberOutcome(band, path, []);
        }

        throw new ArgumentException($"Unknown memory type '{type}' (spec §7).", nameof(type));
    }

    // Resolution-only structured classification — mirrors RememberStructuredAsync's branching but never
    // writes. The Edit/Create reference matches what RememberAsync would produce so the preview is exact.
    private async Task<RememberOutcome> ResolveStructuredAsync(string path, string subject, string content)
    {
        var doc = await _vaultStore.ReadAsync(path);

        if (doc is null || doc.Sections.Count == 0)
        {
            return new RememberOutcome(UpsertBand.Create, $"{path}#{subject}", []);
        }

        var resolution = await _sectionUpsert.ResolveAsync(doc, subject, content);
        return resolution.Band switch
        {
            UpsertBand.Edit => new RememberOutcome(
                UpsertBand.Edit,
                $"{path}#{doc.Sections.First(s => s.Slug == resolution.MatchedSlug).Heading}",
                []),
            UpsertBand.Ambiguous => new RememberOutcome(UpsertBand.Ambiguous, string.Empty, resolution.Candidates),
            _ => new RememberOutcome(UpsertBand.Create, $"{path}#{subject}", []),
        };
    }

    // Structured: one shared document, records keyed by ## heading. Resolve -> Edit/Ambiguous/Create.
    // When createOnAmbiguous is true, the Ambiguous band is resolved as a Create (a new section is
    // appended) so a write always lands — used by the lossless migration path.
    private async Task<RememberOutcome> RememberStructuredAsync(
        string type, string path, string subject, string content, bool createOnAmbiguous)
    {
        var doc = await _vaultStore.ReadAsync(path);
        var bullets = NormalizeContentToBullets(content);

        // No file or no sections yet -> always a Create (ResolveAsync would say the same, but we avoid
        // the embedding round-trip and the null-doc case in one branch).
        if (doc is null || doc.Sections.Count == 0)
        {
            await CreateStructuredSectionAsync(type, path, doc, subject, bullets);
            _logger.LogInformation("Remember created structured section in vault file (Create band)");
            return new RememberOutcome(UpsertBand.Create, $"{path}#{subject}", []);
        }

        var resolution = await _sectionUpsert.ResolveAsync(doc, subject, content);
        switch (resolution.Band)
        {
            case UpsertBand.Edit:
            {
                var matchedSlug = resolution.MatchedSlug!;
                var section = doc.Sections.First(s => s.Slug == matchedSlug);
                var newBody = _sectionUpsert.MergeBullets(section.Body, bullets);
                await _vaultStore.SpliceSectionAsync(path, matchedSlug, newBody);
                await BumpUpdatedAsync(path);
                _logger.LogInformation("Remember edited existing structured section (Edit band)");
                return new RememberOutcome(UpsertBand.Edit, $"{path}#{section.Heading}", []);
            }

            case UpsertBand.Ambiguous:
                if (createOnAmbiguous)
                {
                    // Deterministic, lossless resolution: append a new section so a write always lands.
                    await CreateStructuredSectionAsync(type, path, doc, subject, bullets);
                    _logger.LogInformation(
                        "Remember resolved an ambiguous match to a new structured section (Create band)");
                    return new RememberOutcome(UpsertBand.Create, $"{path}#{subject}", []);
                }

                // No write: the caller (model/user) disambiguates and re-issues a concrete reference.
                _logger.LogInformation(
                    "Remember was ambiguous across {Count} candidate sections; no write performed",
                    resolution.Candidates.Count);
                return new RememberOutcome(UpsertBand.Ambiguous, string.Empty, resolution.Candidates);

            default: // Create
                await CreateStructuredSectionAsync(type, path, doc, subject, bullets);
                _logger.LogInformation("Remember appended new structured section (Create band)");
                return new RememberOutcome(UpsertBand.Create, $"{path}#{subject}", []);
        }
    }

    // Freeform/compiled: one file per item, slug -> filename. Exists -> Edit (merge into its preamble
    // bullets); else Create a new file.
    private async Task<RememberOutcome> RememberFreeformAsync(
        string type, string dir, string subject, string content)
    {
        var path = $"{dir}/{VaultSlug.Slugify(subject)}.md";
        var bullets = NormalizeContentToBullets(content);
        var existing = await _vaultStore.ReadAsync(path);

        if (existing is null)
        {
            var frontmatter = BuildFrontmatter(type, subject);
            var body = bullets.EndsWith('\n') ? bullets : bullets + "\n";
            await _vaultStore.WriteAtomicAsync(path, frontmatter + body);
            _logger.LogInformation("Remember created freeform vault file (Create band)");
            return new RememberOutcome(UpsertBand.Create, path, []);
        }

        // Freeform body lives in the preamble (the single-record file has no ## headings of its own).
        // The preamble byte-range is [frontmatterEnd, firstSectionStart) — frontmatter and any
        // sections are preserved verbatim around the spliced-in merged bullets.
        var merged = _sectionUpsert.MergeBullets(existing.Preamble, bullets);
        var newFile = SplicePreamble(existing, merged);
        await _vaultStore.WriteAtomicAsync(path, newFile);
        await BumpUpdatedAsync(path);
        _logger.LogInformation("Remember edited freeform vault file (Edit band)");
        return new RememberOutcome(UpsertBand.Edit, path, []);
    }

    // Replace the preamble byte-range. The preamble runs from the end of the frontmatter block to the
    // start of the first section heading line (or EOF when there are no sections), so frontmatter and
    // any sibling sections are preserved verbatim.
    private static string SplicePreamble(VaultDocument doc, string newPreamble)
    {
        int preambleEnd;
        if (doc.Sections.Count > 0)
        {
            // First section's heading line starts just before its BodyStart.
            preambleEnd = HeadingLineStart(doc.RawText, doc.Sections[0].BodyStart);
        }
        else
        {
            preambleEnd = doc.RawText.Length;
        }

        var preambleStart = preambleEnd - doc.Preamble.Length;
        return doc.RawText[..preambleStart] + newPreamble + doc.RawText[preambleEnd..];
    }

    // Given a section's BodyStart (the byte just after the heading line's '\n' terminator), return the
    // index of the first byte of that heading line. BodyStart-1 is the heading line's own terminator;
    // we walk back ONE MORE '\n' (the terminator of the line before the heading) and step past it, or
    // to 0 when the heading is the file's first line.
    private static int HeadingLineStart(string raw, int bodyStart)
    {
        // Index of the heading line's terminating '\n' (BodyStart-1), if the body started after one.
        var headingTerminator = bodyStart - 1;
        if (headingTerminator < 0 || headingTerminator > raw.Length - 1 || raw[headingTerminator] != '\n')
        {
            // No trailing '\n' on the heading line (heading is the file's last line); scan from end.
            headingTerminator = raw.Length;
        }

        var lineBefore = raw.LastIndexOf('\n', Math.Max(headingTerminator - 1, 0));
        return lineBefore < 0 ? 0 : lineBefore + 1;
    }

    // Create the file (with frontmatter) if missing, else append a new "## subject\n<bullets>\n" section
    // to the existing RawText. The existing doc (if any) is passed so we only re-read once.
    private async Task CreateStructuredSectionAsync(
        string type, string path, VaultDocument? doc, string subject, string bullets)
    {
        var body = bullets.EndsWith('\n') ? bullets : bullets + "\n";
        var section = $"## {subject}\n{body}";

        if (doc is null)
        {
            var frontmatter = BuildFrontmatter(type, DisplayTitle(type));
            await _vaultStore.WriteAtomicAsync(path, frontmatter + section);
            return;
        }

        // Append after the existing content; a separating blank line keeps the file readable.
        var raw = doc.RawText;
        var separator = raw.EndsWith('\n') ? "\n" : "\n\n";
        await _vaultStore.WriteAtomicAsync(path, raw + separator + section);
    }

    /// <summary>
    /// Build a fresh frontmatter block (spec §2). <c>id</c> is a NEW lowercase-canonical GUID (§2.1
    /// write rule); <c>created</c>/<c>updated</c> are <see cref="DateTime.UtcNow"/> in the §2.5 format.
    /// </summary>
    private static string BuildFrontmatter(string type, string title)
    {
        var id = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var now = DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        return "---\n" +
               "pia: managed\n" +
               $"id: {id}\n" +
               $"type: {type}\n" +
               $"title: {title}\n" +
               $"created: {now}\n" +
               $"updated: {now}\n" +
               "schemaVersion: 1\n" +
               "---\n";
    }

    private static string DisplayTitle(string type) => type switch
    {
        "personal_profile" => "Profile",
        "contact_list" => "Contacts",
        "preference" => "Preferences",
        _ => type,
    };

    /// <summary>
    /// Rewrite ONLY the <c>updated:</c> frontmatter line to now (§2.5), preserving every other byte —
    /// unknown keys, ordering, body — verbatim. If no <c>updated:</c> line exists the file is left
    /// untouched (a non-conforming or section-less file is not our concern here).
    /// </summary>
    private async Task BumpUpdatedAsync(string path)
    {
        var doc = await _vaultStore.ReadAsync(path);
        if (doc is null)
        {
            return;
        }

        var now = DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var updated = ReplaceFrontmatterLine(doc.RawText, "updated", now);
        if (updated is not null)
        {
            await _vaultStore.WriteAtomicAsync(path, updated);
        }
    }

    // Replace the value of a single frontmatter scalar line "key: value" within the leading "---" block,
    // preserving the line's terminator and all surrounding bytes. Returns null if the key is absent.
    private static string? ReplaceFrontmatterLine(string raw, string key, string newValue)
    {
        // Only operate inside the leading frontmatter block (first line is "---").
        if (!raw.StartsWith("---\n", StringComparison.Ordinal) &&
            !raw.StartsWith("---\r\n", StringComparison.Ordinal))
        {
            return null;
        }

        var search = $"\n{key}:";
        var keyIdx = raw.IndexOf(search, StringComparison.Ordinal);
        if (keyIdx < 0)
        {
            return null;
        }

        var valueStart = keyIdx + search.Length;
        var lineEnd = raw.IndexOf('\n', valueStart);
        if (lineEnd < 0)
        {
            lineEnd = raw.Length;
        }

        // Preserve a trailing '\r' (CRLF files) on the rewritten line.
        var hasCr = lineEnd > valueStart && raw[lineEnd - 1] == '\r';
        var newLineContent = $"{search} {newValue}" + (hasCr ? "\r" : string.Empty);
        return raw[..keyIdx] + newLineContent + raw[lineEnd..];
    }

    /// <summary>
    /// Normalize free content into the bullet body format (spec §4). If <paramref name="content"/>
    /// already consists of <c>- key: value</c> bullet lines, it is used as-is. Otherwise the whole
    /// content is treated as free prose and returned unchanged (it lands below any existing bullets via
    /// <see cref="ISectionUpsertService.MergeBullets"/>). NOTE: a model-assisted prose REWRITE — turning
    /// arbitrary prose into structured bullets — is intentionally DEFERRED; this method does no such
    /// rewrite today.
    /// </summary>
    private static string NormalizeContentToBullets(string content) => content;

    /// <inheritdoc />
    public async Task ForgetAsync(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var (path, slug) = VaultReference.Parse(reference);
        if (slug is null)
        {
            // Bare path -> delete the whole file. The watcher/indexer drops its chunks on the change.
            await _vaultStore.DeleteAsync(path);
            _logger.LogInformation("Forget deleted a whole vault file");
            return;
        }

        var doc = await _vaultStore.ReadAsync(path);
        if (doc is null)
        {
            _logger.LogInformation("Forget target file does not exist; nothing to remove");
            return;
        }

        VaultSection? target = null;
        foreach (var section in doc.Sections)
        {
            if (section.Slug == slug)
            {
                target = section;
                break;
            }
        }

        if (target is null)
        {
            _logger.LogInformation("Forget target section was not found; nothing to remove");
            return;
        }

        // Splice out the heading LINE + body, not just the body, so the whole record disappears.
        var headingLineStart = HeadingLineStart(doc.RawText, target.BodyStart);
        var newFile = doc.RawText[..headingLineStart] + doc.RawText[target.BodyEnd..];
        await _vaultStore.WriteAtomicAsync(path, newFile);
        _logger.LogInformation("Forget removed a single vault section");
    }

    private static async Task<IReadOnlyList<(string FilePath, string Heading, string Slug, byte[]? Embedding)>>
        GetAllChunkHeadingsAsync(SqliteConnection connection)
    {
        var chunks = new List<(string, string, string, byte[]?)>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT FilePath, Heading, Slug, Embedding FROM Chunks;";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var embedding = reader.IsDBNull(3) ? null : (byte[])reader[3];
            chunks.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), embedding));
        }

        return chunks;
    }

    private async Task<string?> BuildSnippetAsync(string filePath, string slug)
    {
        var doc = await _vaultStore.ReadAsync(filePath);
        if (doc is null) return null;

        VaultSection? section = null;
        foreach (var candidate in doc.Sections)
        {
            if (candidate.Slug == slug)
            {
                section = candidate;
                break;
            }
        }

        if (section is null) return null;

        var body = section.Body.Trim();
        if (body.Length == 0) return null;

        return body.Length > 200 ? body[..200] : body;
    }

    /// <summary>
    /// Build a sanitized FTS5 MATCH query: each token is double-quoted (so special characters are
    /// treated as literal terms) and OR-joined, mirroring <see cref="FullTextSearchAsync"/>.
    /// </summary>
    private static string BuildFtsQuery(string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ftsTerms = new List<string>();
        foreach (var term in terms)
        {
            var escaped = EscapeFtsQuery(term);
            if (escaped.Length == 0) continue;
            ftsTerms.Add($"\"{escaped}\"");
            if (escaped.Length >= 3)
                ftsTerms.Add($"{escaped}*");
        }

        return string.Join(" OR ", ftsTerms);
    }

    public async Task UpdateEmbeddingAsync(Guid id, byte[] embedding)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Memories SET Embedding = @Embedding WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());
        command.Parameters.AddWithValue("@Embedding", embedding);

        await command.ExecuteNonQueryAsync();
    }

    public async Task TouchAccessTimeAsync(Guid id)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Memories SET LastAccessedAt = @Now WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());
        command.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync();
    }

    public async Task<int> GetObjectCountAsync()
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Memories";
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public async Task<long> GetStorageSizeAsync()
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        // Approximate storage size by summing data lengths
        command.CommandText = """
            SELECT COALESCE(SUM(LENGTH(Data) + LENGTH(Label) + LENGTH(Type) +
                   COALESCE(LENGTH(Embedding), 0)), 0) FROM Memories
            """;
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }

    public async Task<IReadOnlyList<MemoryObject>> GetStaleObjectsAsync(TimeSpan staleThreshold)
    {
        var cutoff = DateTime.UtcNow - staleThreshold;

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Type, Label, Data, Embedding, CreatedAt, UpdatedAt, LastAccessedAt
            FROM Memories
            WHERE LastAccessedAt < @Cutoff
            ORDER BY LastAccessedAt ASC
            """;
        command.Parameters.AddWithValue("@Cutoff", cutoff.ToString("O"));

        return await ReadMemoryObjects(command);
    }

    public async Task<string> ExportAllAsync()
    {
        var allObjects = await GetAllObjectsAsync();
        var exportData = allObjects.Select(m => new
        {
            m.Id,
            m.Type,
            m.Label,
            Data = JsonNode.Parse(m.Data),
            m.CreatedAt,
            m.UpdatedAt,
            m.LastAccessedAt
        });

        return JsonSerializer.Serialize(exportData, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<IReadOnlyList<MemorySummary>> GetMemorySummariesAsync(string? typeFilter = null)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();

        if (typeFilter is not null)
        {
            command.CommandText = """
                SELECT Id, Type, Label FROM Memories
                WHERE Type = @Type
                ORDER BY Type, UpdatedAt DESC
                """;
            command.Parameters.AddWithValue("@Type", typeFilter);
        }
        else
        {
            command.CommandText = """
                SELECT Id, Type, Label FROM Memories
                ORDER BY Type, UpdatedAt DESC
                """;
        }

        var summaries = new List<MemorySummary>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            summaries.Add(new MemorySummary(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2)));
        }

        return summaries.AsReadOnly();
    }

    private async Task<IReadOnlyList<MemoryObject>> GetAllObjectsWithEmbeddingsAsync()
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Type, Label, Data, Embedding, CreatedAt, UpdatedAt, LastAccessedAt
            FROM Memories WHERE Embedding IS NOT NULL
            """;

        return await ReadMemoryObjects(command);
    }

    private static async Task<IReadOnlyList<MemoryObject>> ReadMemoryObjects(SqliteCommand command)
    {
        var objects = new List<MemoryObject>();
        using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            objects.Add(MapMemoryObject(reader));
        }

        return objects.AsReadOnly();
    }

    private static MemoryObject MapMemoryObject(SqliteDataReader reader)
    {
        return new MemoryObject
        {
            Id = Guid.Parse(reader.GetString(0)),
            Type = reader.GetString(1),
            Label = reader.GetString(2),
            Data = reader.GetString(3),
            Embedding = reader.IsDBNull(4) ? null : (byte[])reader[4],
            CreatedAt = DateTime.Parse(reader.GetString(5)),
            UpdatedAt = DateTime.Parse(reader.GetString(6)),
            LastAccessedAt = DateTime.Parse(reader.GetString(7))
        };
    }

    private static void MergeJson(JsonObject target, JsonObject patch)
    {
        foreach (var property in patch)
        {
            if (property.Value is null)
            {
                target.Remove(property.Key);
            }
            else if (property.Value is JsonObject patchObj &&
                     target[property.Key] is JsonObject targetObj)
            {
                MergeJson(targetObj, patchObj);
            }
            else
            {
                target[property.Key] = property.Value.DeepClone();
            }
        }
    }

    private static string EscapeFtsQuery(string term)
    {
        return term.Replace("\"", "\"\"");
    }

    private static IEnumerable<string> GenerateSubTokenPrefixes(string term, int minLen = 4)
    {
        if (term.Length < 8) yield break;
        for (int i = minLen; i <= term.Length - 3; i++)
        {
            yield return term[..i];
        }
    }

    private async Task<IReadOnlyList<(MemoryObject Memory, float Score)>> FuzzyLabelSearchAsync(
        string query)
    {
        var queryTokens = query.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 3) // Skip short stopwords
            .ToArray();

        if (queryTokens.Length == 0) return [];

        var allMemories = await GetAllObjectsAsync();
        var results = new List<(MemoryObject Memory, float Score)>();

        foreach (var memory in allMemories)
        {
            // Tokenize label + first portion of data for cross-language matching
            var labelTokens = memory.Label.ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var dataPreview = memory.Data.Length > 200 ? memory.Data[..200] : memory.Data;
            var dataTokens = dataPreview.ToLowerInvariant()
                .Split([' ', '"', ':', '{', '}', ',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length > 3)
                .Distinct()
                .ToArray();
            var allTokens = labelTokens.Concat(dataTokens).ToArray();

            float bestScore = 0f;
            foreach (var qt in queryTokens)
            {
                foreach (var lt in allTokens)
                {
                    // Jaro-Winkler similarity
                    var jwScore = (float)JaroWinkler.Similarity(qt, lt);
                    if (jwScore > bestScore) bestScore = jwScore;

                    // Substring containment (handles compound words)
                    if (lt.Contains(qt) || qt.Contains(lt))
                    {
                        var subScore = 0.80f;
                        if (subScore > bestScore) bestScore = subScore;
                    }
                }

                // Compound word prefix matching: check if sub-prefixes of query token
                // match the start of any label token (e.g., "schlaf" from "schlafanalyse"
                // matches start of "schlaftracking")
                foreach (var prefix in GenerateSubTokenPrefixes(qt))
                {
                    foreach (var lt in allTokens)
                    {
                        if (lt.StartsWith(prefix))
                        {
                            var prefixScore = 0.80f;
                            if (prefixScore > bestScore) bestScore = prefixScore;
                        }
                    }
                }
            }

            if (bestScore >= 0.75f)
                results.Add((memory, 0.5f + (bestScore - 0.75f) * 0.6f));
        }

        return results
            .OrderByDescending(x => x.Score)
            .Take(10)
            .ToList();
    }
}
