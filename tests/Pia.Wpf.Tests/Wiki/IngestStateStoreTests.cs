using System;
using System.IO;
using System.Threading.Tasks;
using Pia.Services.Interfaces;
using Pia.Services.Wiki;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Wiki;

public class IngestStateStoreTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly IngestStateStore _store;

    public IngestStateStoreTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"pia-ingeststate-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tmpDir);
        _store = new IngestStateStore($"Data Source={Path.Combine(_tmpDir, "history.db")}");
    }

    public void Dispose()
    {
        TempPath.Remove(_tmpDir);
    }

    [Fact]
    public async Task Upsert_then_get_roundtrips()
    {
        await _store.UpsertAsync(new IngestStateEntry(
            "sources/a.txt", "HASH1", IngestOutcome.Success, ["memory/topics/x.md"], DateTimeOffset.UtcNow));

        var entry = await _store.GetAsync("sources/a.txt");
        Assert.NotNull(entry);
        Assert.Equal("HASH1", entry!.ContentHash);
        Assert.Equal(IngestOutcome.Success, entry.Outcome);
        Assert.Equal(["memory/topics/x.md"], entry.TouchedPages);
    }

    [Fact]
    public async Task SourceRef_lookup_is_case_insensitive()
    {
        await _store.UpsertAsync(new IngestStateEntry(
            "sources/A.txt", "HASH1", IngestOutcome.Success, [], DateTimeOffset.UtcNow));

        Assert.NotNull(await _store.GetAsync("sources/a.txt"));

        // Case-variant upsert hits the SAME row, not a second one.
        await _store.UpsertAsync(new IngestStateEntry(
            "sources/a.TXT", "HASH2", IngestOutcome.Success, [], DateTimeOffset.UtcNow));
        var all = await _store.ListAsync();
        var entry = Assert.Single(all);
        Assert.Equal("HASH2", entry.ContentHash);
    }

    [Fact]
    public async Task Delete_removes_the_row()
    {
        await _store.UpsertAsync(new IngestStateEntry(
            "sources/a.txt", "HASH1", IngestOutcome.NoEntities, [], DateTimeOffset.UtcNow));
        await _store.DeleteAsync("sources/a.txt");
        Assert.Null(await _store.GetAsync("sources/a.txt"));
        Assert.Empty(await _store.ListAsync());
    }

    [Fact]
    public async Task ClearAllAsync_removes_all_rows()
    {
        await _store.UpsertAsync(new IngestStateEntry(
            "sources/a.txt", "HASH1", IngestOutcome.Success, ["memory/topics/x.md"], DateTimeOffset.UtcNow));
        await _store.UpsertAsync(new IngestStateEntry(
            "sources/b.txt", "HASH2", IngestOutcome.NoEntities, [], DateTimeOffset.UtcNow));

        await _store.ClearAllAsync();

        Assert.Empty(await _store.ListAsync());
    }
}
