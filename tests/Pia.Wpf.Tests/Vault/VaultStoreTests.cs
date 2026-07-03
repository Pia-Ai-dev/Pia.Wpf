using System.IO;
using System.Text;
using Pia.Infrastructure.Vault;
using Xunit;

namespace Pia.Tests.Vault;

public class VaultStoreTests : IDisposable
{
    private readonly string _root;
    private readonly MarkdownVaultParser _parser = new();

    public VaultStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"pia-test-{Guid.NewGuid()}");
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

    [Fact]
    public void Root_follows_provider_after_SetRoot()
    {
        var provider = new VaultPathProvider(@"C:\a");
        var store = new VaultStore(provider, new MarkdownVaultParser());
        Assert.Equal(@"C:\a", store.Root);
        provider.SetRoot(@"C:\b");
        Assert.Equal(@"C:\b", store.Root);
    }

    [Fact]
    public async Task WriteAtomic_succeeds_with_injected_gate()
    {
        var store = new VaultStore(new VaultPathProvider(_root), _parser, new VaultWriteGate());
        await store.WriteAtomicAsync("memory/a.md", "---\nid: x\n---\nhi");
        Assert.True(File.Exists(Path.Combine(_root, "memory", "a.md")));
    }

    // ---- (a) WriteAtomicAsync -> ReadAsync round-trips exact bytes ----

    [Fact]
    public async Task WriteAtomic_then_Read_round_trips_exact_bytes()
    {
        var content =
            "---\nid: 11111111-1111-1111-1111-111111111111\ntype: note\ntitle: N\nschemaVersion: 1\n---\nHello body\n";
        var store = NewStore();

        await store.WriteAtomicAsync("memory/notes/x.md", content);
        var doc = await store.ReadAsync("memory/notes/x.md");

        Assert.NotNull(doc);
        Assert.Equal(content, doc!.RawText);
    }

    [Fact]
    public async Task Read_returns_null_for_missing_file()
    {
        var store = NewStore();
        Assert.Null(await store.ReadAsync("memory/does-not-exist.md"));
    }

    // ---- (b) SpliceSectionAsync changes ONLY the target section body ----

    private const string ContactsFixture =
        "---\n" +
        "pia: managed\n" +
        "id: 6f9c0b3e-7c1a-4f2e-9a8b-000000000001\n" +
        "type: contact_list\n" +
        "title: Contacts\n" +
        "schemaVersion: 1\n" +
        "---\n" +
        "## John Smith\n" +
        "- email: john@example.com\n" +
        "- phone: 555-0100\n" +
        "\n" +
        "## Alice Jones\n" +
        "- email: alice@example.com\n" +
        "- phone: 555-0200\n";

    private const string FrontmatterVerbatim =
        "---\n" +
        "pia: managed\n" +
        "id: 6f9c0b3e-7c1a-4f2e-9a8b-000000000001\n" +
        "type: contact_list\n" +
        "title: Contacts\n" +
        "schemaVersion: 1\n" +
        "---\n";

    private const string AliceSectionVerbatim =
        "## Alice Jones\n" +
        "- email: alice@example.com\n" +
        "- phone: 555-0200\n";

    [Fact]
    public async Task Splice_changes_only_target_section_body()
    {
        var store = NewStore();
        await store.WriteAtomicAsync("memory/contacts.md", ContactsFixture);

        var newJohnBody =
            "- email: john.smith@acme.com\n" +
            "- phone: 555-9999\n" +
            "- company: Acme\n" +
            "\n";
        await store.SpliceSectionAsync("memory/contacts.md", "john-smith", newJohnBody);

        var doc = await store.ReadAsync("memory/contacts.md");
        Assert.NotNull(doc);
        var raw = doc!.RawText;

        // Frontmatter byte-identical.
        Assert.StartsWith(FrontmatterVerbatim, raw);

        // Alice section byte-identical (unchanged sibling).
        Assert.Contains(AliceSectionVerbatim, raw);

        // John's new body present, old body gone.
        Assert.Contains("- email: john.smith@acme.com", raw);
        Assert.Contains("- company: Acme", raw);
        Assert.DoesNotContain("john@example.com", raw);

        // John's heading line itself is preserved (only the body was spliced).
        Assert.Contains("## John Smith\n", raw);

        // Alice's data is untouched.
        Assert.Contains("alice@example.com", raw);
    }

    // ---- (c) Atomic write: no leftover .tmp on success; original intact on throw ----

    [Fact]
    public async Task Successful_write_leaves_no_tmp_file()
    {
        var store = NewStore();
        await store.WriteAtomicAsync("memory/notes/y.md", "---\nid: 1\n---\nbody\n");

        var leftover = Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories);
        Assert.Empty(leftover);
    }

    [Fact]
    public async Task Failed_write_leaves_original_intact_and_no_tmp()
    {
        var original =
            "---\nid: 11111111-1111-1111-1111-111111111111\ntype: note\nschemaVersion: 1\n---\noriginal body\n";
        // Seed via a normal store first.
        var seed = NewStore();
        await seed.WriteAtomicAsync("memory/notes/z.md", original);

        var throwing = new ThrowingVaultStore(_root, _parser);
        await Assert.ThrowsAsync<IOException>(
            () => throwing.WriteAtomicAsync("memory/notes/z.md", "new content that must not land\n"));

        // Original is byte-for-byte intact.
        var doc = await seed.ReadAsync("memory/notes/z.md");
        Assert.NotNull(doc);
        Assert.Equal(original, doc!.RawText);

        // No leftover .tmp.
        var leftover = Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories);
        Assert.Empty(leftover);
    }

    [Fact]
    public async Task Failed_write_after_tmp_created_cleans_up_the_real_tmp()
    {
        // Exercises the genuine partial-write path: the .tmp IS created, then a later step throws,
        // so the catch-block TryDelete(tmpPath) must remove a real file (not a no-op on a missing one).
        var original =
            "---\nid: 11111111-1111-1111-1111-111111111111\ntype: note\nschemaVersion: 1\n---\noriginal body\n";
        var seed = NewStore();
        await seed.WriteAtomicAsync("memory/notes/z.md", original);

        var throwing = new ThrowAfterTmpVaultStore(_root, _parser);
        await Assert.ThrowsAsync<IOException>(
            () => throwing.WriteAtomicAsync("memory/notes/z.md", "new content that must not land\n"));

        // The seam confirms a real .tmp existed at the moment of failure...
        Assert.True(throwing.TmpExistedAtThrow);

        // ...and the catch-block cleanup removed it.
        var leftover = Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories);
        Assert.Empty(leftover);

        // Original is byte-for-byte intact (the move never happened).
        var doc = await seed.ReadAsync("memory/notes/z.md");
        Assert.NotNull(doc);
        Assert.Equal(original, doc!.RawText);
    }

    // ---- EnumerateAsync / DeleteAsync ----

    [Fact]
    public async Task Enumerate_returns_sorted_relative_paths()
    {
        var store = NewStore();
        await store.WriteAtomicAsync("memory/notes/b.md", "b\n");
        await store.WriteAtomicAsync("memory/notes/a.md", "a\n");

        var paths = await store.EnumerateAsync(Path.Combine("memory", "notes", "*.md"));

        Assert.Equal(2, paths.Count);
        Assert.Equal(
            Path.Combine("memory", "notes", "a.md"),
            paths[0]);
        Assert.Equal(
            Path.Combine("memory", "notes", "b.md"),
            paths[1]);
    }

    [Fact]
    public async Task Delete_removes_the_file()
    {
        var store = NewStore();
        await store.WriteAtomicAsync("memory/notes/d.md", "d\n");
        await store.DeleteAsync("memory/notes/d.md");

        Assert.Null(await store.ReadAsync("memory/notes/d.md"));
    }

    /// <summary>Test seam: forces the underlying write to throw mid-operation.</summary>
    private sealed class ThrowingVaultStore : VaultStore
    {
        public ThrowingVaultStore(string root, MarkdownVaultParser parser)
            : base(root, parser)
        {
        }

        protected override Task WriteFileAsync(string fullPath, string content)
            => throw new IOException("simulated write failure");
    }

    /// <summary>
    /// Test seam: writes the real tmp file (via the base seam) and only THEN throws, so the
    /// WriteAtomicAsync catch-block cleanup is exercised against an actually-created .tmp.
    /// </summary>
    private sealed class ThrowAfterTmpVaultStore : VaultStore
    {
        public ThrowAfterTmpVaultStore(string root, MarkdownVaultParser parser)
            : base(root, parser)
        {
        }

        public bool TmpExistedAtThrow { get; private set; }

        protected override async Task WriteFileAsync(string fullPath, string content)
        {
            await base.WriteFileAsync(fullPath, content);
            TmpExistedAtThrow = File.Exists(fullPath);
            throw new IOException("simulated failure after tmp was written");
        }
    }
}
