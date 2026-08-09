using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure.Vault;
using Pia.Services.Wiki;
using Xunit;

namespace Pia.Tests.Wiki;

/// <summary>Byte-format tests for index.md and log.md against a real <see cref="VaultStore"/> over a temp vault.</summary>
public class VaultWikiTests : IDisposable
{
    private readonly string _root;
    private readonly MarkdownVaultParser _parser = new();

    public VaultWikiTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"pia-wiki-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private VaultStore NewStore() => new(_root, _parser);

    private VaultIndexService NewIndex(VaultStore store) =>
        new(store, NullLogger<VaultIndexService>.Instance);

    private VaultLogService NewLog(VaultStore store) =>
        new(store, NullLogger<VaultLogService>.Instance);

    private static string BodyAfterFrontmatter(string raw)
    {
        // Drop the leading frontmatter block (--- ... ---\n) so timestamp churn doesn't break equality.
        var first = raw.IndexOf("---\n", StringComparison.Ordinal);
        var close = raw.IndexOf("\n---\n", first + 3, StringComparison.Ordinal);
        return close < 0 ? raw : raw[(close + 5)..];
    }

    // ---- (0) rewriting index.md preserves unknown (user-added) scalar frontmatter keys ----

    [Fact]
    public async Task Upsert_preserves_unknown_scalar_frontmatter_keys()
    {
        var store = NewStore();
        // Seed an index.md that already carries a user/Obsidian-added scalar key.
        var seeded =
            "---\n" +
            "pia: managed\n" +
            "id: 11111111-1111-1111-1111-111111111111\n" +
            "type: note\n" +
            "title: Index\n" +
            "created: 2026-06-07T09:00:00Z\n" +
            "updated: 2026-06-07T09:00:00Z\n" +
            "schemaVersion: 1\n" +
            "cssclass: dashboard\n" +
            "---\n" +
            "# Index\n";
        await store.WriteAtomicAsync("memory/index.md", seeded);

        var index = NewIndex(store);
        await index.UpsertEntryAsync("memory/topics/acme.md", "Acme Corp.");

        var raw = (await store.ReadAsync("memory/index.md"))!.RawText;
        Assert.Contains("cssclass: dashboard", raw);            // unknown key survived the rewrite
        Assert.Contains("id: 11111111-1111-1111-1111-111111111111", raw); // stable id preserved
        Assert.Contains("- [[topics/acme]] — Acme Corp.", raw); // and the entry was added
    }

    // ---- (1) grouping, ordering, exact wikilink format, upsert-replaces, remove, housekeeping ----

    [Fact]
    public async Task Upsert_groups_by_type_sorts_ascending_and_uses_exact_wikilink_lines()
    {
        var store = NewStore();
        var index = NewIndex(store);

        await index.UpsertEntryAsync("memory/topics/john-smith.md", "Primary contact at Acme.");
        await index.UpsertEntryAsync("memory/topics/acme.md", "Acme Corp: customer since 2024.");
        await index.UpsertEntryAsync("memory/notes/q2.md", "Q2 offsite notes.");

        var doc = await store.ReadAsync("memory/index.md");
        Assert.NotNull(doc);
        var body = BodyAfterFrontmatter(doc!.RawText);

        var expected =
            "# Index\n" +
            "\n" +
            "## Notes\n" +
            "- [[notes/q2]] — Q2 offsite notes.\n" +
            "\n" +
            "## Topics\n" +
            "### Other\n" +
            "- [[topics/acme]] — Acme Corp: customer since 2024.\n" +
            "- [[topics/john-smith]] — Primary contact at Acme.\n";

        Assert.Equal(expected, body);
    }

