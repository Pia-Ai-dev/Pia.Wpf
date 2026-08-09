using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Infrastructure.Vault;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Migration;
using Pia.Services.Wiki;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Migration;

/// <summary>
/// Startup-ordering regression tests for the JSON→MD migration. Unlike <see cref="VaultMigrationRunnerTests"/>
/// (which builds the runner against an empty vault), these wire the REAL Bootstrapper sequence — scaffold
/// (<see cref="VaultSchemaService.EnsureScaffoldingAsync"/>) THEN migrate — so they catch the GUARD 2 bug
/// where the scaffolded <c>memory/AGENTS.md</c> made an unpopulated vault look populated and the legacy
/// JSON never migrated.
/// </summary>
public class VaultMigrationStartupTests : IDisposable
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
    private readonly VaultSchemaService _schema;

    public VaultMigrationStartupTests()
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
        _schema = new VaultSchemaService(_store, new VaultPathProvider(_vaultRoot), NullLogger<VaultSchemaService>.Instance);
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

    // The real Bootstrapper order: scaffold writes memory/AGENTS.md, THEN migration runs. The scaffolded
    // AGENTS.md must NOT count as a populated vault, so the legacy row migrates and VaultVersion advances.
    [Fact]
    public async Task Scaffold_then_migrate_populates_the_vault_and_advances_version()
    {
        await _memory.CreateObjectAsync("personal_profile", "Coffee", """{"likes":"espresso"}""");

        await _schema.EnsureScaffoldingAsync();
        // Sanity: the scaffold really did write AGENTS.md (the trigger for the original bug).
        Assert.NotNull(await _store.ReadAsync("memory/AGENTS.md"));

        var report = await BuildRunner().RunAsync();

        Assert.False(report.Skipped);
        Assert.True(report.RecordsWritten >= 1);

        Assert.NotNull(await _store.ReadAsync("memory/profile.md"));

        var settings = await _settings.GetSettingsAsync();
        Assert.Equal(1, settings.VaultVersion);
    }

    // GUARD 2 cross-device safety net: with VaultVersion still 0 (so this exercises GUARD 2, not GUARD 1),
    // a REAL record file already present means the vault was pulled already-migrated — skip and do not
    // re-migrate the legacy row.
    [Fact]
    public async Task Skips_when_a_real_record_file_is_already_present()
    {
        await _store.WriteAtomicAsync(
            "memory/contacts.md",
            "---\npia: managed\nid: 6f9c0b3e-7c1a-4f2e-9a8b-000000000001\ntype: contact_list\n---\n## Jane Doe\n- email: jane@x\n");
        await _schema.EnsureScaffoldingAsync();
        await _memory.CreateObjectAsync("note", "Should not migrate", """{"text":"x"}""");

        var before = await _settings.GetSettingsAsync();
        Assert.Equal(0, before.VaultVersion);

        var report = await BuildRunner().RunAsync();

        Assert.True(report.Skipped);
        // The legacy row was NOT migrated (no notes file created).
        Assert.Null(await _store.ReadAsync("memory/notes/should-not-migrate.md"));
    }

    // In-memory ISettingsService backed by a single AppSettings instance (only the methods the runner
    // touches are meaningful).
    private sealed class FakeSettingsService : ISettingsService
    {
        private readonly AppSettings _settings = new();

        public event EventHandler<AppSettings>? SettingsChanged;

        public Task<AppSettings> GetSettingsAsync() => Task.FromResult(_settings);

        public Task SaveSettingsAsync(AppSettings settings)
        {
            _settings.VaultVersion = settings.VaultVersion;
            SettingsChanged?.Invoke(this, _settings);
            return Task.CompletedTask;
        }

        public Task SaveDraftAsync(string? draftText) => Task.CompletedTask;

        public Task<string?> GetDraftAsync() => Task.FromResult<string?>(null);
    }

}
