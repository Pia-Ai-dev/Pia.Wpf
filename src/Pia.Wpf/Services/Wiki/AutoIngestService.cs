using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure.Vault;
using Pia.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services.Wiki;

/// <summary>
/// The auto-ingest pipeline (spec: docs/superpowers/specs/2026-07-07-auto-ingest-sources-design.md).
/// One serial queue for ALL ingest work — a <c>sources/</c> FileSystemWatcher (any extension; the
/// vault watcher's *.md files under <c>sources/</c> are excluded from recall by
/// <see cref="Pia.Infrastructure.Vault.VaultPaths.IsRecallIndexable"/>, so a <c>.md</c> source is only
/// ingested into topic pages, never also embedded raw), the startup reconcile scan, and manual tool runs via
/// <see cref="IIngestScheduler"/>. Automatic triggers are hash-gated against
/// <see cref="IngestStateStore"/> and gated on the AutoIngestSources setting + a configured AI
/// provider; the manual path always executes. After every ingest the previous touched-set is
/// diffed against the new one and dropped pages get their contributions removed — that diff is
/// what makes replace-per-source true when a source shrinks or degrades to no entities.
/// Start/Stop/Restart mirror VaultWatcher so folder relocation can release the directory handle.
/// </summary>
public sealed class AutoIngestService : IIngestScheduler, IDisposable
{
    /// <summary>Longer than VaultWatcher's 300 ms: source files arrive by multi-second copy.</summary>
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(3);

    private readonly IIngestService _ingest;
    private readonly IngestStateStore _state;
    private readonly IVaultStore _store;
    private readonly IProviderService _providers;
    private readonly ISettingsService _settings;
    private readonly VaultPathProvider _paths;
    private readonly ILogger<AutoIngestService> _logger;

    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly ConcurrentDictionary<string, Timer> _pending = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private string? _sourcesDir;
    private bool _disposed;

    public string? CurrentSourceRef { get; private set; }

    public event EventHandler<string>? IngestStarted;
    public event EventHandler? IngestCompleted;

    public AutoIngestService(
        IIngestService ingest,
        IngestStateStore state,
        IVaultStore store,
        IProviderService providers,
        ISettingsService settings,
        VaultPathProvider paths,
        ILogger<AutoIngestService> logger)
    {
        _ingest = ingest;
        _state = state;
        _store = store;
        _providers = providers;
        _settings = settings;
        _paths = paths;
        _logger = logger;
    }

    // ---- lifecycle (mirrors VaultWatcher so relocation can release the directory handle) ----

    public Task StartAsync() => StartAsync(_paths.VaultRoot);

    public async Task StartAsync(string vaultRoot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_watcher is not null)
        {
            return;
        }

        var settings = await _settings.GetSettingsAsync();
        if (!settings.AutoIngestSources)
        {
            _logger.LogInformation("Auto-ingest disabled by setting; manual ingest remains available");
            return;
        }

        // Created defensively: FileSystemWatcher throws on a missing root, and we must not depend
        // on VaultSchemaService's scaffolding order.
        var sourcesDir = Path.Combine(vaultRoot, "sources");
        Directory.CreateDirectory(sourcesDir);
        _sourcesDir = sourcesDir;

        var watcher = new FileSystemWatcher(sourcesDir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        watcher.Created += OnChangedOrCreated;
        watcher.Changed += OnChangedOrCreated;
        watcher.Renamed += OnRenamed;
        watcher.Deleted += OnDeleted;
        watcher.Error += OnError;
        watcher.EnableRaisingEvents = true;
        _watcher = watcher;

        // The reconcile scan is the queue's first work; LLM-bound items drain in the background so
        // startup is never blocked. A watcher event racing the scan is harmless — the second run
        // no-ops on the recorded hash (re-checked under the serial lock).
        _ = Task.Run(() => ReconcileGuardedAsync());

        _logger.LogInformation("Auto-ingest watcher started");
    }

