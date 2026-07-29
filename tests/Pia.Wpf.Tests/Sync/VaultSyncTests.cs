using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Infrastructure.Sync;
using Pia.Infrastructure.Vault;
using Pia.Services;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Services.Sync;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Sync;

/// <summary>
/// Compile-driven TDD for Task 5.3: the per-file last-synced BASE snapshot store
/// (<see cref="SyncBaseStore"/>), the {path,content} sync envelope mapping on
/// <see cref="SyncMapper"/>, and the merge-on-pull reconciler (<see cref="VaultSyncService"/>).
/// Uses a real parser + real <see cref="SectionMergeEngine"/> against temp directories so the
/// merge oracle (spec §10.1) is exercised end-to-end.
/// </summary>
public sealed class VaultSyncTests : IDisposable
{
    private const string Id = "6f9c0b3e-7c1a-4f2e-9a8b-000000000001";
    private const string UserId = "user-123";

    private readonly string _vaultRoot;
    private readonly string _baseRoot;
    private readonly MarkdownVaultParser _parser = new();

    public VaultSyncTests()
    {
        _vaultRoot = Path.Combine(Path.GetTempPath(), "pia-vaultsync-vault-" + Guid.NewGuid().ToString("N"));
        _baseRoot = Path.Combine(Path.GetTempPath(), "pia-vaultsync-base-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_vaultRoot);
        Directory.CreateDirectory(_baseRoot);
    }

    public void Dispose()
    {
        TryDeleteDir(_vaultRoot);
        TryDeleteDir(_baseRoot);
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // best-effort temp cleanup
        }
    }

    private VaultStore Store() => new(_vaultRoot, _parser);

    private SyncBaseStore BaseStore() => new(_baseRoot);

    private VaultSyncService Service()
    {
        var merge = new SectionMergeEngine(_parser);
        return new VaultSyncService(
            merge, BaseStore(), Store(), _parser, NullLogger<VaultSyncService>.Instance);
    }

    /// <summary>Build a contact_list vault doc body with the shared id and the given updated stamp.</summary>
    private static string Doc(string updated, string body)
    {
        return
            "---\n" +
            "pia: managed\n" +
            $"id: {Id}\n" +
            "type: contact_list\n" +
            "title: Contacts\n" +
            "created: 2026-06-07T09:00:00Z\n" +
            $"updated: {updated}\n" +
            "schemaVersion: 1\n" +
            "---\n" +
            body;
    }

    // ---- (a) SyncBaseStore round-trip: write base for an id, read it back, delete ----

    [Fact]
    public async Task SyncBaseStore_WriteReadDelete_RoundTrips()
    {
        var store = BaseStore();
        var id = Guid.Parse(Id);
        const string content = "---\npia: managed\n---\nbody\n";

        Assert.Null(await store.ReadBaseAsync(id));

        await store.WriteBaseAsync(id, content);
        Assert.Equal(content, await store.ReadBaseAsync(id));

        await store.DeleteBaseAsync(id);
        Assert.Null(await store.ReadBaseAsync(id));
    }

    [Fact]
    public async Task SyncBaseStore_Write_IsBomLessUtf8()
    {
        var store = BaseStore();
        var id = Guid.Parse(Id);
        await store.WriteBaseAsync(id, "x");

        var path = Path.Combine(_baseRoot, id.ToString("D") + ".md");
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        // No UTF-8 BOM (EF BB BF) — RawText must round-trip byte-for-byte.
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal((byte)'x', bytes[0]);
    }

    // ---- (b) {path,content} envelope: E2EE-OFF carries path+content in plaintext fields ----

    [Fact]
    public void ToVaultSyncMemory_E2EEOff_CarriesPathAndContentPlaintext()
    {
        var dpapi = Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        var mapper = new SyncMapper(dpapi); // no E2EE -> plaintext path
        var id = Guid.Parse(Id);
        const string path = "memory/contacts.md";
        const string content = "---\npia: managed\n---\n## John\n";

        var sync = mapper.ToVaultSyncMemory(id, path, content, UserId);

        Assert.Equal(id, sync.Id);
        Assert.Equal(path, sync.Path);
        Assert.Equal(content, sync.Data);
        Assert.Null(sync.EncryptedPayload);
        Assert.Null(sync.WrappedDek);

        var (rtPath, rtContent) = mapper.FromVaultSyncMemory(sync);
        Assert.Equal(path, rtPath);
        Assert.Equal(content, rtContent);
    }

    // ---- (b') {path,content} envelope: E2EE-ON encrypts; plaintext Path stays null (C5) ----

