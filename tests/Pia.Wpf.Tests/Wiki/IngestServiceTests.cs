using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Infrastructure.Vault;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Wiki;
using Xunit;

namespace Pia.Tests.Wiki;

/// <summary>
/// Task 7.1 ingest pipeline tests for <see cref="IngestService"/>: a real temp
/// <see cref="SqliteContext"/> + <see cref="VaultStore"/> + real <see cref="MemoryService"/> (over a
/// real <see cref="SectionUpsertService"/> + a deterministic stub embedder) + real
/// <see cref="VaultIndexService"/>/<see cref="VaultLogService"/>, with a STUB
/// <see cref="IIngestExtractor"/> returning two fixed entities so the pipeline is exercised without an
/// API key. The source <c>sources/sample.txt</c> is seeded directly under the vault root.
/// </summary>
public class IngestServiceTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _vaultRoot;
    private readonly SqliteContext _ctx;
    private readonly MarkdownVaultParser _parser = new();
    private readonly VaultStore _store;
    private readonly StubEmbeddingService _embeddings = new();
    private readonly SyncDeleteTrackerService _deleteTracker;
    private readonly SectionUpsertService _upsert;
    private readonly VaultIndexService _index;
    private readonly VaultLogService _log;

    public IngestServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"pia-ingest-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tmpDir);
        _vaultRoot = Path.Combine(_tmpDir, "vault");
        Directory.CreateDirectory(_vaultRoot);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
        _store = new VaultStore(_vaultRoot, _parser);
        _deleteTracker = new SyncDeleteTrackerService(_tmpDir, NullLogger<SyncDeleteTrackerService>.Instance);
        _upsert = new SectionUpsertService(_embeddings);
        _index = new VaultIndexService(_store, NullLogger<VaultIndexService>.Instance);
        _log = new VaultLogService(_store, NullLogger<VaultLogService>.Instance);

        // Seed an immutable source under sources/.
        var sourcesDir = Path.Combine(_vaultRoot, "sources");
        Directory.CreateDirectory(sourcesDir);
        File.WriteAllText(
            Path.Combine(sourcesDir, "sample.txt"),
            "Acme Corp is a customer since 2024. John Smith is the primary contact at Acme.");
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

    private MemoryService BuildMemory()
        => new(_ctx, NullLogger<MemoryService>.Instance, _embeddings, _deleteTracker, _store, _upsert);

    private IngestService BuildIngest(IIngestExtractor extractor)
        => new(extractor, BuildMemory(), _store, _index, _log, _embeddings,
            NullLogger<IngestService>.Instance);

    // Two fixed entities — no API key required.
    private sealed class StubExtractor : IIngestExtractor
    {
        public Task<string> SummarizeAsync(string content, CancellationToken ct = default)
            => Task.FromResult("Notes on Acme Corp and John Smith.");

        public Task<IReadOnlyList<ExtractedEntity>> ExtractEntitiesAsync(string content, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ExtractedEntity>>(
            [
                new ExtractedEntity("Acme Corp", "- type: customer\n- since: 2024"),
                new ExtractedEntity("John Smith", "- role: primary contact\n- company: Acme"),
            ]);
    }

    [Fact]
    public async Task IngestAsync_creates_a_topic_page_per_entity()
    {
        var ingest = BuildIngest(new StubExtractor());

        var result = await ingest.IngestAsync("sources/sample.txt", new DateOnly(2026, 6, 7),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.TouchedPages.Count);

        var acme = await _store.ReadAsync("memory/topics/acme-corp.md");
        var john = await _store.ReadAsync("memory/topics/john-smith.md");
        Assert.NotNull(acme);
        Assert.NotNull(john);

        Assert.Equal("topic", acme!.Frontmatter["type"]);
        Assert.Equal("topic", john!.Frontmatter["type"]);
        Assert.Contains("customer", acme.RawText);
        Assert.Contains("primary contact", john.RawText);
    }

    [Fact]
    public async Task Reingesting_the_same_source_does_not_create_duplicate_pages()
    {
        var ingest = BuildIngest(new StubExtractor());

        await ingest.IngestAsync("sources/sample.txt", new DateOnly(2026, 6, 7),
            TestContext.Current.CancellationToken);
        await ingest.IngestAsync("sources/sample.txt", new DateOnly(2026, 6, 7),
            TestContext.Current.CancellationToken);

        var pages = await _store.EnumerateAsync("memory/topics/*.md");
        Assert.Equal(2, pages.Count);

        // The Acme page still has exactly the one topic record (no duplicated body).
        var acme = await _store.ReadAsync("memory/topics/acme-corp.md");
        Assert.NotNull(acme);
        var sinceCount = acme!.RawText.Split("since: 2024").Length - 1;
        Assert.Equal(1, sinceCount);
    }

    [Fact]
    public async Task Index_has_entries_for_each_touched_topic_page()
    {
        var ingest = BuildIngest(new StubExtractor());

        await ingest.IngestAsync("sources/sample.txt", new DateOnly(2026, 6, 7),
            TestContext.Current.CancellationToken);

        var index = await _store.ReadAsync("memory/index.md");
        Assert.NotNull(index);
        Assert.Contains("[[topics/acme-corp]]", index!.RawText);
        Assert.Contains("[[topics/john-smith]]", index.RawText);
    }

    [Fact]
    public async Task Log_has_an_ingest_line_naming_the_source_and_touched_pages()
    {
        var ingest = BuildIngest(new StubExtractor());

        await ingest.IngestAsync("sources/sample.txt", new DateOnly(2026, 6, 7),
            TestContext.Current.CancellationToken);

        var log = await _store.ReadAsync("memory/log.md");
        Assert.NotNull(log);
        Assert.Contains("] ingest |", log!.RawText);
        Assert.Contains("sample.txt", log.RawText);
        Assert.Contains("topics/acme-corp", log.RawText);
        Assert.Contains("topics/john-smith", log.RawText);
    }

    [Fact]
    public async Task Each_touched_topic_page_records_the_source_in_frontmatter()
    {
        var ingest = BuildIngest(new StubExtractor());

        await ingest.IngestAsync("sources/sample.txt", new DateOnly(2026, 6, 7),
            TestContext.Current.CancellationToken);

        var acme = await _store.ReadAsync("memory/topics/acme-corp.md");
        var john = await _store.ReadAsync("memory/topics/john-smith.md");
        Assert.NotNull(acme);
        Assert.NotNull(john);

        Assert.Contains("sources:", acme!.RawText);
        Assert.Contains("sources/sample.txt", acme.RawText);
        Assert.Contains("sources:", john!.RawText);
        Assert.Contains("sources/sample.txt", john.RawText);
    }

    // Deterministic stub embedder (mirrors MemoryWriteTests): distinct text -> near-orthogonal vectors.
    private sealed class StubEmbeddingService : IEmbeddingService
    {
        private const int Dim = 16;

        public bool IsModelAvailable => true;

        public Task<bool> DownloadModelAsync(IProgress<float>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> EnsureAvailableAsync(IProgress<float>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        {
            var vec = new float[Dim];
            var h = Fnv1a(text);
            for (var i = 0; i < Dim; i++)
            {
                h = (h ^ (uint)(i * 0x9e3779b9)) * 16777619u;
                vec[i] = ((h & 0xffff) / 32767.5f) - 1f;
            }
            return Task.FromResult(vec);
        }

        private static uint Fnv1a(string s)
        {
            uint h = 2166136261u;
            foreach (var c in s)
            {
                h = (h ^ c) * 16777619u;
            }
            return h;
        }

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
}
