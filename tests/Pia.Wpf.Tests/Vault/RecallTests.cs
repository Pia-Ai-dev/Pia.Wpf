using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Infrastructure.Vault;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Vault;

public class RecallTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _vaultRoot;
    private readonly SqliteContext _ctx;
    private readonly MarkdownVaultParser _parser = new();
    private readonly VaultStore _store;
    private readonly StubEmbeddingService _embeddings = new();
    private readonly SyncDeleteTrackerService _deleteTracker;

    public RecallTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"pia-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tmpDir);
        _vaultRoot = Path.Combine(_tmpDir, "vault");
        Directory.CreateDirectory(_vaultRoot);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
        _store = new VaultStore(_vaultRoot, _parser);
        _deleteTracker = new SyncDeleteTrackerService(_tmpDir, NullLogger<SyncDeleteTrackerService>.Instance);
    }

    public void Dispose()
    {
        _ctx.Dispose();
        try
        {
            if (Directory.Exists(_tmpDir))
            {
                Directory.Delete(_tmpDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of the temp dir.
        }
    }

    // A deterministic embedding service used where we only care about row state (not call counts).
    private sealed class StubEmbeddingService : IEmbeddingService
    {
        private static readonly float[] Fixed = { 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f };

        public bool IsModelAvailable => true;

        public Task<bool> DownloadModelAsync(IProgress<float>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> EnsureAvailableAsync(IProgress<float>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult((float[])Fixed.Clone());

        public byte[] FloatsToBytes(float[] embedding)
        {
            var bytes = new byte[embedding.Length * sizeof(float)];
            Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        public float[] BytesToFloats(byte[] bytes)
        {
            var floats = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
            return floats;
        }
    }

    // A file UNDER /memory/ — the canonical Pia-managed location.
    private const string MemoryFixture =
        "---\n" +
        "pia: managed\n" +
        "id: 6f9c0b3e-7c1a-4f2e-9a8b-000000000001\n" +
        "type: profile\n" +
        "title: Profile\n" +
        "schemaVersion: 1\n" +
        "---\n" +
        "## Coffee Preferences\n" +
        "- likes coffee in the morning\n" +
        "\n" +
        "## Goals\n" +
        "- ship the vault\n";

    // A user-authored file OUTSIDE /memory/ (at the vault root) — proves whole-vault scope.
    private const string RootNotesFixture =
        "---\n" +
        "pia: managed\n" +
        "id: 6f9c0b3e-7c1a-4f2e-9a8b-000000000002\n" +
        "type: note\n" +
        "title: Notes\n" +
        "schemaVersion: 1\n" +
        "---\n" +
        "## Coffee Roasters\n" +
        "- favourite roaster is on Main Street\n";

    private async Task<MemoryService> SeedAndBuildAsync()
    {
        await _store.WriteAtomicAsync("memory/profile.md", MemoryFixture);
        await _store.WriteAtomicAsync("notes.md", RootNotesFixture);

        var indexer = new VaultIndexer(_ctx, _store, _parser, _embeddings, NullLogger<VaultIndexer>.Instance);
        await indexer.RebuildAllAsync();

        return new MemoryService(
            _ctx, NullLogger<MemoryService>.Instance, _embeddings, _deleteTracker, _store,
            new SectionUpsertService(_embeddings));
    }

    [Fact]
    public async Task Recall_returns_hits_from_both_inside_and_outside_memory_folder()
    {
        var service = await SeedAndBuildAsync();

        var hits = await service.RecallAsync("Coffee");

        Assert.NotEmpty(hits);

        // Whole-vault scope: a hit from the /memory/ file AND from the root-level file.
        Assert.Contains(hits, h => h.FilePath.Replace('\\', '/') == "memory/profile.md");
        Assert.Contains(hits, h => h.FilePath.Replace('\\', '/') == "notes.md");
    }

    [Fact]
    public async Task Recall_hits_carry_filepath_heading_snippet_and_score()
    {
        var service = await SeedAndBuildAsync();

        var hits = await service.RecallAsync("Coffee");

        Assert.NotEmpty(hits);
        foreach (var hit in hits)
        {
            Assert.False(string.IsNullOrWhiteSpace(hit.FilePath));
            Assert.False(string.IsNullOrWhiteSpace(hit.Heading));
            Assert.False(string.IsNullOrWhiteSpace(hit.Snippet));
            Assert.True(hit.Score > 0f);
        }
    }

    [Fact]
    public async Task Recall_orders_hits_by_score_descending()
    {
        var service = await SeedAndBuildAsync();

        var hits = await service.RecallAsync("Coffee");

        var scores = hits.Select(h => h.Score).ToList();
        for (int i = 1; i < scores.Count; i++)
        {
            Assert.True(scores[i - 1] >= scores[i]);
        }
    }

    [Fact]
    public async Task Recall_respects_topK()
    {
        var service = await SeedAndBuildAsync();

        var hits = await service.RecallAsync("Coffee", topK: 1);

        Assert.True(hits.Count <= 1);
    }
}
