using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Infrastructure.Vault;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.Wiki;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Wiki;

/// <summary>
/// Task 7.2 lint coherence pass tests for <see cref="LintService"/>: a real temp
/// <see cref="SqliteContext"/> + <see cref="VaultStore"/> + a deterministic
/// <see cref="StubEmbeddingService"/>, with a vault seeded to trigger EACH of the six checks
/// (Contradiction, Stale, Orphan, MissingXref auto-fix, Duplicate merge, GapPage) plus the journal
/// append. No API key required — every collaborator is real or a deterministic stub.
/// </summary>
public class LintServiceTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _vaultRoot;
    private readonly SqliteContext _ctx;
    private readonly MarkdownVaultParser _parser = new();
    private readonly VaultStore _store;
    private readonly StubEmbeddingService _embeddings = new();
    private readonly VaultLogService _log;
    private readonly DateOnly _date = new(2026, 6, 7);

    public LintServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"pia-lint-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tmpDir);
        _vaultRoot = Path.Combine(_tmpDir, "vault");
        Directory.CreateDirectory(_vaultRoot);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
        _store = new VaultStore(_vaultRoot, _parser);
        _log = new VaultLogService(_store, NullLogger<VaultLogService>.Instance);
    }

    public void Dispose()
    {
        _ctx.Dispose();
        TempPath.Remove(_tmpDir);
    }

    // A REAL IngestService (only the two LLM-bound collaborators are fakes), so the duplicate check
    // exercises the actual merge — provenance union, archive, index removal, link retarget — rather
    // than a stub that only mimics its file moves.
    private LintService BuildLint()
        => new(_store, _ctx, _embeddings, _log, BuildIngest(), NullLogger<LintService>.Instance);

    private IIngestService BuildIngest()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        var index = new VaultIndexService(_store, NullLogger<VaultIndexService>.Instance);
        return new IngestService(
            new NoTopicsExtractor(),
            _store,
            index,
            _log,
            new EchoSynthesizer(),
            new VaultCharterService(_store, NullLogger<VaultCharterService>.Instance),
            new VaultTemplateService(_store, NullLogger<VaultTemplateService>.Instance),
            () => throw new InvalidOperationException("tokenization is off in these tests"),
            settings,
            NullLogger<IngestService>.Instance);
    }

    private sealed class NoTopicsExtractor : IIngestExtractor
    {
        public Task<IReadOnlyList<ExtractedTopic>> DiscoverTopicsAsync(
            string content, string charter, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ExtractedTopic>>([]);
    }

    private sealed class EchoSynthesizer : IIngestSynthesizer
    {
        public Task<SynthesizedPage> SynthesizeAsync(
            string title, string category, string charter, string template,
            IReadOnlyList<(string Ref, string Text)> sources,
            IReadOnlyCollection<string> knownSlugs, CancellationToken ct = default)
            => Task.FromResult(new SynthesizedPage(
                $"{title} merged across {sources.Count} source(s).", $"{title} summary"));
    }

    private void SeedPage(string relativePath, string title, string body)
    {
        var frontmatter =
            "---\n" +
            "pia: managed\n" +
            $"id: {Guid.NewGuid():d}\n" +
            "type: topic\n" +
            $"title: {title}\n" +
            "created: 2026-06-01T00:00:00Z\n" +
            "updated: 2026-06-01T00:00:00Z\n" +
            "schemaVersion: 1\n" +
            "---\n";
        var full = Path.Combine(_vaultRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, frontmatter + body);
    }

    private void SeedPageWithSources(string relativePath, string title, string sourcesFlowList, string body)
    {
        var frontmatter =
            "---\n" +
            "pia: managed\n" +
            $"id: {Guid.NewGuid():d}\n" +
            "type: topic\n" +
            $"title: {title}\n" +
            $"sources: {sourcesFlowList}\n" +
            "created: 2026-06-01T00:00:00Z\n" +
            "updated: 2026-06-01T00:00:00Z\n" +
            "schemaVersion: 1\n" +
            "---\n";
        var full = Path.Combine(_vaultRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, frontmatter + body);
    }

    [Fact]
    public async Task RunAsync_flags_a_contradiction_when_same_key_differs_across_pages()
    {
        // Two pages each carry the SAME entity field key with DIFFERENT values.
        SeedPage("memory/topics/acme-corp.md", "Acme Corp", "- tier: enterprise\n");
        SeedPage("memory/topics/acme-deal.md", "Acme Corp", "- tier: starter\n");

        var report = await BuildLint().RunAsync(_date, true, TestContext.Current.CancellationToken);

        Assert.Contains(report.Findings, f => f.Kind == LintKind.Contradiction);
    }

    [Fact]
    public async Task RunAsync_flags_a_stale_page_when_its_source_ref_is_missing()
    {
        // sources: points at a file that does not exist under the vault root.
        SeedPageWithSources(
            "memory/topics/ghost.md", "Ghost", "[sources/never-existed.txt]", "- note: x\n");

        var report = await BuildLint().RunAsync(_date, true, TestContext.Current.CancellationToken);

        Assert.Contains(report.Findings, f => f.Kind == LintKind.Stale);
    }

    [Fact]
    public async Task RunAsync_flags_an_orphan_page_with_no_inbound_links()
    {
        // "lonely" is never linked from any other page body.
        SeedPage("memory/topics/lonely.md", "Lonely", "- note: nobody links here\n");
        SeedPage("memory/topics/hub.md", "Hub", "Links to [[topics/other]] but not lonely.\n");
        SeedPage("memory/topics/other.md", "Other", "- note: linked by hub\n");

        var report = await BuildLint().RunAsync(_date, true, TestContext.Current.CancellationToken);

        Assert.Contains(
            report.Findings,
            f => f.Kind == LintKind.Orphan && f.Detail.Contains("lonely", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunAsync_auto_fixes_a_missing_xref_by_inserting_the_wikilink()
    {
        // "Berlin" has its own topic page; the mentioning page names it but does not link it.
        SeedPage("memory/topics/berlin.md", "Berlin", "- country: Germany\n");
        SeedPage("memory/topics/trip.md", "Trip", "We flew to Berlin in spring.\n");

        var report = await BuildLint().RunAsync(_date, true, TestContext.Current.CancellationToken);

        Assert.Contains(report.Findings, f => f.Kind == LintKind.MissingXref && f.AutoFixed);

        var trip = await _store.ReadAsync("memory/topics/trip.md");
        Assert.NotNull(trip);
        Assert.Contains("[[topics/berlin]]", trip!.RawText);
    }

    [Fact]
    public async Task RunAsync_merges_duplicate_topic_pages_by_archiving_one()
    {
        // The stub embedder is keyed on body text; identical bodies -> cosine 1.0 (>= 0.9) -> merge.
        SeedPage("memory/topics/dup-a.md", "Dup A", "- fact: identical body content here\n");
        SeedPage("memory/topics/dup-b.md", "Dup B", "- fact: identical body content here\n");

        var report = await BuildLint().RunAsync(_date, true, TestContext.Current.CancellationToken);

        Assert.Contains(report.Findings, f => f.Kind == LintKind.Duplicate && f.AutoFixed);

        // One of the two originals is gone; an archive copy exists under memory/.archive/.
        var topics = await _store.EnumerateAsync("memory/topics/*.md");
        var remaining = topics.Count(p => p.Replace('\\', '/').EndsWith("dup-a.md", StringComparison.Ordinal)
                                          || p.Replace('\\', '/').EndsWith("dup-b.md", StringComparison.Ordinal));
        Assert.Equal(1, remaining);

        var archived = await _store.EnumerateAsync("memory/.archive/*.md");
        Assert.NotEmpty(archived);
    }

    // The cleanup action shows this report before asking for confirmation, so a dry run must report
    // exactly what a real run would do and change nothing at all.
    [Fact]
    public async Task RunAsync_dry_run_reports_the_merge_without_performing_it()
    {
        SeedPage("memory/topics/dup-a.md", "Dup A", "- fact: identical body content here\n");
        SeedPage("memory/topics/dup-b.md", "Dup B", "- fact: identical body content here\n");
        SeedPage("memory/topics/berlin.md", "Berlin", "- country: Germany\n");
        SeedPage("memory/topics/trip.md", "Trip", "We flew to Berlin in spring.\n");
        var beforeTrip = await _store.ReadAsync("memory/topics/trip.md");

        var report = await BuildLint().RunAsync(_date, false, TestContext.Current.CancellationToken);

        Assert.Contains(report.Findings, f => f.Kind == LintKind.Duplicate);
        Assert.Contains(report.Findings, f => f.Kind == LintKind.MissingXref);
        Assert.DoesNotContain(report.Findings, f => f.AutoFixed);

        Assert.NotNull(await _store.ReadAsync("memory/topics/dup-a.md"));
        Assert.NotNull(await _store.ReadAsync("memory/topics/dup-b.md"));
        Assert.Empty(await _store.EnumerateAsync("memory/.archive/*.md"));
        Assert.Equal(beforeTrip!.RawText, (await _store.ReadAsync("memory/topics/trip.md"))!.RawText);
    }

    [Fact]
    public async Task RunAsync_flags_a_gap_page_for_a_wikilink_with_no_target_file()
    {
        // The link target topics/missing.md does not exist.
        SeedPage("memory/topics/source.md", "Source", "See [[topics/missing]] for more.\n");

        var report = await BuildLint().RunAsync(_date, true, TestContext.Current.CancellationToken);

        Assert.Contains(
            report.Findings,
            f => f.Kind == LintKind.GapPage && f.Detail.Contains("missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunAsync_appends_each_finding_to_the_log()
    {
        SeedPageWithSources(
            "memory/topics/ghost.md", "Ghost", "[sources/never-existed.txt]", "- note: x\n");

        var report = await BuildLint().RunAsync(_date, true, TestContext.Current.CancellationToken);

        Assert.NotEmpty(report.Findings);

        var log = await _store.ReadAsync("memory/log.md");
        Assert.NotNull(log);
        Assert.Contains("] lint |", log!.RawText);
    }

}