    [Fact]
    public void ToVaultSyncMemory_E2EEOn_EncryptsPayload_PathStaysNull()
    {
        var e2ee = Substitute.For<IE2EEService>();
        e2ee.IsReady().Returns(true);
        e2ee.EncryptRecord(Arg.Any<object>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(("CIPHER", "WDEK"));

        var dpapi = Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        var mapper = new SyncMapper(dpapi, e2ee);
        var id = Guid.Parse(Id);
        const string path = "memory/contacts.md";
        const string content = "---\npia: managed\n---\n## John\n";

        var sync = mapper.ToVaultSyncMemory(id, path, content, UserId);

        Assert.Equal(id, sync.Id);
        Assert.Equal("CIPHER", sync.EncryptedPayload);
        Assert.Equal("WDEK", sync.WrappedDek);
        Assert.Null(sync.Path);    // C5: server never sees a plaintext path

        // NOT null: SyncMemory.Data is initialised to "{}" by the DTO itself, and the E2EE branch simply
        // never assigns it — as in all 11 E2EE branches in SyncMapper, none of which nulls Data. So "{}"
        // is the layer-wide shape and this was the only assertion claiming otherwise. C5 still holds: the
        // requirement is that no plaintext PATH or CONTENT reaches the server, and an empty JSON object
        // carries neither. Asserting the literal keeps the guarantee explicit — if a future change ever
        // let real content into Data under E2EE, this fails.
        Assert.Equal("{}", sync.Data);
        e2ee.Received().EncryptRecord(
            Arg.Any<object>(), UserId, "vault_file", id.ToString());
    }

    [Fact]
    public void FromVaultSyncMemory_E2EEOn_DecryptsPayload()
    {
        var e2ee = Substitute.For<IE2EEService>();
        e2ee.IsReady().Returns(true);
        var id = Guid.Parse(Id);
        const string path = "memory/contacts.md";
        const string content = "---\npia: managed\n---\n## John\n";
        e2ee.DecryptRecord<VaultSyncPayload>(
                "CIPHER", "WDEK", UserId, "vault_file", id.ToString())
            .Returns(new VaultSyncPayload(path, content));

        var dpapi = Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        var mapper = new SyncMapper(dpapi, e2ee);

        var sync = new SyncMemory { Id = id, EncryptedPayload = "CIPHER", WrappedDek = "WDEK" };
        var (rtPath, rtContent) = mapper.FromVaultSyncMemory(sync, UserId);

        Assert.Equal(path, rtPath);
        Assert.Equal(content, rtContent);
    }

    // ---- (b'') {path,content} envelope: an encrypted row given to a mapper that CANNOT decrypt
    //      (E2EE inactive) THROWS instead of materializing an empty (path, content). The sync layer
    //      catches this and skips the row. The plaintext round-trip still works unchanged. ----

    [Fact]
    public void FromVaultSyncMemory_EncryptedRow_E2EEInactive_Throws()
    {
        var dpapi = Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        var mapper = new SyncMapper(dpapi); // no E2EE -> cannot decrypt
        var id = Guid.Parse(Id);

        // Encrypted envelope: ciphertext present, plaintext Path/Data null (C5).
        var sync = new SyncMemory { Id = id, EncryptedPayload = "CIPHER", WrappedDek = "WDEK" };

        Assert.Throws<InvalidOperationException>(() => mapper.FromVaultSyncMemory(sync, UserId));
    }

    [Fact]
    public void FromVaultSyncMemory_PlaintextRow_RoundTrips()
    {
        var dpapi = Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        var mapper = new SyncMapper(dpapi); // no E2EE -> plaintext path
        var id = Guid.Parse(Id);
        const string path = "memory/contacts.md";
        const string content = "---\npia: managed\n---\n## John\n";

        var sync = new SyncMemory { Id = id, Path = path, Data = content };
        var (rtPath, rtContent) = mapper.FromVaultSyncMemory(sync);

        Assert.Equal(path, rtPath);
        Assert.Equal(content, rtContent);
    }

    // ---- (c) merge-on-pull: first pull (no base) writes remote verbatim ----

    [Fact]
    public async Task ReconcileOnPull_NoBase_WritesRemoteVerbatim()
    {
        var service = Service();
        const string path = "memory/contacts.md";
        var remote = Doc("2026-06-07T10:00:00Z", "## John\n- email: john@remote.com\n");

        var merged = await service.ReconcileOnPullAsync(Guid.Parse(Id), path, remote);

        Assert.Equal(remote, merged);
        // The file landed in the vault verbatim...
        Assert.Equal(remote, await File.ReadAllTextAsync(
            Path.Combine(_vaultRoot, "memory", "contacts.md"), TestContext.Current.CancellationToken));
        // ...and the base advanced to remote.
        Assert.Equal(remote, await BaseStore().ReadBaseAsync(Guid.Parse(Id)));
    }

    // ---- (c') merge-on-pull: base + disjoint local/remote edits -> BOTH edits survive ----

    [Fact]
    public async Task ReconcileOnPull_WithBase_MergesBothEdits()
    {
        const string path = "memory/contacts.md";
        var id = Guid.Parse(Id);

        var @base = Doc("2026-06-07T09:00:00Z",
            "## John\n- email: john@base.com\n\n## Alice\n- email: alice@base.com\n");
        var local = Doc("2026-06-07T10:00:00Z",
            "## John\n- email: john@LOCAL.com\n\n## Alice\n- email: alice@base.com\n");
        var remote = Doc("2026-06-07T09:30:00Z",
            "## John\n- email: john@base.com\n\n## Alice\n- email: alice@REMOTE.com\n");

        // Seed base snapshot + a local edit on disk.
        await BaseStore().WriteBaseAsync(id, @base);
        await Store().WriteAtomicAsync(path, local);

        var merged = await Service().ReconcileOnPullAsync(id, path, remote);

        // Disjoint edits auto-merge: John (local) AND Alice (remote) both survive.
        Assert.Contains("john@LOCAL.com", merged);
        Assert.Contains("alice@REMOTE.com", merged);
        Assert.DoesNotContain("<<<<<<< local", merged);

        // The merged text is what landed on disk.
        Assert.Equal(merged, await File.ReadAllTextAsync(
            Path.Combine(_vaultRoot, "memory", "contacts.md"), TestContext.Current.CancellationToken));
    }
}
