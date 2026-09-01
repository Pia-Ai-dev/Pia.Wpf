using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Infrastructure.Vault;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Vault;

public class MemoryWriteTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _vaultRoot;
    private readonly SqliteContext _ctx;
    private readonly MarkdownVaultParser _parser = new();
    private readonly VaultStore _store;
    private readonly StubEmbeddingService _embeddings = new();
    private readonly SyncDeleteTrackerService _deleteTracker;
    private readonly SectionUpsertService _upsert;

    public MemoryWriteTests()
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

    [Fact]
    public async Task Remember_contact_on_empty_vault_creates_file_with_valid_frontmatter()
    {
        var service = BuildService();

        var outcome = await service.RememberAsync("contact_list", "John Smith", "- email: john@x");

        Assert.Equal(UpsertBand.Create, outcome.Band);
        Assert.Equal("memory/contacts.md#John Smith", outcome.Reference);
        Assert.Empty(outcome.Candidates);

        var doc = await _store.ReadAsync("memory/contacts.md");
        Assert.NotNull(doc);

        Assert.Equal("managed", doc!.Frontmatter["pia"]);
        Assert.Equal("contact_list", doc.Frontmatter["type"]);
        Assert.Equal("1", doc.Frontmatter["schemaVersion"]);

        var idRaw = doc.Frontmatter["id"];
        Assert.True(Guid.TryParse(idRaw, out _));
        Assert.Equal(idRaw, idRaw.ToLowerInvariant());

        // created/updated are present and ISO-8601 UTC second precision.
        Assert.EndsWith("Z", doc.Frontmatter["created"]);
        Assert.EndsWith("Z", doc.Frontmatter["updated"]);

        var section = Assert.Single(doc.Sections, s => s.Heading == "John Smith");
        Assert.Contains("john@x", section.Body);
    }

    [Fact]
    public async Task Remember_same_subject_again_edits_and_dedups_into_one_section()
    {
        var service = BuildService();

        await service.RememberAsync("contact_list", "John Smith", "- email: john@x");
        var outcome = await service.RememberAsync("contact_list", "John Smith", "- phone: 5");

        Assert.Equal(UpsertBand.Edit, outcome.Band);
        Assert.Equal("memory/contacts.md#John Smith", outcome.Reference);

        var doc = await _store.ReadAsync("memory/contacts.md");
        Assert.NotNull(doc);

        // DEDUP at the service level: still exactly ONE "## John Smith" section.
        var sections = doc!.Sections.Where(s => s.Heading == "John Smith").ToList();
        Assert.Single(sections);

        // Body carries both fields.
        Assert.Contains("john@x", sections[0].Body);
        Assert.Contains("phone: 5", sections[0].Body);
    }

    [Fact]
    public async Task Remember_personal_profile_creates_profile_md()
    {
        var service = BuildService();

        var outcome = await service.RememberAsync("personal_profile", "Coffee", "- likes: espresso");

        Assert.Equal(UpsertBand.Create, outcome.Band);

        var doc = await _store.ReadAsync("memory/profile.md");
        Assert.NotNull(doc);
        Assert.Equal("personal_profile", doc!.Frontmatter["type"]);
        Assert.Contains(doc.Sections, s => s.Heading == "Coffee");
    }

    [Fact]
    public async Task Forget_with_heading_removes_section()
    {
        var service = BuildService();

        await service.RememberAsync("contact_list", "John Smith", "- email: john@x");
        await service.RememberAsync("contact_list", "Jane Doe", "- email: jane@x");

        await service.ForgetAsync("memory/contacts.md#John Smith");

        var doc = await _store.ReadAsync("memory/contacts.md");
        Assert.NotNull(doc);
        Assert.DoesNotContain(doc!.Sections, s => s.Heading == "John Smith");
        // The sibling section survives.
        Assert.Contains(doc.Sections, s => s.Heading == "Jane Doe");
    }

    [Fact]
    public async Task Forget_without_heading_deletes_whole_file()
    {
        var service = BuildService();

        await service.RememberAsync("note", "Q2 Retro", "- attendees: 8");
        var notePath = "memory/notes/q2-retro.md";
        Assert.NotNull(await _store.ReadAsync(notePath));

        await service.ForgetAsync(notePath);

        Assert.Null(await _store.ReadAsync(notePath));
    }

    [Fact]
    public async Task UpdateSection_replaces_section_body_and_preserves_frontmatter_and_siblings()
    {
        var service = BuildService();

        await service.RememberAsync("contact_list", "John Smith", "- email: john@x");
        await service.RememberAsync("contact_list", "Jane Doe", "- email: jane@x");

        var before = await _store.ReadAsync("memory/contacts.md");
        var originalId = before!.Frontmatter["id"];

        await service.UpdateSectionAsync("memory/contacts.md#John Smith", "- email: new@x\n- phone: 555");

        var doc = await _store.ReadAsync("memory/contacts.md");
        Assert.NotNull(doc);

        // The edited section carries the new body (whole-body replace, no merge of the old email).
        var john = Assert.Single(doc!.Sections, s => s.Heading == "John Smith");
        Assert.Contains("new@x", john.Body);
        Assert.Contains("phone: 555", john.Body);
        Assert.DoesNotContain("john@x", john.Body);

        // The sibling section is untouched (byte-range splice).
        var jane = Assert.Single(doc.Sections, s => s.Heading == "Jane Doe");
        Assert.Contains("jane@x", jane.Body);

        // Frontmatter identity is preserved (only the body was spliced; updated may be bumped).
        Assert.Equal(originalId, doc.Frontmatter["id"]);
        Assert.Equal("contact_list", doc.Frontmatter["type"]);
        Assert.Equal("managed", doc.Frontmatter["pia"]);
    }

    [Fact]
    public async Task UpdateSection_on_bare_path_replaces_freeform_body_wholesale()
    {
        var service = BuildService();

        await service.RememberAsync("note", "Q2 Retro", "- attendees: 8");
        var before = await _store.ReadAsync("memory/notes/q2-retro.md");
        var originalId = before!.Frontmatter["id"];

        await service.UpdateSectionAsync("memory/notes/q2-retro.md", "Completely rewritten prose body.");

        var doc = await _store.ReadAsync("memory/notes/q2-retro.md");
        Assert.NotNull(doc);
        Assert.Contains("Completely rewritten prose body.", doc!.RawText);
        // Whole-body replace: the original bullets are gone.
        Assert.DoesNotContain("attendees: 8", doc.RawText);
        // Frontmatter survives.
        Assert.Equal(originalId, doc.Frontmatter["id"]);
        Assert.Equal("note", doc.Frontmatter["type"]);
    }

    [Fact]
    public async Task Remember_note_freeform_creates_single_file_then_edits_it()
    {
        var service = BuildService();

        var created = await service.RememberAsync("note", "Q2 Retro", "- attendees: 8");
        Assert.Equal(UpsertBand.Create, created.Band);
        Assert.Equal("memory/notes/q2-retro.md", created.Reference.Replace('\\', '/'));

        var edited = await service.RememberAsync("note", "Q2 Retro", "- duration: 90m");
        Assert.Equal(UpsertBand.Edit, edited.Band);

        var doc = await _store.ReadAsync("memory/notes/q2-retro.md");
        Assert.NotNull(doc);
        Assert.Equal("note", doc!.Frontmatter["type"]);
        Assert.Contains("attendees: 8", doc.RawText);
        Assert.Contains("duration: 90m", doc.RawText);
    }
}
