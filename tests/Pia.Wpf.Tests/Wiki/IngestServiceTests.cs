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
/// Ingest pipeline tests for <see cref="IngestService"/> under the topic-driven synthesis model: a real
/// temp <see cref="VaultStore"/> + real <see cref="VaultIndexService"/>/<see cref="VaultLogService"/>,
/// with a FAKE <see cref="IIngestExtractor"/> returning fixed topics and a FAKE
/// <see cref="IIngestSynthesizer"/> returning a deterministic body per title — so the pipeline is
/// exercised without an API key. The source <c>sources/sample.txt</c> is seeded under the vault root.
/// </summary>
public class IngestServiceTests : IDisposable
{
    private const string ManagedMarker = "<!-- pia:managed -->";

    private readonly string _tmpDir;
    private readonly string _vaultRoot;
    private readonly MarkdownVaultParser _parser = new();
    private readonly VaultStore _store;
    private readonly VaultIndexService _index;
    private readonly VaultLogService _log;
    private readonly VaultCharterService _charter;

    public IngestServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"pia-ingest-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tmpDir);
        _vaultRoot = Path.Combine(_tmpDir, "vault");
        Directory.CreateDirectory(_vaultRoot);
        _store = new VaultStore(_vaultRoot, _parser);
        _index = new VaultIndexService(_store, NullLogger<VaultIndexService>.Instance);
        _log = new VaultLogService(_store, NullLogger<VaultLogService>.Instance);
        _charter = new VaultCharterService(_store, NullLogger<VaultCharterService>.Instance);

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

    private void SeedSource(string name, string content)
        => File.WriteAllText(Path.Combine(_vaultRoot, "sources", name), content);

    private IngestService BuildIngest(IIngestExtractor extractor, IIngestSynthesizer synth)
        => new(extractor, _store, _index, _log, synth, _charter, NullLogger<IngestService>.Instance);

