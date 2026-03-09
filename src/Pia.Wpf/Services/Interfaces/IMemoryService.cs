using Pia.Models;

namespace Pia.Services.Interfaces;

public record MemorySummary(Guid Id, string Type, string Label);

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
    Task UpdateEmbeddingAsync(Guid id, byte[] embedding);
    Task TouchAccessTimeAsync(Guid id);
    Task<int> GetObjectCountAsync();
    Task<long> GetStorageSizeAsync();
    Task<IReadOnlyList<MemoryObject>> GetStaleObjectsAsync(TimeSpan staleThreshold);
    Task<string> ExportAllAsync();
    Task<IReadOnlyList<MemorySummary>> GetMemorySummariesAsync(string? typeFilter = null);
}
