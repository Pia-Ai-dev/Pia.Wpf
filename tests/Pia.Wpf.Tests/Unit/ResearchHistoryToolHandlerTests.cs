using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Wpf.Tests.Unit;

public class ResearchHistoryToolHandlerTests
{
    private static ResearchHistoryToolHandler CreateHandler(
        FakeResearchHistoryService history,
        FakeEmbeddingService embedding)
    {
        return new ResearchHistoryToolHandler(
            history,
            embedding,
            NullLogger<ResearchHistoryToolHandler>.Instance);
    }

    private static FunctionCallContent MakeCall(string toolName, IDictionary<string, object?> args)
        => new("call-1", toolName, args);

    [Fact]
    public async Task SearchResearchHistory_EmbeddingUnavailable_FallsBackToTextOnly()
    {
        var history = new FakeResearchHistoryService
        {
            HybridResult =
            [
                new ResearchHistoryEntry
                {
                    Id = Guid.NewGuid(),
                    Query = "Tesla news",
                    SynthesizedResult = "details about Tesla",
                    Status = "Completed",
                    ProviderId = Guid.NewGuid(),
                    CreatedAt = DateTime.Now,
                    CompletedAt = DateTime.Now
                }
            ]
        };
        var embedding = new FakeEmbeddingService { Available = false };
        var handler = CreateHandler(history, embedding);

        var args = new Dictionary<string, object?> { ["query"] = "Tesla" };
        var (result, pending) = await handler.HandleToolCallAsync(MakeCall("search_research_history", args));

        Assert.Null(pending);
        Assert.NotNull(result);
        Assert.Contains("Tesla news", result!.ToString()!);
        // Embedding generation must NOT happen when EnsureAvailable returns false.
        Assert.Equal(0, embedding.GenerateCalls);
        Assert.Null(history.LastEmbeddingArg);
        Assert.Equal("Tesla", history.LastQueryArg);
    }

    [Fact]
    public async Task SearchResearchHistory_NoMatches_ReturnsNoMatchingMessage()
    {
        var history = new FakeResearchHistoryService { HybridResult = Array.Empty<ResearchHistoryEntry>() };
        var embedding = new FakeEmbeddingService { Available = false };
        var handler = CreateHandler(history, embedding);

        var args = new Dictionary<string, object?> { ["query"] = "nope" };
        var (result, pending) = await handler.HandleToolCallAsync(MakeCall("search_research_history", args));

        Assert.Null(pending);
        Assert.Equal("No matching research entries.", result?.ToString());
    }

    [Fact]
    public async Task GetResearchEntry_ValidId_ReturnsFullEntry()
    {
        var id = Guid.NewGuid();
        var history = new FakeResearchHistoryService();
        history.Entries[id] = new ResearchHistoryEntry
        {
            Id = id,
            Query = "Q",
            SynthesizedResult = "R",
            Status = "Completed",
            ProviderId = Guid.NewGuid(),
            CreatedAt = DateTime.Now,
            CompletedAt = DateTime.Now
        };
        var embedding = new FakeEmbeddingService { Available = false };
        var handler = CreateHandler(history, embedding);

        var args = new Dictionary<string, object?> { ["id"] = id.ToString() };
        var (result, pending) = await handler.HandleToolCallAsync(MakeCall("get_research_entry", args));

        Assert.Null(pending);
        var text = result?.ToString();
        Assert.Contains("Query: Q", text);
        Assert.Contains("Result:\nR", text);
    }

    [Fact]
    public async Task GetResearchEntry_InvalidId_ReturnsError()
    {
        var history = new FakeResearchHistoryService();
        var embedding = new FakeEmbeddingService { Available = false };
        var handler = CreateHandler(history, embedding);

        var args = new Dictionary<string, object?> { ["id"] = "not-a-guid" };
        var (result, pending) = await handler.HandleToolCallAsync(MakeCall("get_research_entry", args));

        Assert.Null(pending);
        Assert.StartsWith("Error: invalid GUID", result?.ToString());
    }

    private class FakeResearchHistoryService : IResearchHistoryService
    {
        public IReadOnlyList<ResearchHistoryEntry> HybridResult { get; set; } = Array.Empty<ResearchHistoryEntry>();
        public Dictionary<Guid, ResearchHistoryEntry> Entries { get; } = new();
        public string? LastQueryArg { get; private set; }
        public float[]? LastEmbeddingArg { get; private set; }
        public int LastTopKArg { get; private set; }

        public event EventHandler? SessionsChanged;

        public Task<IReadOnlyList<ResearchHistoryEntry>> HybridSearchAsync(string query, float[]? queryEmbedding = null, int topK = 10)
        {
            LastQueryArg = query;
            LastEmbeddingArg = queryEmbedding;
            LastTopKArg = topK;
            return Task.FromResult(HybridResult);
        }

        public Task<ResearchHistoryEntry?> GetEntryAsync(Guid id)
        {
            Entries.TryGetValue(id, out var entry);
            return Task.FromResult(entry);
        }

        public Task AddEntryAsync(ResearchHistoryEntry entry) => throw new NotImplementedException();

        public Task<IReadOnlyList<ResearchHistoryEntry>> SearchEntriesAsync(
            string? searchText = null,
            DateTime? fromDate = null,
            DateTime? toDate = null,
            int offset = 0,
            int limit = 50) => throw new NotImplementedException();

        public Task DeleteEntryAsync(Guid id) => throw new NotImplementedException();

        public Task<int> GetEntryCountAsync(string? searchText = null, DateTime? fromDate = null, DateTime? toDate = null) =>
            throw new NotImplementedException();

        public Task UpdateEmbeddingAsync(Guid id, byte[] embedding) => throw new NotImplementedException();

        public Task<IReadOnlyList<ResearchHistoryEntry>> VectorSearchAsync(float[] queryEmbedding, int topK = 10, float threshold = 0.2f) =>
            throw new NotImplementedException();

        // Quiet down "event never used" warning while preserving the interface contract.
        private void RaiseSessionsChanged() => SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private class FakeEmbeddingService : IEmbeddingService
    {
        public bool Available { get; set; }
        public int GenerateCalls { get; private set; }

        public bool IsModelAvailable => Available;

        public Task<bool> EnsureAvailableAsync(IProgress<float>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Available);

        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        {
            GenerateCalls++;
            return Task.FromResult(new float[] { 0.1f, 0.2f, 0.3f });
        }

        public Task<bool> DownloadModelAsync(IProgress<float>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Available);

        public byte[] FloatsToBytes(float[] embedding) => Array.Empty<byte>();
        public float[] BytesToFloats(byte[] bytes) => Array.Empty<float>();
    }
}