    [Fact]
    public async Task Reupserting_an_existing_path_updates_not_duplicates()
    {
        var store = NewStore();
        var index = NewIndex(store);

        await index.UpsertEntryAsync("memory/topics/acme.md", "Old summary.");
        await index.UpsertEntryAsync("memory/topics/acme.md", "New summary.");

        var doc = await store.ReadAsync("memory/index.md");
        var body = BodyAfterFrontmatter(doc!.RawText);

        Assert.Contains("- [[topics/acme]] — New summary.\n", body);
        Assert.DoesNotContain("Old summary.", body);
        // Exactly one acme line.
        var occurrences = body.Split("[[topics/acme]]").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public async Task Remove_drops_the_entry()
    {
        var store = NewStore();
        var index = NewIndex(store);

        await index.UpsertEntryAsync("memory/topics/acme.md", "Acme.");
        await index.UpsertEntryAsync("memory/topics/john-smith.md", "John.");
        await index.RemoveEntryAsync("memory/topics/acme.md");

        var doc = await store.ReadAsync("memory/index.md");
        var body = BodyAfterFrontmatter(doc!.RawText);

        Assert.DoesNotContain("topics/acme", body);
        Assert.Contains("- [[topics/john-smith]] — John.\n", body);
    }

    [Fact]
    public async Task Housekeeping_files_are_never_catalog_entries()
    {
        var store = NewStore();
        var index = NewIndex(store);

        await index.UpsertEntryAsync("memory/index.md", "self");
        await index.UpsertEntryAsync("memory/log.md", "journal");
        await index.UpsertEntryAsync("memory/AGENTS.md", "schema");
        await index.UpsertEntryAsync("memory/notes/keep.md", "real entry");

        var doc = await store.ReadAsync("memory/index.md");
        var body = BodyAfterFrontmatter(doc!.RawText);

        Assert.DoesNotContain("[[index]]", body);
        Assert.DoesNotContain("[[log]]", body);
        Assert.DoesNotContain("[[AGENTS]]", body);
        Assert.Contains("- [[notes/keep]] — real entry\n", body);
    }

    // ---- (2) determinism: different upsert orders, same final set -> byte-identical bodies ----

    [Fact]
    public async Task Two_upsert_orders_with_same_final_set_produce_byte_identical_bodies()
    {
        var storeA = new VaultStore(Path.Combine(_root, "a"), _parser);
        var storeB = new VaultStore(Path.Combine(_root, "b"), _parser);
        var indexA = new VaultIndexService(storeA, NullLogger<VaultIndexService>.Instance);
        var indexB = new VaultIndexService(storeB, NullLogger<VaultIndexService>.Instance);

        await indexA.UpsertEntryAsync("memory/topics/acme.md", "Acme.");
        await indexA.UpsertEntryAsync("memory/projects/apollo.md", "Apollo.");
        await indexA.UpsertEntryAsync("memory/notes/q2.md", "Q2.");
        await indexA.UpsertEntryAsync("memory/profile.md", "Me.");

        await indexB.UpsertEntryAsync("memory/notes/q2.md", "Q2.");
        await indexB.UpsertEntryAsync("memory/profile.md", "Me.");
        await indexB.UpsertEntryAsync("memory/topics/acme.md", "Acme.");
        await indexB.UpsertEntryAsync("memory/projects/apollo.md", "Apollo.");

        var docA = await storeA.ReadAsync("memory/index.md");
        var docB = await storeB.ReadAsync("memory/index.md");

        Assert.Equal(BodyAfterFrontmatter(docA!.RawText), BodyAfterFrontmatter(docB!.RawText));
    }

    // ---- (3) log: append-only, both lines in order, each grep-parseable ----

    [Fact]
    public async Task Log_appends_lines_in_order_and_is_append_only()
    {
        var store = NewStore();
        var log = NewLog(store);
        var date = new DateOnly(2026, 6, 7);

        await log.AppendAsync("ingest", "q2-report.pdf -> topics/acme", date);
        await log.AppendAsync("remember", "contacts#John Smith updated", date);

        var afterTwo = (await store.ReadAsync("memory/log.md"))!.RawText;

        Assert.Contains("## [2026-06-07] ingest | q2-report.pdf -> topics/acme\n", afterTwo);
        Assert.Contains("## [2026-06-07] remember | contacts#John Smith updated\n", afterTwo);

        // ingest precedes remember (in order).
        var iIngest = afterTwo.IndexOf("] ingest |", StringComparison.Ordinal);
        var iRemember = afterTwo.IndexOf("] remember |", StringComparison.Ordinal);
        Assert.True(iIngest >= 0 && iRemember > iIngest);

        // A third append leaves the first two byte-identical (append-only prefix).
        await log.AppendAsync("lint", "merged duplicate topics/acme-corp -> topics/acme", date);
        var afterThree = (await store.ReadAsync("memory/log.md"))!.RawText;

        Assert.StartsWith(afterTwo, afterThree);
        Assert.Contains("## [2026-06-07] lint | merged duplicate topics/acme-corp -> topics/acme\n", afterThree);
    }
}
