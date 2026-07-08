using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure.Vault;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Wiki;
using Xunit;

namespace Pia.Tests.Wiki;

/// <summary>
/// Task 7.1 ingest pipeline tests for <see cref="IngestService"/>: a real temp
/// <see cref="VaultStore"/> + real <see cref="VaultIndexService"/>/<see cref="VaultLogService"/> (over
/// a deterministic stub embedder), with a STUB <see cref="IIngestExtractor"/> returning two fixed
/// entities so the pipeline is exercised without an API key. The source <c>sources/sample.txt</c> is
/// seeded directly under the vault root.
/// </summary>
public class IngestServiceTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _vaultRoot;
    private readonly MarkdownVaultParser _parser = new();
    private readonly VaultStore _store;
    private readonly StubEmbeddingService _embeddings = new();
    private readonly VaultIndexService _index;
    private readonly VaultLogService _log;

    public IngestServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"pia-ingest-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tmpDir);
        _vaultRoot = Path.Combine(_tmpDir, "vault");
        Directory.CreateDirectory(_vaultRoot);
        _store = new VaultStore(_vaultRoot, _parser);
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

    private IngestService BuildIngest(IIngestExtractor extractor)
        => new(extractor, _store, _index, _log, _embeddings, NullLogger<IngestService>.Instance);

    private IngestToolHandler BuildToolHandler()
        => new(new PassthroughScheduler(BuildIngest(new StubExtractor())),
            NullLogger<IngestToolHandler>.Instance);

    /// <summary>The tool handler routes through the scheduler; here it just forwards inline.</summary>
    private sealed class PassthroughScheduler(IIngestService inner) : IIngestScheduler
    {
        public event EventHandler? IngestCompleted { add { } remove { } }

        public Task<IngestResult> RunAsync(string sourceRef, CancellationToken ct = default)
            => inner.IngestAsync(sourceRef, DateOnly.FromDateTime(DateTime.UtcNow), ct);

        public Task RemoveAsync(string sourceRef, CancellationToken ct = default)
            => Task.CompletedTask;
    }

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

    // Configurable variant for re-ingest scenarios (v2 of a source with changed facts).
    private sealed class FixedExtractor(params ExtractedEntity[] entities) : IIngestExtractor
    {
        public Task<string> SummarizeAsync(string content, CancellationToken ct = default)
            => Task.FromResult("Fixed summary.");

        public Task<IReadOnlyList<ExtractedEntity>> ExtractEntitiesAsync(string content, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ExtractedEntity>>(entities);
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

    [Fact]
    public async Task IngestAsync_writes_facts_into_a_source_section()
    {
        var ingest = BuildIngest(new StubExtractor());
        await ingest.IngestAsync("sources/sample.txt", new DateOnly(2026, 7, 8),
            TestContext.Current.CancellationToken);

        var acme = await _store.ReadAsync("memory/topics/acme-corp.md");
        Assert.NotNull(acme);
        var section = Assert.Single(acme!.Sections);
        Assert.Equal("Source: sources/sample.txt", section.Heading);
        Assert.Contains("customer", section.Body);
    }

    [Fact]
    public async Task Reingest_replaces_the_source_section_not_appends()
    {
        var ingest = BuildIngest(new StubExtractor());
        await ingest.IngestAsync("sources/sample.txt", new DateOnly(2026, 7, 8),
            TestContext.Current.CancellationToken);

        // v2 of the source: same entity, different fact.
        var ingest2 = BuildIngest(new FixedExtractor(
            new ExtractedEntity("Acme Corp", "- type: former customer")));
        await ingest2.IngestAsync("sources/sample.txt", new DateOnly(2026, 7, 9),
            TestContext.Current.CancellationToken);

        var acme = await _store.ReadAsync("memory/topics/acme-corp.md");
        var section = Assert.Single(acme!.Sections.Where(s => s.Heading == "Source: sources/sample.txt"));
        Assert.Contains("former customer", section.Body);
        Assert.DoesNotContain("since: 2024", acme.RawText); // old fact replaced, not kept
    }

    [Fact]
    public async Task Reingest_preserves_manual_preamble_and_foreign_sections()
    {
        var ingest = BuildIngest(new StubExtractor());
        await ingest.IngestAsync("sources/sample.txt", new DateOnly(2026, 7, 8),
            TestContext.Current.CancellationToken);

        // Simulate a manual remember (preamble) and another source's section. The created page reads
        // "---\n\n## Source: ..." (VaultFrontmatter.Build ends with "---\n", then the "\n" separator) —
        // the pattern below must match that exact shape or the Replace is a silent no-op.
        var acme = await _store.ReadAsync("memory/topics/acme-corp.md");
        var withExtras = acme!.RawText.Replace("---\n\n## ", "---\n- manually remembered fact\n\n## ")
            + "\n## Source: sources/other.txt\n\n- from another source\n";
        Assert.Contains("manually remembered fact", withExtras); // guard: the pattern matched
        await _store.WriteAtomicAsync("memory/topics/acme-corp.md", withExtras);

        await ingest.IngestAsync("sources/sample.txt", new DateOnly(2026, 7, 9),
            TestContext.Current.CancellationToken);

        var after = await _store.ReadAsync("memory/topics/acme-corp.md");
        Assert.Contains("manually remembered fact", after!.RawText);
        Assert.Contains("from another source", after.RawText);
        Assert.Equal(2, after.Sections.Count(s => s.Heading.StartsWith("Source: ")));
    }

    [Fact]
    public async Task RemoveContributionsAsync_removes_section_and_deletes_empty_pages()
    {
        var ingest = BuildIngest(new StubExtractor());
        var result = await ingest.IngestAsync("sources/sample.txt", new DateOnly(2026, 7, 8),
            TestContext.Current.CancellationToken);

        await ingest.RemoveContributionsAsync("sources/sample.txt", result.TouchedPages,
            TestContext.Current.CancellationToken);

        // Pages had ONLY this source's section -> deleted.
        Assert.Null(await _store.ReadAsync("memory/topics/acme-corp.md"));
        Assert.Null(await _store.ReadAsync("memory/topics/john-smith.md"));
    }

    [Fact]
    public async Task RemoveContributionsAsync_keeps_pages_with_other_content()
    {
        var ingest = BuildIngest(new StubExtractor());
        var result = await ingest.IngestAsync("sources/sample.txt", new DateOnly(2026, 7, 8),
            TestContext.Current.CancellationToken);

        var acme = await _store.ReadAsync("memory/topics/acme-corp.md");
        await _store.WriteAtomicAsync("memory/topics/acme-corp.md",
            acme!.RawText + "\n## Source: sources/other.txt\n\n- other fact\n");

        await ingest.RemoveContributionsAsync("sources/sample.txt", result.TouchedPages,
            TestContext.Current.CancellationToken);

        var after = await _store.ReadAsync("memory/topics/acme-corp.md");
        Assert.NotNull(after); // survives — another source still contributes
        Assert.DoesNotContain("Source: sources/sample.txt", after!.RawText);
        Assert.Contains("other fact", after.RawText);
        // Frontmatter ref pruned:
        Assert.DoesNotContain("sources/sample.txt",
            string.Join(",", SourcesProvenance.ReadSourceRefs(after.RawText)));
    }

    [Fact]
    public async Task IngestAsync_refuses_a_traversal_path_that_escapes_the_vault()
    {
        // A '..' target that genuinely resolves OUTSIDE the vault root (<tmpDir>/vault -> <tmpDir>).
        File.WriteAllText(Path.Combine(_tmpDir, "outside.txt"), "Secret data about Acme Corp.");
        var ingest = BuildIngest(new StubExtractor());

        var result = await ingest.IngestAsync("../outside.txt", new DateOnly(2026, 6, 7),
            TestContext.Current.CancellationToken);

        Assert.Equal(IngestOutcome.SourceNotFound, result.Outcome);
        Assert.Empty(result.TouchedPages);
        // The guard must stop BEFORE extraction — no topic page is written from the outside file.
        Assert.Null(await _store.ReadAsync("memory/topics/acme-corp.md"));
    }

    [Fact]
    public async Task IngestAsync_refuses_an_absolute_path_outside_the_vault()
    {
        var outside = Path.Combine(_tmpDir, "outside-abs.txt");
        File.WriteAllText(outside, "Secret data about Acme Corp.");
        var ingest = BuildIngest(new StubExtractor());

        var result = await ingest.IngestAsync(outside, new DateOnly(2026, 6, 7),
            TestContext.Current.CancellationToken);

        Assert.Equal(IngestOutcome.SourceNotFound, result.Outcome);
        Assert.Empty(result.TouchedPages);
        Assert.Null(await _store.ReadAsync("memory/topics/acme-corp.md"));
    }

    [Fact]
    public async Task Ingest_tool_returns_a_readable_success_string()
    {
        var handler = BuildToolHandler();
        var call = new FunctionCallContent("c1", "ingest",
            new Dictionary<string, object?> { ["source_ref"] = "sources/sample.txt" });

        var result = await handler.HandleToolCallAsync(call, TestContext.Current.CancellationToken);

        var text = Assert.IsType<string>(result);
        Assert.Contains("Ingested 'sources/sample.txt' into 2 memory page(s)", text);
        Assert.Contains("memory/topics/acme-corp.md", text);
        Assert.Contains("memory/topics/john-smith.md", text);
    }

    [Fact]
    public async Task Ingest_tool_normalizes_a_vault_prefixed_source_ref()
    {
        var handler = BuildToolHandler();
        var call = new FunctionCallContent("c1", "ingest",
            new Dictionary<string, object?> { ["source_ref"] = "Vault/sources/sample.txt" });

        var result = await handler.HandleToolCallAsync(call, TestContext.Current.CancellationToken);

        var text = Assert.IsType<string>(result);
        Assert.StartsWith("Ingested 'sources/sample.txt'", text);
        Assert.NotNull(await _store.ReadAsync("memory/topics/acme-corp.md"));
    }

    [Fact]
    public async Task Ingest_tool_reports_a_missing_source_with_the_staging_recipe()
    {
        var handler = BuildToolHandler();
        var call = new FunctionCallContent("c1", "ingest",
            new Dictionary<string, object?> { ["source_ref"] = "sources/missing.txt" });

        var result = await handler.HandleToolCallAsync(call, TestContext.Current.CancellationToken);

        var text = Assert.IsType<string>(result);
        Assert.Contains("not found", text);
        Assert.Contains("Vault/sources/", text);
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
