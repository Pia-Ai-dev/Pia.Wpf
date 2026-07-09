using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure.Vault;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.Wiki;
using Xunit;

namespace Pia.Tests.Wiki;

/// <summary>
/// Task 5 scheduler tests for <see cref="AutoIngestService"/>: a real temp vault + a real
/// <see cref="IngestStateStore"/> over history.db, with a RECORDING stub <see cref="IIngestService"/>
/// so the serial queue, hash gate, shrink diff, watcher and reconcile are exercised without LLM calls.
/// </summary>
public class AutoIngestServiceTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _vaultRoot;
    private readonly string _sourcesDir;
    private readonly IngestStateStore _state;
    private readonly RecordingIngestService _ingest = new();
    private readonly StubProviderService _providers = new();
    private readonly StubSettingsService _settings = new();
    private readonly VaultPathProvider _paths;

    public AutoIngestServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"pia-autoingest-test-{Guid.NewGuid()}");
        _vaultRoot = Path.Combine(_tmpDir, "vault");
        _sourcesDir = Path.Combine(_vaultRoot, "sources");
        Directory.CreateDirectory(_sourcesDir);
        _state = new IngestStateStore($"Data Source={Path.Combine(_tmpDir, "history.db")}");
        _paths = new VaultPathProvider(_vaultRoot);
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

    private AutoIngestService Build() => new(
        _ingest,
        _state,
        new VaultStore(_vaultRoot, new MarkdownVaultParser()),
        _providers,
        _settings,
        _paths,
        NullLogger<AutoIngestService>.Instance);

    /// <summary>Write a source file under sources/ and return its vault-relative ref.</summary>
    private string Seed(string name, string content)
    {
        File.WriteAllText(Path.Combine(_sourcesDir, name), content);
        return "sources/" + name;
    }

    [Fact]
    public async Task RunAsync_raises_IngestStarted_and_publishes_CurrentSourceRef()
    {
        var sourceRef = Seed("started.txt", "v1");
        using var svc = Build();

        string? startedRef = null;
        string? refWhileRunning = null;
        svc.IngestStarted += (_, r) =>
        {
            startedRef = r;
            refWhileRunning = svc.CurrentSourceRef; // authoritative while the compile runs
        };

        Assert.Null(svc.CurrentSourceRef); // idle before

        await svc.RunAsync(sourceRef, TestContext.Current.CancellationToken);

        Assert.Equal(sourceRef, startedRef);
        Assert.Equal(sourceRef, refWhileRunning);
        Assert.Null(svc.CurrentSourceRef); // cleared when done — no stuck "Ingesting…"
    }

    [Fact]
    public async Task RunAsync_ingests_and_records_state()
    {
        var sourceRef = Seed("a.txt", "v1");
        using var svc = Build();

        var result = await svc.RunAsync(sourceRef, TestContext.Current.CancellationToken);

        Assert.Equal(IngestOutcome.Success, result.Outcome);
        Assert.Equal([sourceRef], _ingest.IngestCalls);
        var state = await _state.GetAsync(sourceRef);
        Assert.NotNull(state);
        Assert.Equal(result.TouchedPages, state!.TouchedPages);
    }

    [Fact]
    public async Task Reconcile_skips_unchanged_and_reingests_changed()
    {
        var refA = Seed("a.txt", "v1");
        var refB = Seed("b.txt", "v1");
        using var svc = Build();
        await svc.ReconcileAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, _ingest.IngestCalls.Count);

        File.WriteAllText(Path.Combine(_sourcesDir, "a.txt"), "v2"); // change one
        await svc.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, _ingest.IngestCalls.Count); // only a.txt re-ran
        Assert.Equal(refA, _ingest.IngestCalls[^1]);
        _ = refB;
    }

    [Fact]
    public async Task Reconcile_removes_tracked_but_missing_sources_and_deletes_state()
    {
        var sourceRef = Seed("a.txt", "v1");
        using var svc = Build();
        await svc.ReconcileAsync(TestContext.Current.CancellationToken);

        File.Delete(Path.Combine(_sourcesDir, "a.txt"));
        await svc.ReconcileAsync(TestContext.Current.CancellationToken);

        var remove = Assert.Single(_ingest.RemoveCalls);
        Assert.Equal(sourceRef, remove.Source);
        Assert.Null(await _state.GetAsync(sourceRef)); // row deleted -> not re-enqueued next startup
    }

    [Fact]
    public async Task Reconcile_without_provider_records_nothing_and_calls_nothing()
    {
        Seed("a.txt", "v1");
        _providers.HasProvider = false;
        using var svc = Build();

        await svc.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_ingest.IngestCalls);
        Assert.Empty(await _state.ListAsync()); // retried next startup/change
    }

    [Fact]
    public async Task Shrinking_touched_set_removes_dropped_pages()
    {
        var sourceRef = Seed("a.txt", "v1");
        _ingest.ResultFor = _ => new IngestResult(sourceRef,
            ["memory/topics/x.md", "memory/topics/y.md"]);
        using var svc = Build();
        await svc.RunAsync(sourceRef, TestContext.Current.CancellationToken);

        _ingest.ResultFor = _ => new IngestResult(sourceRef, ["memory/topics/x.md"]);
        await svc.RunAsync(sourceRef, TestContext.Current.CancellationToken);

        var remove = Assert.Single(_ingest.RemoveCalls);
        Assert.Equal(["memory/topics/y.md"], remove.Pages);
        Assert.Equal(["memory/topics/x.md"], (await _state.GetAsync(sourceRef))!.TouchedPages);
    }

    [Fact]
    public async Task Degenerate_outcome_after_success_removes_all_contributions()
    {
        var sourceRef = Seed("a.txt", "v1");
        _ingest.ResultFor = _ => new IngestResult(sourceRef, ["memory/topics/x.md"]);
        using var svc = Build();
        await svc.RunAsync(sourceRef, TestContext.Current.CancellationToken);

        _ingest.ResultFor = _ => new IngestResult(sourceRef, [], IngestOutcome.NoEntities);
        await svc.RunAsync(sourceRef, TestContext.Current.CancellationToken);

        var remove = Assert.Single(_ingest.RemoveCalls);
        Assert.Equal(["memory/topics/x.md"], remove.Pages);
        var state = await _state.GetAsync(sourceRef);
        Assert.Equal(IngestOutcome.NoEntities, state!.Outcome);
        Assert.Empty(state.TouchedPages);
    }

    [Fact]
    public async Task SourceNotFound_records_nothing()
    {
        var sourceRef = Seed("a.txt", "v1");
        _ingest.ResultFor = _ => new IngestResult(sourceRef, [], IngestOutcome.SourceNotFound);
        using var svc = Build();

        await svc.RunAsync(sourceRef, TestContext.Current.CancellationToken);

        Assert.Null(await _state.GetAsync(sourceRef));
    }

    [Fact]
    public async Task SynthesisFailed_records_nothing_and_does_not_remove()
    {
        var sourceRef = Seed("a.txt", "v1");
        // A prior clean run recorded a touched page.
        _ingest.ResultFor = _ => new IngestResult(sourceRef, ["memory/topics/x.md"]);
        using var svc = Build();
        await svc.RunAsync(sourceRef, TestContext.Current.CancellationToken);
        _ingest.RemoveCalls.Clear();

        // A subsequent flaky run reports SynthesisFailed — transient: record nothing, prune nothing.
        Seed("a.txt", "v2");
        _ingest.ResultFor = _ => new IngestResult(sourceRef, ["memory/topics/x.md"], IngestOutcome.SynthesisFailed);
        await svc.RunAsync(sourceRef, TestContext.Current.CancellationToken);

        Assert.Empty(_ingest.RemoveCalls); // no shrink-diff wipe off the back of a flaky provider
        // The state row is unchanged from the clean run (Success), so the hash never froze on failure.
        var state = await _state.GetAsync(sourceRef);
        Assert.Equal(IngestOutcome.Success, state!.Outcome);
        Assert.Equal(["memory/topics/x.md"], state.TouchedPages);
    }

    [Fact]
    public async Task StartAsync_with_setting_off_does_not_watch_or_reconcile()
    {
        Seed("a.txt", "v1");
        _settings.Enabled = false;
        using var svc = Build();

        await svc.StartAsync(_vaultRoot);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.Empty(_ingest.IngestCalls);
    }

    [Fact]
    public async Task IngestCompleted_fires_after_run()
    {
        var sourceRef = Seed("a.txt", "v1");
        using var svc = Build();
        var fired = 0;
        svc.IngestCompleted += (_, _) => Interlocked.Increment(ref fired);

        await svc.RunAsync(sourceRef, TestContext.Current.CancellationToken);

        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task Watcher_ingests_a_dropped_file_after_debounce()
    {
        using var svc = Build();
        await svc.StartAsync(_vaultRoot);

        Seed("dropped.txt", "hello");

        // Debounce is 3 s; poll up to 15 s for the serial queue to process it.
        for (var i = 0; i < 150 && _ingest.IngestCalls.Count == 0; i++)
            await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(["sources/dropped.txt"], _ingest.IngestCalls);
    }

    [Fact]
    public async Task Watcher_collapses_rapid_writes_into_one_ingest()
    {
        using var svc = Build();
        await svc.StartAsync(_vaultRoot);

        // Five writes inside one 3 s debounce window must produce exactly ONE ingest — and the
        // under-lock hash re-check must keep a racing duplicate event from double-spending.
        for (var i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(_sourcesDir, "burst.txt"), $"content v{i}");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        for (var i = 0; i < 150 && _ingest.IngestCalls.Count == 0; i++)
            await Task.Delay(100, TestContext.Current.CancellationToken);
        await Task.Delay(1000, TestContext.Current.CancellationToken); // grace: no second call

        Assert.Equal(["sources/burst.txt"], _ingest.IngestCalls);
    }

    [Fact]
    public async Task RestartAsync_moves_the_watcher_to_a_new_root()
    {
        using var svc = Build();
        await svc.StartAsync(_vaultRoot);

        var newVault = Path.Combine(_tmpDir, "vault2");
        Directory.CreateDirectory(Path.Combine(newVault, "sources"));
        _paths.SetRoot(newVault); // relocation re-points the provider before restarting
        await svc.RestartAsync(newVault);

        File.WriteAllText(Path.Combine(newVault, "sources", "moved.txt"), "hello");
        for (var i = 0; i < 150 && _ingest.IngestCalls.Count == 0; i++)
            await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Contains("sources/moved.txt", _ingest.IngestCalls);
    }

    // ---- stubs ----

    /// <summary>Records ingest/remove calls; results configurable per source ref.</summary>
    private sealed class RecordingIngestService : IIngestService
    {
        public List<string> IngestCalls { get; } = [];
        public List<(string Source, IReadOnlyList<string> Pages)> RemoveCalls { get; } = [];

        /// <summary>Result factory; defaults to Success touching <c>memory/topics/&lt;name&gt;.md</c>.</summary>
        public Func<string, IngestResult>? ResultFor { get; set; }

        public Task<IngestResult> IngestAsync(
            string sourceRelativePath, DateOnly date, CancellationToken ct = default)
        {
            lock (IngestCalls)
            {
                IngestCalls.Add(sourceRelativePath);
            }

            var result = ResultFor?.Invoke(sourceRelativePath) ?? new IngestResult(
                sourceRelativePath,
                [$"memory/topics/{Path.GetFileNameWithoutExtension(sourceRelativePath)}.md"]);
            return Task.FromResult(result);
        }

        public Task RemoveContributionsAsync(
            string sourceRef, IReadOnlyList<string> pages, CancellationToken ct = default)
        {
            lock (RemoveCalls)
            {
                RemoveCalls.Add((sourceRef, pages));
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>Only <see cref="GetDefaultProviderAsync"/> matters to the scheduler.</summary>
    private sealed class StubProviderService : IProviderService
    {
        public bool HasProvider { get; set; } = true;

#pragma warning disable CS0067 // Event is never used in tests.
        public event EventHandler? ProvidersChanged;
#pragma warning restore CS0067

        public Task<AiProvider?> GetDefaultProviderAsync() => Task.FromResult<AiProvider?>(
            HasProvider ? new AiProvider { Name = "stub", Endpoint = "http://localhost" } : null);

        public Task<IReadOnlyList<AiProvider>> GetProvidersAsync()
            => Task.FromResult<IReadOnlyList<AiProvider>>([]);

        public Task<AiProvider?> GetProviderAsync(Guid id) => Task.FromResult<AiProvider?>(null);
        public Task<AiProvider?> GetDefaultProviderForModeAsync(WindowMode mode) => Task.FromResult<AiProvider?>(null);
        public Task<AiProvider> AddProviderAsync(AiProvider provider, string? apiKey) => throw new NotImplementedException();
        public Task UpdateProviderAsync(AiProvider provider, string? newApiKey = null) => throw new NotImplementedException();
        public Task DeleteProviderAsync(Guid id) => throw new NotImplementedException();
        public string? GetDecryptedApiKey(AiProvider provider) => null;
        public Task<TestConnectionResult> TestConnectionAsync(AiProvider provider) => throw new NotImplementedException();
        public Task<TestConnectionResult> TestConnectionAsync(AiProvider provider, string? plainApiKey) => throw new NotImplementedException();
        public Task EnsureBuiltInProviderAsync() => Task.CompletedTask;
        public Task<List<string>> FetchModelsAsync(string endpoint, string? apiKey, AiProviderType providerType) => throw new NotImplementedException();
        public Task<bool> IsProviderActiveAsync(AiProvider provider) => Task.FromResult(true);
        public Task ReassignProviderIdAsync(Guid oldId, Guid newId, AiProvider merged) => Task.CompletedTask;
        public Task RepairModeDefaultsAsync() => Task.CompletedTask;
        public Task ConsolidateLocalDuplicatesAsync() => Task.CompletedTask;
    }

    /// <summary>Serves <see cref="AppSettings.AutoIngestSources"/> from <see cref="Enabled"/>.</summary>
    private sealed class StubSettingsService : ISettingsService
    {
        public bool Enabled { get; set; } = true;

#pragma warning disable CS0067 // Event is never used in tests.
        public event EventHandler<AppSettings>? SettingsChanged;
#pragma warning restore CS0067

        public Task<AppSettings> GetSettingsAsync()
            => Task.FromResult(new AppSettings { AutoIngestSources = Enabled });

        public Task SaveSettingsAsync(AppSettings settings) => Task.CompletedTask;
        public Task SaveDraftAsync(string? draftText) => Task.CompletedTask;
        public Task<string?> GetDraftAsync() => Task.FromResult<string?>(null);
    }
}