    public void Stop()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnChangedOrCreated;
            _watcher.Changed -= OnChangedOrCreated;
            _watcher.Renamed -= OnRenamed;
            _watcher.Deleted -= OnDeleted;
            _watcher.Error -= OnError;
            _watcher.Dispose();
            _watcher = null;
        }

        foreach (var timer in _pending.Values)
        {
            timer.Dispose();
        }

        _pending.Clear();
    }

    public Task RestartAsync(string vaultRoot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        return StartAsync(vaultRoot);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _serial.Dispose();
    }

    // ---- IIngestScheduler ----

    public Task<IngestResult> RunAsync(string sourceRef, CancellationToken ct = default)
        => ExecuteAsync(Normalize(sourceRef), knownHash: null, autoGated: false, ct);

    public async Task RemoveAsync(string sourceRef, CancellationToken ct = default)
    {
        sourceRef = Normalize(sourceRef);
        await _serial.WaitAsync(ct);
        try
        {
            var state = await _state.GetAsync(sourceRef);
            IReadOnlyList<string> pages = state?.TouchedPages is { Count: > 0 } touched
                ? touched
                : await ScanPagesForSourceAsync(sourceRef);
            if (pages.Count > 0)
            {
                await _ingest.RemoveContributionsAsync(sourceRef, pages, ct);
            }

            // Delete the row so the next reconcile doesn't re-enqueue this removal forever.
            await _state.DeleteAsync(sourceRef);
        }
        finally
        {
            _serial.Release();
            RaiseIngestCompleted();
        }
    }

    // ---- reconcile (public for tests; called by StartAsync on the background queue) ----

    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        var settings = await _settings.GetSettingsAsync();
        if (!settings.AutoIngestSources)
        {
            return;
        }

        var sourcesDir = _sourcesDir ?? Path.Combine(_paths.VaultRoot, "sources");
        if (!Directory.Exists(sourcesDir))
        {
            return;
        }

        var files = Directory.EnumerateFiles(sourcesDir, "*", SearchOption.AllDirectories).ToList();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            await AutoRunAsync(ToRef(sourcesDir, file), ct);
        }

        // Tracked but gone from disk -> the source was deleted while we weren't watching.
        var onDisk = new HashSet<string>(
            files.Select(f => ToRef(sourcesDir, f)), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in await _state.ListAsync())
        {
            ct.ThrowIfCancellationRequested();
            if (!onDisk.Contains(entry.SourceRef))
            {
                await RemoveAsync(entry.SourceRef, ct);
            }
        }

        _logger.LogInformation("Auto-ingest reconcile completed over {Count} source file(s)", files.Count);
    }

    private async Task ReconcileGuardedAsync()
    {
        try
        {
            await ReconcileAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-ingest reconcile failed");
        }
    }

    // ---- internals ----

    /// <summary>Hash-gated automatic run: skips when content is unchanged since the last record.</summary>
    private async Task AutoRunAsync(string sourceRef, CancellationToken ct)
    {
        try
        {
            if (await _providers.GetDefaultProviderAsync() is null)
            {
                // No record is written, so the source is retried on the next change or startup.
                _logger.LogDebug("Auto-ingest skipped: no AI provider configured");
                return;
            }

            var hash = TryHashFile(sourceRef);
            if (hash is null)
            {
                return; // vanished mid-flight; the Deleted event / next reconcile cleans up
            }

            var state = await _state.GetAsync(sourceRef);
            if (string.Equals(state?.ContentHash, hash, StringComparison.Ordinal))
            {
                return; // unchanged — never re-spend the LLM calls
            }

            await ExecuteAsync(sourceRef, hash, autoGated: true, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-ingest failed to process a source");
            _logger.SensitiveDebug("Auto-ingest failed on {Source}", sourceRef);
        }
    }

    private async Task<IngestResult> ExecuteAsync(
        string sourceRef, string? knownHash, bool autoGated, CancellationToken ct)
    {
        await _serial.WaitAsync(ct);
        try
        {
            // The pre-semaphore hash check in AutoRunAsync is only an early-out; it can race an
            // in-flight ingest of the same file (reconcile scan vs watcher event — a real window,
            // since each ingest is two LLM calls). Re-check under the lock so the loser of that
            // race no-ops instead of double-spending. Manual runs (autoGated: false) always run.
            if (autoGated)
            {
                var gate = TryHashFile(sourceRef);
                if (gate is null)
                {
                    return new IngestResult(sourceRef, [], IngestOutcome.SourceNotFound);
                }

                var recorded = await _state.GetAsync(sourceRef);
                if (string.Equals(recorded?.ContentHash, gate, StringComparison.Ordinal))
                {
                    // Deliberate: the finally still raises IngestCompleted for this no-op — a
                    // spurious sources-overview reload is cheap and keeps the event contract simple.
                    return new IngestResult(sourceRef, recorded!.TouchedPages, recorded.Outcome);
                }

                knownHash = gate;
            }

            // Past the gates: real LLM work starts now. Publish the running ref (authoritative, read by
            // views that open mid-ingest) and signal subscribers before the two-call synthesis begins.
            CurrentSourceRef = sourceRef;
            RaiseIngestStarted(sourceRef);

            var result = await _ingest.IngestAsync(sourceRef, DateOnly.FromDateTime(DateTime.Now), ct);
            if (result.Outcome is IngestOutcome.SourceNotFound or IngestOutcome.SynthesisFailed)
            {
                // Transient: record nothing — retried on next change/reconcile. SynthesisFailed means
                // topics were found but ≥1 page's synthesis came back empty (flaky/absent provider);
                // recording it would freeze the hash and let the shrink-diff wipe good contributions.
                if (result.Outcome == IngestOutcome.SynthesisFailed)
                {
                    _logger.LogWarning("Ingest synthesis failed; source will be retried");
                    _logger.SensitiveDebug("Ingest synthesis failed for {Source}; source will be retried", sourceRef);
                }

                return result;
            }

            // Without a provider the extractor degrades to NoEntities. Recording THAT would (a)
            // freeze the hash so the source is never retried once a provider exists, and (b) run
            // the shrink-diff below and wipe valid contributions. Treat it as transient instead.
            if (await _providers.GetDefaultProviderAsync() is null)
            {
                return result;
            }

            var hash = knownHash ?? TryHashFile(sourceRef);
            if (hash is null)
            {
                return result; // file vanished after ingest read it; next event settles it
            }

            var previous = await _state.GetAsync(sourceRef);
            IReadOnlyList<string> newTouched =
                result.Outcome == IngestOutcome.Success ? result.TouchedPages : [];
            var dropped = (previous?.TouchedPages ?? [])
                .Where(p => !newTouched.Contains(p, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (dropped.Count > 0)
            {
                // The pages v(n-1) touched but v(n) no longer does — strip the stale sections.
                await _ingest.RemoveContributionsAsync(sourceRef, dropped, ct);
            }

            await _state.UpsertAsync(new IngestStateEntry(
                sourceRef, hash, result.Outcome, newTouched, DateTimeOffset.UtcNow));

            _logger.LogInformation("Auto-ingest completed ({Outcome}, {Count} page(s))",
                result.Outcome, newTouched.Count);
            return result;
        }
        finally
        {
            // Clear BEFORE raising so a subscriber's reload reads the idle state (no stuck "Ingesting…").
            CurrentSourceRef = null;
            _serial.Release();
            RaiseIngestCompleted();
        }
    }

    private void RaiseIngestStarted(string sourceRef)
    {
        try
        {
            IngestStarted?.Invoke(this, sourceRef);
        }
        catch (Exception ex)
        {
            // A throwing subscriber must not abort the ingest we are about to run.
            _logger.LogWarning(ex, "IngestStarted subscriber threw");
        }
    }

    private void RaiseIngestCompleted()
    {
        try
        {
            IngestCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // A throwing subscriber must not mask the queue item's own result (this runs in finally).
            _logger.LogWarning(ex, "IngestCompleted subscriber threw");
        }
    }

    /// <summary>Fallback when the state row is missing: find pages via their sources: frontmatter.</summary>
    private async Task<IReadOnlyList<string>> ScanPagesForSourceAsync(string sourceRef)
    {
        var hits = new List<string>();
        foreach (var path in await _store.EnumerateAsync("memory/topics/*.md"))
        {
            var doc = await _store.ReadAsync(path);
            if (doc is not null && SourcesProvenance.ReadSourceRefs(doc.RawText)
                    .Contains(sourceRef, StringComparer.OrdinalIgnoreCase))
            {
                // EnumerateAsync returns native separators (backslash on Windows); the removal
                // pipeline and index keys are forward-slash.
                hits.Add(path.Replace('\\', '/'));
            }
        }

        return hits;
    }

    private string? TryHashFile(string sourceRef)
    {
        var full = Path.Combine(
            _paths.VaultRoot, sourceRef.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            using var stream = File.OpenRead(full);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---- watcher plumbing (same debounce shape as VaultWatcher, longer window) ----

    private void OnChangedOrCreated(object sender, FileSystemEventArgs e) => Schedule(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.OldFullPath))
        {
            Schedule(e.OldFullPath); // fires as removal — the old ref's file no longer exists
        }

        Schedule(e.FullPath);
    }

    private void OnDeleted(object sender, FileSystemEventArgs e) => Schedule(e.FullPath);

    private void OnError(object sender, ErrorEventArgs e) =>
        _logger.LogWarning(e.GetException(), "Auto-ingest watcher error");

    private void Schedule(string fullPath)
    {
        // Directory events carry no ingestable content; a directory delete surfaces per-file.
        if (_disposed || _sourcesDir is null || Directory.Exists(fullPath))
        {
            return;
        }

        var sourceRef = ToRef(_sourcesDir, fullPath);
        var timer = new Timer(_ => Fire(sourceRef), null, DebounceWindow, Timeout.InfiniteTimeSpan);
        if (_pending.TryRemove(sourceRef, out var previous))
        {
            previous.Dispose();
        }

        _pending[sourceRef] = timer;
    }

    // Timer callbacks need a void entry point; the async body is fire-and-forget but fully
    // guarded, so nothing can escape (async void would rethrow onto the ThreadPool and crash).
    private void Fire(string sourceRef) => _ = FireAsync(sourceRef);

    private async Task FireAsync(string sourceRef)
    {
        if (_pending.TryRemove(sourceRef, out var timer))
        {
            timer.Dispose();
        }

        try
        {
            var full = Path.Combine(
                _paths.VaultRoot, sourceRef.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                await AutoRunAsync(sourceRef, CancellationToken.None);
            }
            else
            {
                await RemoveAsync(sourceRef, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            // A watcher callback must never crash the process; surface and move on.
            _logger.LogWarning(ex, "Auto-ingest failed to process a change");
            _logger.SensitiveDebug("Auto-ingest failed on {Source}", sourceRef);
        }
    }

    private static string ToRef(string sourcesDir, string fullPath) =>
        "sources/" + Path.GetRelativePath(sourcesDir, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/');

    private static string Normalize(string sourceRef) =>
        sourceRef.Trim().Replace('\\', '/').TrimStart('/');
}
