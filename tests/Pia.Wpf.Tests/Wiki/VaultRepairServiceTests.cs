using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure.Vault;
using Pia.Services.Interfaces;
using Pia.Services.Wiki;
using Xunit;

namespace Pia.Tests.Wiki;

/// <summary>
/// Unit tests for <see cref="VaultRepairService"/> — the startup pass that unsticks topic pages whose
/// frontmatter no longer parses, which ingest cannot repair on its own because it reads a page before
/// rewriting it.
/// </summary>
public class VaultRepairServiceTests : IDisposable
{
    private const string Poisoned =
        "---\npia: managed\ntitle: {\"subject\": \"Ilka Brenner\", \"category\": \"person\"},\n---\nbody\n";

    private readonly string _tmpDir;
    private readonly string _vaultRoot;
    private readonly VaultStore _store;
    private readonly VaultIndexService _index;
    private readonly IngestStateStore _state;

    public VaultRepairServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"pia-vaultrepair-test-{Guid.NewGuid()}");
        _vaultRoot = Path.Combine(_tmpDir, "vault");
        Directory.CreateDirectory(Path.Combine(_vaultRoot, "memory", "topics"));
        _store = new VaultStore(_vaultRoot, new MarkdownVaultParser());
        _index = new VaultIndexService(_store, NullLogger<VaultIndexService>.Instance);
        _state = new IngestStateStore($"Data Source={Path.Combine(_tmpDir, "history.db")}");
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
            // Best-effort cleanup of the temp dir (SQLite pooling may still hold history.db).
        }
    }

    private VaultRepairService Build() =>
        new(_store, _index, _state, NullLogger<VaultRepairService>.Instance);

    private void WritePage(string slug, string text) =>
        File.WriteAllText(Path.Combine(_vaultRoot, "memory", "topics", slug + ".md"), text);

    [Fact]
    public async Task Archives_an_unparseable_page_and_clears_its_source_state()
    {
        WritePage("ilka-brenner", Poisoned);
        await _state.UpsertAsync(new IngestStateEntry(
            "sources/roster.txt", "hash", IngestOutcome.Success,
            ["memory/topics/ilka-brenner.md"], DateTimeOffset.UtcNow));

        var repaired = await Build().RepairUnparseableTopicPagesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, repaired);
        Assert.False(File.Exists(Path.Combine(_vaultRoot, "memory", "topics", "ilka-brenner.md")));
        Assert.True(File.Exists(Path.Combine(_vaultRoot, "memory", ".archive", "ilka-brenner.md")));
        Assert.Null(await _state.GetAsync("sources/roster.txt"));
    }

    [Fact]
    public async Task Archived_copy_keeps_the_original_text()
    {
        WritePage("ilka-brenner", Poisoned);

        await Build().RepairUnparseableTopicPagesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            Poisoned,
            File.ReadAllText(Path.Combine(_vaultRoot, "memory", ".archive", "ilka-brenner.md")));
    }

    [Fact]
    public async Task Leaves_a_healthy_page_and_its_state_alone()
    {
        WritePage("pia", VaultFrontmatter.BuildPreserving(null, "Pia", "product") + "\nbody\n");
        await _state.UpsertAsync(new IngestStateEntry(
            "sources/notes.txt", "hash", IngestOutcome.Success,
            ["memory/topics/pia.md"], DateTimeOffset.UtcNow));

        var repaired = await Build().RepairUnparseableTopicPagesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, repaired);
        Assert.True(File.Exists(Path.Combine(_vaultRoot, "memory", "topics", "pia.md")));
        Assert.NotNull(await _state.GetAsync("sources/notes.txt"));
    }

    // A page with no frontmatter block at all is hand-added, not ingest-managed.
    [Fact]
    public async Task Leaves_a_page_with_no_frontmatter_block_alone()
    {
        WritePage("hand-written", "# Hand written\n\nJust prose.\n");

        var repaired = await Build().RepairUnparseableTopicPagesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, repaired);
        Assert.True(File.Exists(Path.Combine(_vaultRoot, "memory", "topics", "hand-written.md")));
    }
}
