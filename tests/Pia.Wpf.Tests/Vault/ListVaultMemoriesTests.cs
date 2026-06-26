using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Infrastructure.Vault;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Vault;

/// <summary>
/// Tests for the vault read/list surface (Task B.2): enumerate memory records as view items, scoped to
/// genuine record files (housekeeping/scaffolding and the sources/ RAW layer are excluded), with one
/// item per <c>##</c> section and one item per freeform preamble file.
/// </summary>
public class ListVaultMemoriesTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _vaultRoot;
    private readonly SqliteContext _ctx;
    private readonly MarkdownVaultParser _parser = new();
    private readonly VaultStore _store;
    private readonly StubEmbeddingService _embeddings = new();
    private readonly SyncDeleteTrackerService _deleteTracker;
    private readonly SectionUpsertService _upsert;

    public ListVaultMemoriesTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"pia-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tmpDir);
        _vaultRoot = Path.Combine(_tmpDir, "vault");
        Directory.CreateDirectory(_vaultRoot);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
        _store = new VaultStore(_vaultRoot, _parser);
        _deleteTracker = new SyncDeleteTrackerService(_tmpDir, NullLogger<SyncDeleteTrackerService>.Instance);
        _upsert = new SectionUpsertService(_embeddings);
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

    private MemoryService BuildService()
        => new(_ctx, NullLogger<MemoryService>.Instance, _embeddings, _deleteTracker, _store, _upsert);

    private async Task SeedVaultAsync()
    {
        await _store.WriteAtomicAsync("memory/profile.md",
            "---\npia: managed\nid: 00000000-0000-0000-0000-000000000001\ntype: personal_profile\ntitle: Profile\nupdated: 2026-06-01T12:00:00Z\n---\n## Coffee\n- likes: espresso\n\n## Commute\n- mode: bike\n");
        await _store.WriteAtomicAsync("memory/contacts.md",
            "---\npia: managed\nid: 00000000-0000-0000-0000-000000000002\ntype: contact_list\ntitle: Contacts\nupdated: 2026-06-02T12:00:00Z\n---\n## John Smith\n- email: john@x\n");
        await _store.WriteAtomicAsync("memory/notes/foo.md",
            "---\npia: managed\nid: 00000000-0000-0000-0000-000000000003\ntype: note\ntitle: Foo Note\nupdated: 2026-06-03T12:00:00Z\n---\n- attendees: 8\n\nSome prose.\n");
        // Non-records that must be excluded:
        await _store.WriteAtomicAsync("memory/AGENTS.md",
            "---\npia: managed\nid: 00000000-0000-0000-0000-000000000004\ntype: note\n---\n## Ownership\n- rule: memory only\n");
        await _store.WriteAtomicAsync("memory/index.md",
            "---\npia: managed\nid: 00000000-0000-0000-0000-000000000005\ntype: note\n---\n## Catalog\n- entry: x\n");
        await _store.WriteAtomicAsync("sources/raw.md",
            "---\nfoo: bar\n---\n## Raw\n- data: 1\n");
    }

    [Fact]
    public async Task ListMemoriesAsync_returns_one_item_per_section_and_per_freeform_file()
    {
        await SeedVaultAsync();
        var service = BuildService();

        var items = await service.ListMemoriesAsync();

        // Exactly 4 records: profile{Coffee, Commute} + contacts{John Smith} + note{Foo Note}.
        Assert.Equal(4, items.Count);

        var coffee = Assert.Single(items, i => i.Title == "Coffee");
        Assert.Equal("personal_profile", coffee.Type);
        Assert.Equal("memory/profile.md#Coffee", coffee.Reference);
        Assert.Contains("espresso", coffee.Body);

        Assert.Single(items, i => i.Title == "Commute" && i.Type == "personal_profile");

        var john = Assert.Single(items, i => i.Title == "John Smith");
        Assert.Equal("contact_list", john.Type);
        Assert.Equal("memory/contacts.md#John Smith", john.Reference);

        // Freeform note: title from frontmatter, body from preamble, REFERENCE is the bare path.
        var foo = Assert.Single(items, i => i.Title == "Foo Note");
        Assert.Equal("note", foo.Type);
        Assert.Equal("memory/notes/foo.md", foo.Reference);
        Assert.Contains("attendees: 8", foo.Body);
    }

    [Fact]
    public async Task ListMemoriesAsync_excludes_scaffolding_and_sources()
    {
        await SeedVaultAsync();
        var service = BuildService();

        var items = await service.ListMemoriesAsync();

        Assert.DoesNotContain(items, i => i.FilePath.Contains("AGENTS"));
        Assert.DoesNotContain(items, i => i.FilePath.Contains("index.md"));
        Assert.DoesNotContain(items, i => i.FilePath.Contains("sources/"));
        Assert.DoesNotContain(items, i => i.Title is "Ownership" or "Catalog" or "Raw");
    }

    [Fact]
    public async Task GetVaultMemoryStatsAsync_counts_records_and_sums_record_bytes()
    {
        await SeedVaultAsync();
        var service = BuildService();

        var (count, bytes) = await service.GetVaultMemoryStatsAsync();

        Assert.Equal(4, count);
        Assert.True(bytes > 0);
    }

    // Distinct text -> well-spread near-orthogonal unit vectors; identical text round-trips identically.
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
