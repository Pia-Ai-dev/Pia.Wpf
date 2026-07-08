using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure.Vault;
using Pia.Services.Wiki;
using Xunit;

namespace Pia.Tests.Wiki;

/// <summary>
/// Tests for <see cref="VaultIndexService"/> — specifically the §8 rewrite's category sub-grouping of
/// the <c>## Topics</c> group, reading each topic page's frontmatter <c>category</c> at rewrite time.
/// Uses a real temp <see cref="VaultStore"/>, same setup shape as <see cref="IngestServiceTests"/>.
/// </summary>
public class VaultIndexServiceTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _vaultRoot;
    private readonly MarkdownVaultParser _parser = new();
    private readonly VaultStore _store;
    private readonly VaultIndexService _index;

    public VaultIndexServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"pia-index-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tmpDir);
        _vaultRoot = Path.Combine(_tmpDir, "vault");
        Directory.CreateDirectory(_vaultRoot);
        _store = new VaultStore(_vaultRoot, _parser);
        _index = new VaultIndexService(_store, NullLogger<VaultIndexService>.Instance);
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

    private async Task SeedTopicAsync(string slug, string title, string? category)
    {
        var content = VaultFrontmatter.Build("topic", title, category) + "\n" + title + " body.\n";
        await _store.WriteAtomicAsync($"memory/topics/{slug}.md", content);
    }

    [Fact]
    public async Task Topics_group_is_subgrouped_by_category_in_canonical_order()
    {
        await SeedTopicAsync("pia", "Pia", "product");
        await SeedTopicAsync("gdpr", "GDPR", "regulation");
        await SeedTopicAsync("mystery", "Mystery", null);

        await _index.UpsertEntryAsync("memory/topics/pia.md", "Pia summary");
        await _index.UpsertEntryAsync("memory/topics/gdpr.md", "GDPR summary");
        await _index.UpsertEntryAsync("memory/topics/mystery.md", "Mystery summary");

        var doc = await _store.ReadAsync("memory/index.md");
        Assert.NotNull(doc);
        var text = doc!.RawText;

        var topicsIdx = text.IndexOf("## Topics", StringComparison.Ordinal);
        Assert.True(topicsIdx >= 0, "index should contain a ## Topics group");

        var productsIdx = text.IndexOf("### Products", topicsIdx, StringComparison.Ordinal);
        var regulationsIdx = text.IndexOf("### Regulations", topicsIdx, StringComparison.Ordinal);
        var otherIdx = text.IndexOf("### Other", topicsIdx, StringComparison.Ordinal);

        Assert.True(productsIdx >= 0, "### Products sub-heading expected");
        Assert.True(regulationsIdx >= 0, "### Regulations sub-heading expected");
        Assert.True(otherIdx >= 0, "### Other sub-heading expected");

        // Canonical order: Products before Regulations before Other.
        Assert.True(productsIdx < regulationsIdx, "Products must precede Regulations");
        Assert.True(regulationsIdx < otherIdx, "Regulations must precede Other");

        Assert.Contains("### Products\n- [[topics/pia]] — Pia summary", text);
        Assert.Contains("### Regulations\n- [[topics/gdpr]] — GDPR summary", text);
        Assert.Contains("### Other\n- [[topics/mystery]] — Mystery summary", text);
    }
}
