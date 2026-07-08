using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure.Vault;
using Pia.Services.Wiki;
using Xunit;

namespace Pia.Tests.Wiki;

/// <summary>
/// <see cref="VaultSourcesService"/> surfaces the vault's <c>sources/</c> RAW layer joined against the
/// ingest provenance that <see cref="IngestService"/> records in topic-page <c>sources:</c> frontmatter
/// (written as a YAML flow list — read back leniently via <see cref="SourcesProvenance"/>).
/// </summary>
public class VaultSourcesServiceTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _vaultRoot;
    private readonly VaultStore _store;
    private readonly VaultSourcesService _service;

    public VaultSourcesServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"pia-sources-test-{Guid.NewGuid()}");
        _vaultRoot = Path.Combine(_tmpDir, "vault");
        Directory.CreateDirectory(_vaultRoot);
        _store = new VaultStore(_vaultRoot, new MarkdownVaultParser());
        _service = new VaultSourcesService(_store, NullLogger<VaultSourcesService>.Instance);
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

    private void SeedSource(string relativeName, string content = "raw content")
    {
        var path = Path.Combine(_vaultRoot, "sources", relativeName.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private Task SeedTopicPage(string slug, string sourcesLine)
        => _store.WriteAtomicAsync(
            $"memory/topics/{slug}.md",
            $"---\nid: {Guid.NewGuid()}\ntype: topic\n{sourcesLine}\n---\n# {slug}\n\n- a fact\n");

    [Fact]
    public async Task Missing_sources_dir_yields_empty_list()
    {
        Assert.Empty(await _service.ListSourcesAsync());
    }

    [Fact]
    public async Task Sources_join_topic_provenance_and_order_by_name()
    {
        SeedSource("beta.txt", "beta body");
        SeedSource("alpha.txt", "alpha body");
        SeedSource("scan.pdf", "binary-ish");
        await SeedTopicPage("acme-corp", "sources: [sources/alpha.txt]");
        await SeedTopicPage("john-smith", "sources: [sources/alpha.txt, sources/missing.txt]");
        // A topic page without provenance must not disturb the counts.
        await SeedTopicPage("plain", "updated: 2026-07-07T00:00:00Z");

        var items = await _service.ListSourcesAsync();

        Assert.Equal(new[] { "alpha.txt", "beta.txt", "scan.pdf" }, items.Select(i => i.Name).ToArray());

        var alpha = items[0];
        Assert.Equal("sources/alpha.txt", alpha.RelativePath);
        Assert.Equal(2, alpha.TopicPageCount);
        Assert.True(alpha.IsIngested);
        Assert.True(alpha.IsText);
        Assert.Equal("alpha body".Length, alpha.Bytes);

        var beta = items[1];
        Assert.Equal(0, beta.TopicPageCount);
        Assert.False(beta.IsIngested);
        Assert.True(beta.IsText);

        // Non-text sources are listed (visible!) but flagged as not ingestable.
        var scan = items[2];
        Assert.False(scan.IsText);
        Assert.False(scan.IsIngested);
    }

    [Fact]
    public async Task Subfolder_sources_get_forward_slash_relative_paths()
    {
        SeedSource("reports/q2.csv");

        var item = Assert.Single(await _service.ListSourcesAsync());
        Assert.Equal("sources/reports/q2.csv", item.RelativePath);
        Assert.Equal("q2.csv", item.Name);
    }

    [Fact]
    public async Task Provenance_ref_casing_differences_still_match()
    {
        // Ingest stores the model-provided spelling of the ref, which may differ in casing from the
        // on-disk file name on a case-insensitive filesystem.
        SeedSource("Q2-Report.txt");
        await SeedTopicPage("acme-corp", "sources: [sources/q2-report.txt]");

        var item = Assert.Single(await _service.ListSourcesAsync());
        Assert.Equal(1, item.TopicPageCount);
    }

    [Fact]
    public async Task Hand_edited_frontmatter_degrades_to_not_ingested()
    {
        SeedSource("alpha.txt");
        // No frontmatter at all — the lenient reader must return no refs, not throw.
        await _store.WriteAtomicAsync("memory/topics/broken.md", "# broken\n\nno frontmatter here\n");

        var item = Assert.Single(await _service.ListSourcesAsync());
        Assert.False(item.IsIngested);
    }
}
