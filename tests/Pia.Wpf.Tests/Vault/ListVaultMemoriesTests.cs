using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Infrastructure.Vault;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Vault;

public class ListVaultMemoriesTests : IDisposable
{
    private const string ProfileMd =
        "---\npia: managed\nid: 00000000-0000-0000-0000-000000000001\ntype: personal_profile\ntitle: Profile\nupdated: 2026-06-01T12:00:00Z\n---\n## Coffee\n- likes: espresso\n\n## Commute\n- mode: bike\n";
    private const string ContactsMd =
        "---\npia: managed\nid: 00000000-0000-0000-0000-000000000002\ntype: contact_list\ntitle: Contacts\nupdated: 2026-06-02T12:00:00Z\n---\n## John Smith\n- email: john@x\n";
    private const string FooNoteMd =
        "---\npia: managed\nid: 00000000-0000-0000-0000-000000000003\ntype: note\ntitle: Foo Note\nupdated: 2026-06-03T12:00:00Z\n---\n- attendees: 8\n\nSome prose.\n";

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
        TempPath.Remove(_tmpDir);
    }

    private MemoryService BuildService()
        => new(_ctx, NullLogger<MemoryService>.Instance, _embeddings, _deleteTracker, _store, _upsert);

    private async Task SeedVaultAsync()
    {
        await _store.WriteAtomicAsync("memory/profile.md", ProfileMd);
        await _store.WriteAtomicAsync("memory/contacts.md", ContactsMd);
        await _store.WriteAtomicAsync("memory/notes/foo.md", FooNoteMd);
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

        var items = (await service.ListMemoriesAsync()).Items;

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

        var items = (await service.ListMemoriesAsync()).Items;

        Assert.DoesNotContain(items, i => i.FilePath.Contains("AGENTS"));
        Assert.DoesNotContain(items, i => i.FilePath.Contains("index.md"));
        Assert.DoesNotContain(items, i => i.FilePath.Contains("sources/"));
        Assert.DoesNotContain(items, i => i.Title is "Ownership" or "Catalog" or "Raw");
    }

    [Fact]
    public async Task ListMemoriesAsync_parses_document_updated_timestamp()
    {
        await SeedVaultAsync();
        var service = BuildService();

        var items = (await service.ListMemoriesAsync()).Items;

        var coffee = Assert.Single(items, i => i.Title == "Coffee");
        Assert.Equal(new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc), coffee.Updated);
    }

    [Fact]
    public async Task ListMemoriesAsync_sums_only_record_file_bytes()
    {
        await SeedVaultAsync();
        var service = BuildService();

        var snapshot = await service.ListMemoriesAsync();

        // Exactly the three record files — never AGENTS.md / index.md / sources/raw.md.
        var expected = Encoding.UTF8.GetByteCount(ProfileMd)
            + Encoding.UTF8.GetByteCount(ContactsMd)
            + Encoding.UTF8.GetByteCount(FooNoteMd);
        Assert.Equal(expected, snapshot.Bytes);
        Assert.Equal(4, snapshot.Items.Count);
    }

    [Fact]
    public async Task ListMemoriesAsync_infers_type_from_path_when_frontmatter_type_absent()
    {
        // Type-less frontmatter (hand-authored or a foreign client) -> the type is inferred from the path.
        await _store.WriteAtomicAsync("memory/preferences.md",
            "---\npia: managed\nid: 00000000-0000-0000-0000-0000000000a1\n---\n## Tone\n- style: terse\n");
        await _store.WriteAtomicAsync("memory/projects/acme.md",
            "---\npia: managed\nid: 00000000-0000-0000-0000-0000000000a2\ntitle: Acme\n---\n- status: active\n");
        await _store.WriteAtomicAsync("memory/topics/widgets.md",
            "---\npia: managed\nid: 00000000-0000-0000-0000-0000000000a3\ntitle: Widgets\n---\n- kind: gadget\n");
        var service = BuildService();

        var items = (await service.ListMemoriesAsync()).Items;

        Assert.Equal("preference", Assert.Single(items, i => i.Title == "Tone").Type);
        Assert.Equal("project", Assert.Single(items, i => i.Title == "Acme").Type);
        Assert.Equal("topic", Assert.Single(items, i => i.Title == "Widgets").Type);
    }

}
