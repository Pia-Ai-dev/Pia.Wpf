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
using Pia.Services.Migration;
using Xunit;

namespace Pia.Tests.Migration;

public class VaultMigrationRunnerTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _vaultRoot;
    private readonly SqliteContext _ctx;
    private readonly MarkdownVaultParser _parser = new();
    private readonly VaultStore _store;
    private readonly StubEmbeddingService _embeddings = new();
    private readonly SyncDeleteTrackerService _deleteTracker;
    private readonly SectionUpsertService _upsert;
    private readonly MemoryService _memory;
    private readonly VaultIndexer _indexer;
    private readonly FakeSettingsService _settings;
    private readonly MemoryJsonRenderer _renderer = new();

    public VaultMigrationRunnerTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"pia-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tmpDir);
        _vaultRoot = Path.Combine(_tmpDir, "vault");
        Directory.CreateDirectory(_vaultRoot);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
        _store = new VaultStore(_vaultRoot, _parser);
        _deleteTracker = new SyncDeleteTrackerService(_tmpDir, NullLogger<SyncDeleteTrackerService>.Instance);
        _upsert = new SectionUpsertService(_embeddings);
        _memory = new MemoryService(_ctx, NullLogger<MemoryService>.Instance, _embeddings, _deleteTracker, _store, _upsert);
        _indexer = new VaultIndexer(_ctx, _store, _parser, _embeddings, NullLogger<VaultIndexer>.Instance);
        _settings = new FakeSettingsService();
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

    private VaultMigrationRunner BuildRunner()
        => new(_memory, _store, _indexer, _settings, _renderer, NullLogger<VaultMigrationRunner>.Instance);

    // --- DEDUP-ON-MIGRATE ---------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_dedups_identical_contact_across_two_rows_into_one_section()
    {
        // Two contact_list rows whose arrays BOTH contain an identical "John Smith".
        await _memory.CreateObjectAsync(
            "contact_list", "Work contacts",
            """[{"name":"John Smith","email":"john@work"}]""");
        await _memory.CreateObjectAsync(
            "contact_list", "Personal contacts",
            """[{"name":"John Smith","phone":"555"}]""");

        var report = await BuildRunner().RunAsync();

        Assert.False(report.Skipped);

        var doc = await _store.ReadAsync("memory/contacts.md");
        Assert.NotNull(doc);

        // DEDUP-ON-WRITE: exactly ONE "## John Smith" section (the 2nd entry resolved Edit on lexical 1.0).
        var johns = doc!.Sections.Where(s => s.Heading == "John Smith").ToList();
        Assert.Single(johns);
    }

    [Fact]
    public async Task RunAsync_writes_ambiguous_record_instead_of_dropping_it()
    {
        // The SECOND contact "Johnny" resolves AMBIGUOUS against the first "John Smith" section:
        // lexical JaroWinkler("Johnny","John Smith") ~0.69 sits in [0.60, 0.85), and the FNV embedding
        // stub maps the two distinct inputs to near-orthogonal vectors (cosine ~0), so the score stays
        // below the 0.85 Edit cut. With createOnAmbiguous the record must still LAND (as a Create) —
        // it is never dropped (this is the same Ambiguous recipe proven in SectionUpsertServiceTests).
        await _memory.CreateObjectAsync(
            "contact_list", "Work", """[{"name":"John Smith","email":"john@x"}]""");
        await _memory.CreateObjectAsync(
            "contact_list", "Personal", """[{"name":"Johnny","city":"NYC"}]""");

        var report = await BuildRunner().RunAsync();

        Assert.False(report.Skipped);
        // No record may be dropped — the ambiguous one was force-created.
        Assert.Equal(0, report.Dropped);
        // Both records actually landed (one Create for John Smith, one Create for the ambiguous Johnny).
        Assert.Equal(2, report.RecordsWritten);

        var doc = await _store.ReadAsync("memory/contacts.md");
        Assert.NotNull(doc);
        // The ambiguous record wrote a NEW section rather than merging or being dropped.
        Assert.Contains(doc!.Sections, s => s.Heading == "John Smith");
        Assert.Contains(doc.Sections, s => s.Heading == "Johnny");
    }

    [Fact]
    public async Task RunAsync_edit_band_merge_preserves_nested_leaves_losslessly()
    {
        // Two contact_list rows BOTH containing "John Smith". The first is a flat bullet; the SECOND
        // carries a NESTED object (address {city, zip}) AND an ARRAY (tags [vip, client]). The second
        // resolves Edit (exact-heading lexical 1.0) and merges into the one section. The nested/array
        // leaves arrive as NON-top-level-bullet new-body lines, so the Edit-band MergeBullets must
        // APPEND them losslessly (this assertion would FAIL before the MergeBullets fix).
        await _memory.CreateObjectAsync(
            "contact_list", "Work", """[{"name":"John Smith","email":"john@x"}]""");
        await _memory.CreateObjectAsync(
            "contact_list", "Personal",
            """[{"name":"John Smith","address":{"city":"NYC","zip":"10001"},"tags":["vip","client"]}]""");

        await BuildRunner().RunAsync();

        var doc = await _store.ReadAsync("memory/contacts.md");
        Assert.NotNull(doc);

        // EXACTLY one "## John Smith" section (the 2nd entry merged via the Edit band, not a duplicate).
        var johns = doc!.Sections.Where(s => s.Heading == "John Smith").ToList();
        Assert.Single(johns);

        // All nested leaves from the second record are preserved in that one section's body — nothing
        // was dropped by the merge.
        var body = johns[0].Body;
        Assert.Contains("city", body);
        Assert.Contains("NYC", body);
        Assert.Contains("zip", body);
        Assert.Contains("10001", body);
        Assert.Contains("vip", body);
        Assert.Contains("client", body);
    }

    // --- ARCHIVE ------------------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_archives_every_original_row_under_dot_archive()
    {
        var a = await _memory.CreateObjectAsync("note", "Alpha", """{"text":"a"}""");
        var b = await _memory.CreateObjectAsync("note", "Beta", """{"text":"b"}""");

        await BuildRunner().RunAsync();

        Assert.NotNull(await _store.ReadAsync($"memory/.archive/{a.Id}.json"));
        Assert.NotNull(await _store.ReadAsync($"memory/.archive/{b.Id}.json"));
    }

    // --- MARKER + IDEMPOTENT ------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_sets_marker_and_second_run_is_idempotent()
    {
        await _memory.CreateObjectAsync(
            "contact_list", "Contacts",
            """[{"name":"Jane Doe","email":"jane@x"}]""");

        var first = await BuildRunner().RunAsync();
        Assert.False(first.Skipped);

        var settings = await _settings.GetSettingsAsync();
        Assert.Equal(1, settings.VaultVersion);

        var contactsPath = Path.Combine(_vaultRoot, "memory", "contacts.md");
        var bytesAfterFirst = await File.ReadAllTextAsync(contactsPath, TestContext.Current.CancellationToken);

        // A second run must skip and leave contacts.md byte-identical.
        var second = await BuildRunner().RunAsync();
        Assert.True(second.Skipped);

        var bytesAfterSecond = await File.ReadAllTextAsync(contactsPath, TestContext.Current.CancellationToken);
        Assert.Equal(bytesAfterFirst, bytesAfterSecond);
    }

    // --- LOSSLESS irregular row ---------------------------------------------------------------

    [Fact]
    public async Task RunAsync_preserves_irregular_note_json_verbatim()
    {
        // Irregular JSON Data (not the structured bullet shape) must survive as a fenced json block.
        const string irregular = """{"weird":["nested",{"k":1}],"raw":"keep me"}""";
        await _memory.CreateObjectAsync("note", "Odd Note", irregular);

        await BuildRunner().RunAsync();

        var doc = await _store.ReadAsync("memory/notes/odd-note.md");
        Assert.NotNull(doc);
        // Nothing dropped: the original payload's distinctive content is present in the body.
        Assert.Contains("keep me", doc!.RawText);
    }

    // --- CROSS-DEVICE GUARD -------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_skips_when_vault_already_populated()
    {
        // Simulate a device that pulled an already-migrated vault: memory/ has a file, VaultVersion still 0.
        await _store.WriteAtomicAsync("memory/profile.md", "---\npia: managed\nid: x\ntype: personal_profile\n---\n## A\n- k: v\n");
        await _memory.CreateObjectAsync("note", "Should not migrate", """{"text":"x"}""");

        var settings = await _settings.GetSettingsAsync();
        Assert.Equal(0, settings.VaultVersion);

        var report = await BuildRunner().RunAsync();

        Assert.True(report.Skipped);
        // The legacy row was NOT migrated (no notes file created).
        Assert.Null(await _store.ReadAsync("memory/notes/should-not-migrate.md"));
    }

    // --- helpers ------------------------------------------------------------------------------

    // In-memory ISettingsService backed by a single AppSettings instance (only the methods the runner
    // touches are meaningful).
    private sealed class FakeSettingsService : ISettingsService
    {
        private readonly AppSettings _settings = new();

        public event EventHandler<AppSettings>? SettingsChanged;

        public Task<AppSettings> GetSettingsAsync() => Task.FromResult(_settings);

        public Task SaveSettingsAsync(AppSettings settings)
        {
            // The runner mutates the same instance and saves it; mirror non-trivial fields.
            _settings.VaultVersion = settings.VaultVersion;
            SettingsChanged?.Invoke(this, _settings);
            return Task.CompletedTask;
        }

        public Task SaveDraftAsync(string? draftText) => Task.CompletedTask;

        public Task<string?> GetDraftAsync() => Task.FromResult<string?>(null);
    }

    // Distinct text -> well-spread near-orthogonal unit vectors so unrelated subjects do NOT collide;
    // identical text round-trips to an identical vector (so identical contacts merge).
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
