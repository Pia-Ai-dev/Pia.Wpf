using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class MemoryService : IMemoryService
{
    private readonly SqliteContext _context;
    private readonly ILogger<MemoryService> _logger;
    private readonly IEmbeddingService _embeddingService;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public MemoryService(SqliteContext context, ILogger<MemoryService> logger, IEmbeddingService embeddingService)
    {
        _context = context;
        _logger = logger;
        _embeddingService = embeddingService;
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
        // Structured JSON query - search for exact values in JSON data
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Type, Label, Data, Embedding, CreatedAt, UpdatedAt, LastAccessedAt
            FROM Memories
            WHERE Label LIKE @Query OR Data LIKE @Query
            ORDER BY UpdatedAt DESC
            LIMIT 20
            """;
        command.Parameters.AddWithValue("@Query", $"%{query}%");

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

        // Escape FTS5 special characters and create a query with OR between terms
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ftsQuery = string.Join(" OR ", terms.Select(t => $"\"{EscapeFtsQuery(t)}\""));
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
        // In-memory cosine similarity search
        var allObjects = await GetAllObjectsWithEmbeddingsAsync();

        var results = allObjects
            .Where(m => m.Embedding is not null)
            .Select(m => (Memory: m, Score: CosineSimilarity(queryEmbedding, _embeddingService.BytesToFloats(m.Embedding!))))
            .Where(x => x.Score >= threshold)
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => x.Memory)
            .ToList();

        return results.AsReadOnly();
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

        // Tier 3: Vector similarity search
        if (queryEmbedding is not null)
        {
            var vectorResults = await VectorSearchAsync(queryEmbedding, topK);
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

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;

        float dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denominator = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denominator == 0 ? 0f : dot / denominator;
    }

    private static string EscapeFtsQuery(string term)
    {
        return term.Replace("\"", "\"\"");
    }
}