    private IngestToolHandler BuildToolHandler()
        => new(new PassthroughScheduler(BuildIngest(
                new FakeExtractor(
                    new ExtractedTopic("Acme Corp", "organization"),
                    new ExtractedTopic("John Smith", "person")),
                new FakeSynthesizer())),
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

    // Fixed topics — no API key required. Ignores content (returns the same topics for any source).
    private sealed class FakeExtractor(params ExtractedTopic[] topics) : IIngestExtractor
    {
        public Task<IReadOnlyList<ExtractedTopic>> DiscoverTopicsAsync(
            string content, string charter, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ExtractedTopic>>(topics);
    }

    // Deterministic synthesizer: records each call as (title, sourceCount) and returns a body encoding
    // the source count. Titles passed to the ctor synthesize to an EMPTY page (models the transient
    // provider failure).
    private sealed class FakeSynthesizer : IIngestSynthesizer
    {
        private readonly HashSet<string> _emptyTitles;

        public FakeSynthesizer(params string[] emptyTitles)
            => _emptyTitles = new HashSet<string>(emptyTitles, StringComparer.OrdinalIgnoreCase);

        public List<(string Title, int SourceCount)> Calls { get; } = new();

        // Per-call source refs, so tests can assert WHICH sources a (re-)synthesis merged.
        public List<IReadOnlyList<string>> CallRefs { get; } = new();

        public Task<SynthesizedPage> SynthesizeAsync(string title, string category, string charter,
            IReadOnlyList<(string Ref, string Text)> sources, CancellationToken ct = default)
        {
            Calls.Add((title, sources.Count));
            CallRefs.Add(sources.Select(s => s.Ref).ToList());
            if (_emptyTitles.Contains(title))
            {
                return Task.FromResult(new SynthesizedPage(string.Empty, string.Empty));
            }

            return Task.FromResult(new SynthesizedPage(
                $"{title} is a synthesized topic from {sources.Count} source(s).", $"{title} summary"));
        }
    }

    [Fact]
    public async Task Ingest_creates_synthesized_topic_pages()
    {
        var ingest = BuildIngest(
            new FakeExtractor(
                new ExtractedTopic("Acme Corp", "organization"),
                new ExtractedTopic("GDPR", "regulation")),
            new FakeSynthesizer());

        var result = await ingest.IngestAsync("sources/sample.txt", new DateOnly(2026, 7, 8),
            TestContext.Current.CancellationToken);

        Assert.Equal(IngestOutcome.Success, result.Outcome);
        Assert.Equal(2, result.TouchedPages.Count);

        var acme = await _store.ReadAsync("memory/topics/acme-corp.md");
        Assert.NotNull(acme);
        Assert.Equal("topic", acme!.Frontmatter["type"]);
        Assert.Equal("organization", acme.Frontmatter["category"]);
        Assert.Contains("Acme Corp is a synthesized topic", acme.RawText);
        Assert.DoesNotContain("## Source:", acme.RawText);
        Assert.Contains("sources/sample.txt",
            string.Join(",", SourcesProvenance.ReadSourceRefs(acme.RawText)));
    }

    [Fact]
    public async Task Reingest_after_second_source_unions_sources()
    {
        SeedSource("second.txt", "More context about Acme Corp.");
        var synth = new FakeSynthesizer();
        var ingest = BuildIngest(new FakeExtractor(new ExtractedTopic("Acme Corp", "organization")), synth);

        await ingest.IngestAsync("sources/sample.txt", new DateOnly(2026, 7, 8),
            TestContext.Current.CancellationToken);
        await ingest.IngestAsync("sources/second.txt", new DateOnly(2026, 7, 9),
            TestContext.Current.CancellationToken);

        var acme = await _store.ReadAsync("memory/topics/acme-corp.md");
        var refs = SourcesProvenance.ReadSourceRefs(acme!.RawText);
        Assert.Contains("sources/sample.txt", refs);
        Assert.Contains("sources/second.txt", refs);

        // The second ingest re-synthesized across BOTH raw sources.
        Assert.Contains(synth.Calls, c => c.Title == "Acme Corp" && c.SourceCount == 2);
    }

    [Fact]
    public async Task Ingest_preserves_manual_preamble()
    {
        var ingest = BuildIngest(new FakeExtractor(new ExtractedTopic("Acme Corp", "organization")),
            new FakeSynthesizer());

        // Seed a page carrying a manual preamble ABOVE the managed sentinel. The fake body has no '##'
        // heading — the realistic case that doc.Preamble would otherwise mis-fold into the preamble.
        var seeded = VaultFrontmatter.BuildPreserving(null, "Acme Corp", "organization") + "\n"
            + "Manually remembered fact.\n\n" + ManagedMarker + "\nOld body.\n";
        await _store.WriteAtomicAsync("memory/topics/acme-corp.md", seeded);

        await ingest.IngestAsync("sources/sample.txt", new DateOnly(2026, 7, 8),
            TestContext.Current.CancellationToken);
        await ingest.IngestAsync("sources/sample.txt", new DateOnly(2026, 7, 9),
            TestContext.Current.CancellationToken);

        var acme = await _store.ReadAsync("memory/topics/acme-corp.md");
        Assert.NotNull(acme);

        // Preamble survives verbatim and exactly once (no accumulation across the two ingests).
        Assert.Equal(1, acme!.RawText.Split("Manually remembered fact.").Length - 1);

        // The body below the sentinel is the latest synthesis; the old body is gone.
        var idx = acme.RawText.IndexOf(ManagedMarker, StringComparison.Ordinal);
        Assert.Contains("Acme Corp is a synthesized topic", acme.RawText[idx..]);
        Assert.DoesNotContain("Old body.", acme.RawText);
    }

    [Fact]
    public async Task Ingest_touches_only_notable_topics()
    {
        var ingest = BuildIngest(
            new FakeExtractor(
                new ExtractedTopic("Acme Corp", "organization"),
                new ExtractedTopic("John Smith", "person")),
            new FakeSynthesizer());

        var result = await ingest.IngestAsync("sources/sample.txt", new DateOnly(2026, 7, 8),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.TouchedPages.Count);

        var pages = await _store.EnumerateAsync("memory/topics/*.md");
        Assert.Equal(2, pages.Count);

        var index = await _store.ReadAsync("memory/index.md");
        Assert.NotNull(index);
        Assert.Contains("[[topics/acme-corp]]", index!.RawText);
        Assert.Contains("[[topics/john-smith]]", index.RawText);
    }

    [Fact]
    public async Task Reingest_preserves_id_and_created()
    {
        var ext = new FakeExtractor(new ExtractedTopic("Acme Corp", "organization"));
        var ingest = BuildIngest(ext, new FakeSynthesizer());

        await ingest.IngestAsync("sources/sample.txt", new DateOnly(2026, 7, 8),
            TestContext.Current.CancellationToken);
        var first = await _store.ReadAsync("memory/topics/acme-corp.md");
        var id = first!.Frontmatter["id"];
        var created = first.Frontmatter["created"];

        SeedSource("second.txt", "Additional notes about Acme Corp.");
        var synth2 = new FakeSynthesizer();
        var ingest2 = BuildIngest(ext, synth2);
        await ingest2.IngestAsync("sources/second.txt", new DateOnly(2026, 7, 9),
            TestContext.Current.CancellationToken);

        var after = await _store.ReadAsync("memory/topics/acme-corp.md");
        Assert.Equal(id, after!.Frontmatter["id"]);
        Assert.Equal(created, after.Frontmatter["created"]);
        // Seconds-resolution timestamps → the re-stamped 'updated' is >= 'created' (not strictly >).
        Assert.True(string.CompareOrdinal(after.Frontmatter["updated"], created) >= 0);
        // Evidence the page WAS re-synthesized across both sources.
        Assert.Contains(synth2.Calls, c => c.SourceCount == 2);
    }

    [Fact]
    public async Task Ingest_returns_SynthesisFailed_when_any_synthesis_comes_back_empty()
    {
        var ingest = BuildIngest(
            new FakeExtractor(
                new ExtractedTopic("Acme Corp", "organization"),
                new ExtractedTopic("GDPR", "regulation")),
            new FakeSynthesizer("GDPR")); // the second topic synthesizes to nothing

        var result = await ingest.IngestAsync("sources/sample.txt", new DateOnly(2026, 7, 8),
            TestContext.Current.CancellationToken);

        Assert.Equal(IngestOutcome.SynthesisFailed, result.Outcome);
        // The first (successful) page WAS still written — partial output is kept; the retry re-does it.
        Assert.NotNull(await _store.ReadAsync("memory/topics/acme-corp.md"));
        Assert.Contains("memory/topics/acme-corp.md", result.TouchedPages);
        // The failing topic's page was never written.
        Assert.Null(await _store.ReadAsync("memory/topics/gdpr.md"));
    }

    [Fact]
    public async Task Remove_last_source_deletes_page_and_index()
    {
        var ingest = BuildIngest(new FakeExtractor(new ExtractedTopic("Acme Corp", "organization")),
            new FakeSynthesizer());
        await ingest.IngestAsync("sources/sample.txt", new DateOnly(2026, 7, 8),
            TestContext.Current.CancellationToken);
        Assert.NotNull(await _store.ReadAsync("memory/topics/acme-corp.md"));

        await ingest.RemoveContributionsAsync("sources/sample.txt",
            new[] { "memory/topics/acme-corp.md" }, TestContext.Current.CancellationToken);

        // The page's only source is gone → the page and its index entry are deleted.
        Assert.Null(await _store.ReadAsync("memory/topics/acme-corp.md"));
        var index = await _store.ReadAsync("memory/index.md");
        Assert.DoesNotContain("[[topics/acme-corp]]", index?.RawText ?? string.Empty);
    }

    [Fact]
    public async Task Remove_one_of_two_sources_resynthesizes()
    {
        SeedSource("second.txt", "More context about Acme Corp.");
        var ingest = BuildIngest(new FakeExtractor(new ExtractedTopic("Acme Corp", "organization")),
            new FakeSynthesizer());
        await ingest.IngestAsync("sources/sample.txt", new DateOnly(2026, 7, 8),
            TestContext.Current.CancellationToken);
        await ingest.IngestAsync("sources/second.txt", new DateOnly(2026, 7, 9),
            TestContext.Current.CancellationToken);

        // A fresh synthesizer isolates the removal's re-synthesis call from the ingest calls above.
        var synth = new FakeSynthesizer();
        var remover = BuildIngest(new FakeExtractor(new ExtractedTopic("Acme Corp", "organization")), synth);
        await remover.RemoveContributionsAsync("sources/sample.txt",
            new[] { "memory/topics/acme-corp.md" }, TestContext.Current.CancellationToken);

        var acme = await _store.ReadAsync("memory/topics/acme-corp.md");
        Assert.NotNull(acme);
        // sources: now lists only the remaining source.
        Assert.Equal(new[] { "sources/second.txt" }, SourcesProvenance.ReadSourceRefs(acme!.RawText));

        // Re-synthesized across ONLY the remaining source (B).
        var call = Assert.Single(synth.Calls);
        Assert.Equal("Acme Corp", call.Title);
        Assert.Equal(1, call.SourceCount);
        Assert.Equal(new[] { "sources/second.txt" }, synth.CallRefs.Single());
    }

    [Fact]
    public async Task Remove_with_empty_synthesis_still_prunes_sources_frontmatter()
    {
        SeedSource("second.txt", "More context about Acme Corp.");
        var ingest = BuildIngest(new FakeExtractor(new ExtractedTopic("Acme Corp", "organization")),
            new FakeSynthesizer());
        await ingest.IngestAsync("sources/sample.txt", new DateOnly(2026, 7, 8),
            TestContext.Current.CancellationToken);
        await ingest.IngestAsync("sources/second.txt", new DateOnly(2026, 7, 9),
            TestContext.Current.CancellationToken);

        var before = await _store.ReadAsync("memory/topics/acme-corp.md");
        var bodyBefore = before!.RawText[before.RawText.IndexOf(ManagedMarker, StringComparison.Ordinal)..];

        // A synthesizer that returns EMPTY for this topic models a dead provider during removal.
        var remover = BuildIngest(new FakeExtractor(new ExtractedTopic("Acme Corp", "organization")),
            new FakeSynthesizer("Acme Corp"));
        await remover.RemoveContributionsAsync("sources/sample.txt",
            new[] { "memory/topics/acme-corp.md" }, TestContext.Current.CancellationToken);

        var acme = await _store.ReadAsync("memory/topics/acme-corp.md");
        Assert.NotNull(acme);
        // sources: pruned deterministically even though the best-effort synthesis produced nothing.
        Assert.Equal(new[] { "sources/second.txt" }, SourcesProvenance.ReadSourceRefs(acme!.RawText));
        // Body is left UNCHANGED (stale is acceptable — it self-heals on the next ingest).
        var bodyAfter = acme.RawText[acme.RawText.IndexOf(ManagedMarker, StringComparison.Ordinal)..];
        Assert.Equal(bodyBefore, bodyAfter);
        // Index entry survives.
        var index = await _store.ReadAsync("memory/index.md");
        Assert.Contains("[[topics/acme-corp]]", index!.RawText);
    }

    [Fact]
    public async Task IngestAsync_refuses_a_traversal_path_that_escapes_the_vault()
    {
        // A '..' target that genuinely resolves OUTSIDE the vault root (<tmpDir>/vault -> <tmpDir>).
        File.WriteAllText(Path.Combine(_tmpDir, "outside.txt"), "Secret data about Acme Corp.");
        var ingest = BuildIngest(new FakeExtractor(new ExtractedTopic("Acme Corp", "organization")),
            new FakeSynthesizer());

        var result = await ingest.IngestAsync("../outside.txt", new DateOnly(2026, 7, 8),
            TestContext.Current.CancellationToken);

        Assert.Equal(IngestOutcome.SourceNotFound, result.Outcome);
        Assert.Empty(result.TouchedPages);
        // The guard must stop BEFORE discovery — no topic page is written from the outside file.
        Assert.Null(await _store.ReadAsync("memory/topics/acme-corp.md"));
    }

    [Fact]
    public async Task IngestAsync_refuses_an_absolute_path_outside_the_vault()
    {
        var outside = Path.Combine(_tmpDir, "outside-abs.txt");
        File.WriteAllText(outside, "Secret data about Acme Corp.");
        var ingest = BuildIngest(new FakeExtractor(new ExtractedTopic("Acme Corp", "organization")),
            new FakeSynthesizer());

        var result = await ingest.IngestAsync(outside, new DateOnly(2026, 7, 8),
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
}
