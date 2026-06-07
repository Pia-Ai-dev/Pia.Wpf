using Pia.Models;

namespace Pia.Services.Interfaces;

public record MemorySummary(Guid Id, string Type, string Label);

public record RecallHit(string FilePath, string Heading, string Snippet, float Score);

/// <summary>
/// Outcome of <see cref="IMemoryService.RememberAsync"/> (vault write path). <see cref="Reference"/>
/// is a <c>path#heading</c> address (e.g. <c>memory/contacts.md#John Smith</c>) for the
/// <see cref="UpsertBand.Edit"/>/<see cref="UpsertBand.Create"/> bands, or a bare path for freeform
/// types. For <see cref="UpsertBand.Ambiguous"/> no write happened: <see cref="Reference"/> is empty
/// and <see cref="Candidates"/> (matching section slugs, score-descending) is non-empty.
/// </summary>
public record RememberOutcome(UpsertBand Band, string Reference, IReadOnlyList<string> Candidates);

public interface IMemoryService
{
    Task<MemoryObject> CreateObjectAsync(string type, string label, string jsonData);
    Task<MemoryObject> ImportObjectAsync(MemoryObject memory);
    Task<MemoryObject?> GetObjectAsync(Guid id);
    Task UpdateObjectAsync(Guid id, string jsonMergePatch);
    Task UpdateObjectDataAsync(Guid id, string label, string jsonData);
    Task AppendToListAsync(Guid id, string jsonEntry);
    Task DeleteObjectAsync(Guid id);
    Task<IReadOnlyList<MemoryObject>> GetObjectsByTypeAsync(string type);
    Task<IReadOnlyList<MemoryObject>> GetAllObjectsAsync();
    Task<IReadOnlyList<MemoryObject>> SearchAsync(string query);
    Task<IReadOnlyList<MemoryObject>> FullTextSearchAsync(string query);
    Task<IReadOnlyList<MemoryObject>> VectorSearchAsync(float[] queryEmbedding, int topK = 5, float threshold = 0.3f);
    Task<IReadOnlyList<MemoryObject>> HybridSearchAsync(string query, float[]? queryEmbedding = null, int topK = 10);
    Task<IReadOnlyList<RecallHit>> RecallAsync(string query, int topK = 10);

    /// <summary>
    /// Write a memory into the on-disk vault (format spec v1). Maps <paramref name="type"/> to its
    /// document/path (§7), resolves the target section via <see cref="ISectionUpsertService"/>, and
    /// either edits the matched section (deterministic bullet merge), creates a new section/file, or
    /// returns <see cref="UpsertBand.Ambiguous"/> without writing. Embeddings are NOT generated here —
    /// the vault watcher/indexer owns reindex on file change.
    /// </summary>
    Task<RememberOutcome> RememberAsync(string type, string subject, string content);

    /// <summary>
    /// Remove a memory from the vault. A <c>path#heading</c> <paramref name="reference"/> splices out
    /// that one section (heading line + body); a bare path deletes the whole file.
    /// </summary>
    Task ForgetAsync(string reference);
    Task UpdateEmbeddingAsync(Guid id, byte[] embedding);
    Task TouchAccessTimeAsync(Guid id);
    Task<int> GetObjectCountAsync();
    Task<long> GetStorageSizeAsync();
    Task<IReadOnlyList<MemoryObject>> GetStaleObjectsAsync(TimeSpan staleThreshold);
    Task<string> ExportAllAsync();
    Task<IReadOnlyList<MemorySummary>> GetMemorySummariesAsync(string? typeFilter = null);
}
