using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Shared.E2EE;
using Pia.Shared.Models;
using Pia.Shared.Sync;

namespace Pia.Services;

public class SyncClientService : ISyncClientService, IDisposable
{
    private readonly IAuthService _authService;
    private readonly ISettingsService _settingsService;
    private readonly ITemplateService _templateService;
    private readonly IPersonaService? _personaService;
    private readonly IProviderService _providerService;
    private readonly IHistoryService _historyService;
    private readonly IMemoryService _memoryService;
    private readonly ITodoService? _todoService;
    private readonly IKanbanColumnService? _columnService;
    private readonly IScheduledJobService? _scheduledJobService;
    private readonly IE2EEService? _e2ee;
    private readonly IDeviceManagementService? _deviceMgmt;
    private readonly IDeviceKeyService? _deviceKeys;
    private readonly IPluginService? _pluginService;
    private readonly IPolicyService? _policyService;
    private readonly SyncMapper _mapper;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SyncClientService> _logger;
    private readonly SyncDeleteTrackerService _deleteTracker;

    // Single serializer for both push sites (first-sync + delta). camelCase unifies the previous
    // PascalCase(delta)/camelCase(first-sync) split; WhenWritingNull trims "field":null keys
    // (notably an omitted Settings when unchanged); case-insensitive read keeps parity with the
    // server's case-insensitive binder. The server accepts either casing, so this is a pure
    // client cleanup with no server-compat impact.
    internal static readonly JsonSerializerOptions PushSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private Timer? _syncTimer;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(5);
    // Backoff ceiling: after enough consecutive idle cycles the period grows toward this.
    private static readonly TimeSpan MaxSyncInterval = TimeSpan.FromMinutes(15);
    // First run after 10 seconds (one-shot; the cycle re-arms itself thereafter).
    private static readonly TimeSpan InitialSyncDelay = TimeSpan.FromSeconds(10);
    // Number of consecutive idle cycles before the period starts backing off toward the ceiling.
    private const int IdleBackoffThreshold = 6;
    // Multiplicative growth applied per idle cycle beyond the threshold.
    private const double BackoffGrowthFactor = 1.5;
    // +/- jitter applied to every scheduled delay to break thundering-herd alignment.
    private const double JitterFraction = 0.20;
    // Interim (Phase 2.2): the pending-device check runs on this cadence (every Nth eligible
    // cycle) instead of every cycle, until the pull response carries PendingDevices (Phase 5).
    private const int DeviceCheckCadence = 6;
    // Max sessions per first-sync push body (Sec 6.4). Sessions dominate the payload, so the chunked
    // first-sync migration ships <=this many sessions per POST to stay within the 30/60s rate limit.
    private const int FirstSyncBatchSize = 200;
    // Page size for loading every session during first-sync (replaces the old silent 10k cap).
    private const int SessionLoadPageSize = 1000;
    // Mirrors the server's per-user "sync" rate-limit policy (Pia.Server RateLimitOptions: default
    // Sync PermitLimit=30 requests / 60s sliding window). The chunked first-sync push paces itself
    // against this so an account with many batches (~29+, i.e. ~5,800+ sessions at
    // FirstSyncBatchSize=200) never trips the limit: RateLimitRetryHandler's exponential backoff
    // cannot recover from a sliding-window 429 (no Retry-After is sent, and the window doesn't free
    // a permit any sooner), so an unpaced loop would abort the migration permanently and re-fail at
    // the same batch on every retry.
    private const int SyncRateLimitPermits = 30;
    private static readonly TimeSpan SyncRateLimitWindow = TimeSpan.FromSeconds(60);
    // Safety margin below the server's real permit count, leaving headroom for the trailing pull
    // (and any other concurrent sync traffic) that shares the same rate-limit bucket.
    private const int SyncRateLimitSafetyMargin = 2;

    private int _consecutiveIdleCycles;
    private int _deviceCheckCounter;
    private bool _hasVerifiedServerE2EEStatus;
    // Bumped on every Start/Stop so a sync cycle in flight during a Stop+Start cannot re-arm
    // the newly-started timer with a stale (non-InitialSyncDelay) period.
    private int _syncGeneration;

    public bool IsSyncActive => _syncTimer is not null;
    public bool IsE2EEOnboardingRequired { get; private set; }
    public event EventHandler? E2EEOnboardingRequired;
    public event EventHandler? E2EEOnboardingCleared;
    public event EventHandler<PendingDeviceEventArgs>? PendingDeviceDetected;
    public event EventHandler? CurrentDeviceRevoked;
    public event EventHandler<SyncCompletedEventArgs>? SyncCompleted;

    public void NotifyE2EEOnboardingRequired()
    {
        if (IsE2EEOnboardingRequired) return;
        IsE2EEOnboardingRequired = true;
        E2EEOnboardingRequired?.Invoke(this, EventArgs.Empty);
    }

    public void NotifyE2EEOnboardingCompleted()
    {
        if (!IsE2EEOnboardingRequired) return;
        IsE2EEOnboardingRequired = false;
        E2EEOnboardingCleared?.Invoke(this, EventArgs.Empty);
    }

    public SyncClientService(
        IAuthService authService,
        ISettingsService settingsService,
        ITemplateService templateService,
        IProviderService providerService,
        IHistoryService historyService,
        IMemoryService memoryService,
        SyncMapper mapper,
        IHttpClientFactory httpClientFactory,
        ILogger<SyncClientService> logger,
        SyncDeleteTrackerService deleteTracker,
        ITodoService? todoService = null,
        IKanbanColumnService? columnService = null,
        IScheduledJobService? scheduledJobService = null,
        IE2EEService? e2ee = null,
        IDeviceManagementService? deviceMgmt = null,
        IDeviceKeyService? deviceKeys = null,
        IPluginService? pluginService = null,
        IPersonaService? personaService = null,
        IPolicyService? policyService = null)
    {
        _authService = authService;
        _settingsService = settingsService;
        _templateService = templateService;
        _personaService = personaService;
        _providerService = providerService;
        _historyService = historyService;
        _memoryService = memoryService;
        _todoService = todoService;
        _columnService = columnService;
        _scheduledJobService = scheduledJobService;
        _e2ee = e2ee;
        _deviceMgmt = deviceMgmt;
        _deviceKeys = deviceKeys;
        _pluginService = pluginService;
        _policyService = policyService;
        _mapper = mapper;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _deleteTracker = deleteTracker;

        _authService.LoginStateChanged += OnAuthLoginStateChanged;
    }

    private void OnAuthLoginStateChanged(object? sender, bool isLoggedIn)
    {
        if (isLoggedIn) return;
        _hasVerifiedServerE2EEStatus = false;
        NotifyE2EEOnboardingCompleted();
    }

    public void StartBackgroundSync()
    {
        if (_syncTimer is not null) return;

        _consecutiveIdleCycles = 0;
        _deviceCheckCounter = 0;
        var generation = ++_syncGeneration;

        // One-shot timer: it fires once after InitialSyncDelay, then each cycle re-arms the
        // timer for a jittered, adaptively-backed-off delay via ArmNextSyncCycle. An infinite
        // period keeps the timer from auto-repeating. The timer instance and generation are
        // captured in the closure so a Stop+Start that races an in-flight cycle can never let
        // the stale cycle re-arm the new session's timer (see ArmNextSyncCycle).
        Timer? timer = null;
        timer = new Timer(async _ =>
        {
            try { await SyncNowAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Background sync cycle failed"); }
            finally { ArmNextSyncCycle(generation, timer!); }
        }, null, InitialSyncDelay, Timeout.InfiniteTimeSpan);
        _syncTimer = timer;

        _logger.LogInformation("Background sync started (base interval: {Interval})", SyncInterval);
    }

    public void StopBackgroundSync()
    {
        _syncGeneration++; // invalidate any in-flight cycle's pending re-arm
        _syncTimer?.Dispose();
        _syncTimer = null;
        _hasVerifiedServerE2EEStatus = false;
        _logger.LogInformation("Background sync stopped");
    }

    // Re-arm the one-shot sync timer for the next cycle. Called from the timer callback after
    // every SyncNowAsync run. The delay adapts to how many consecutive idle cycles have elapsed
    // (backoff) and carries +/- jitter to avoid synchronized fleet-wide polling.
    // <paramref name="generation"/>/<paramref name="timer"/> pin this call to the session that
    // scheduled it: if StopBackgroundSync (and possibly a subsequent StartBackgroundSync) ran
    // while the cycle was in flight, both the generation and the timer reference will have
    // moved on, and this stale callback must not touch the new session's timer.
    private void ArmNextSyncCycle(int generation, Timer timer)
    {
        if (generation != _syncGeneration || !ReferenceEquals(timer, _syncTimer))
            return; // background sync was stopped (and possibly restarted) since this cycle began

        var delay = ComputeNextSyncDelay(_consecutiveIdleCycles, Random.Shared.NextDouble());
        try
        {
            timer.Change(delay, Timeout.InfiniteTimeSpan);
            _logger.LogDebug("Next background sync in {DelaySeconds:F0}s (consecutive idle cycles: {Idle})",
                delay.TotalSeconds, _consecutiveIdleCycles);
        }
        catch (ObjectDisposedException)
        {
            // Timer was disposed concurrently by StopBackgroundSync; nothing to re-arm.
        }
    }

    /// <summary>
    /// Pure scheduling math (unit-testable): computes the next sync delay from the number of
    /// consecutive idle cycles and a uniform random unit value in [0,1). Below the backoff
    /// threshold the base period is used; at/above it the period grows by
    /// <see cref="BackoffGrowthFactor"/> per extra idle cycle, capped at <see cref="MaxSyncInterval"/>.
    /// A +/- <see cref="JitterFraction"/> jitter is then applied.
    /// </summary>
    internal static TimeSpan ComputeNextSyncDelay(int consecutiveIdleCycles, double randomUnit)
    {
        var period = SyncInterval.TotalMilliseconds;
        if (consecutiveIdleCycles >= IdleBackoffThreshold)
        {
            var exponent = consecutiveIdleCycles - IdleBackoffThreshold + 1;
            period = SyncInterval.TotalMilliseconds * Math.Pow(BackoffGrowthFactor, exponent);
            period = Math.Min(period, MaxSyncInterval.TotalMilliseconds);
        }

        // Map randomUnit [0,1) -> jitter multiplier [1 - JitterFraction, 1 + JitterFraction).
        var jitterMultiplier = 1.0 + (randomUnit * 2.0 - 1.0) * JitterFraction;
        return TimeSpan.FromMilliseconds(period * jitterMultiplier);
    }

    /// <summary>
    /// Pure classification of a completed sync cycle from the push outcome and the pull tuple.
    /// A cycle is <see cref="SyncCycleOutcome.Active"/> when it moved data (the push sent
    /// changes — upserts, deletes, or plugin preferences — or a pull returned rows),
    /// <see cref="SyncCycleOutcome.Idle"/> when the push had nothing to send and the pull
    /// succeeded with no changes (304 -> ServerTimestamp null), and
    /// <see cref="SyncCycleOutcome.Inconclusive"/> otherwise (e.g. a failed push or a failed
    /// pull), which neither advances nor resets the backoff. A failed push must never be
    /// classified as idle: unpushed local changes are still pending and should not engage
    /// backoff, which would slow their retry.
    /// </summary>
    internal static SyncCycleOutcome ClassifyCycle(bool pushSucceeded, bool pushSentChanges, int pulled, bool pullSucceeded, DateTime? serverTimestamp)
    {
        if (!pushSucceeded)
            return SyncCycleOutcome.Inconclusive;
        if (pushSentChanges || pulled > 0)
            return SyncCycleOutcome.Active;
        if (pullSucceeded && serverTimestamp is null)
            return SyncCycleOutcome.Idle;
        return SyncCycleOutcome.Inconclusive;
    }

    /// <summary>
    /// Pure update of the consecutive-idle counter: reset to 0 on activity, increment on idle,
    /// leave unchanged on an inconclusive cycle.
    /// </summary>
    internal static int UpdateIdleCycleCount(int current, SyncCycleOutcome outcome) => outcome switch
    {
        SyncCycleOutcome.Active => 0,
        SyncCycleOutcome.Idle => current + 1,
        _ => current,
    };

    /// <summary>
    /// Pure cadence gate for the interim (Phase 2.2) pending-device check (Sec 4.2): true on
    /// the first eligible cycle (counter 0) and every <see cref="DeviceCheckCadence"/>th cycle
    /// thereafter.
    /// </summary>
    internal static bool ShouldCheckDevices(int counter) => counter % DeviceCheckCadence == 0;

    /// <summary>
    /// Pure state-machine step for the interim (Phase 2.2) pending-device check cadence:
    /// decides whether to check this cycle (<see cref="ShouldCheckDevices"/>, or unconditionally
    /// when <paramref name="got200Pull"/>) and what the counter should be for the next cycle.
    /// After a check the counter resets to 1 (not 0) so the cadence engages every
    /// <see cref="DeviceCheckCadence"/>th eligible cycle instead of every cycle — resetting to 0
    /// would make <see cref="ShouldCheckDevices"/> true again on the very next call.
    /// </summary>
    internal static (bool ShouldCheck, int NextCounter) AdvanceDeviceCheck(int counter, bool got200Pull)
    {
        var shouldCheck = ShouldCheckDevices(counter) || got200Pull;
        var nextCounter = shouldCheck ? 1 : counter + 1;
        return (shouldCheck, nextCounter);
    }

    public async Task<SyncResult?> SyncNowAsync()
    {
        if (!_authService.IsLoggedIn) return null;

        // Non-blocking: skip if another sync is already running
        if (!await _syncLock.WaitAsync(0)) return null;

        SyncResult? result = null;
        var syncSw = Stopwatch.StartNew();
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            _logger.LogInformation("SyncNowAsync started — LastSyncTimestamp: {LastSync}", settings.LastSyncTimestamp);
            if (!settings.SyncEnabled || string.IsNullOrEmpty(settings.ServerUrl))
                return null;

            // E2EE initialization check: if E2EE is enabled but UMK not available,
            // this is a second device that needs onboarding before sync can proceed.
            if (_e2ee is not null && settings.IsE2EEEnabled && !_e2ee.IsReady())
            {
                _logger.LogWarning("E2EE enabled but UMK not available; onboarding required");
                NotifyE2EEOnboardingRequired();
                return null;
            }

            // Server E2EE check: if local E2EE is off, verify against server to catch cases where
            // E2EE was enabled on another device (e.g., first-run wizard login, app restart).
            // Without this, sync would push IsE2EEEncrypted=false and pull rows it cannot decrypt.
            //
            // The latch is set only on a CONCLUSIVE answer. It used to be set before the call, so a
            // device awaiting recovery-code onboarding checked once, bailed, and then sailed straight
            // through this guard on every later cycle — pulling ciphertext with E2EE inactive. An
            // unreachable server is likewise "unknown", not "off": skip the cycle rather than sync
            // under an assumption that silently blanks encrypted rows.
            if (!_hasVerifiedServerE2EEStatus && _deviceMgmt is not null && !settings.IsE2EEEnabled)
            {
                var serverStatus = await _deviceMgmt.CheckE2EEStatusAsync();
                if (serverStatus is null)
                {
                    _logger.LogWarning("Could not determine server E2EE status; skipping this sync cycle");
                    return null;
                }

                if (serverStatus.IsEnabled)
                {
                    _logger.LogWarning("E2EE enabled on server but not locally; onboarding required");
                    NotifyE2EEOnboardingRequired();
                    return null;
                }

                _hasVerifiedServerE2EEStatus = true;
            }

            var accessToken = await _authService.GetAccessTokenAsync();
            if (string.IsNullOrEmpty(accessToken))
                return null;

            var serverUrl = settings.ServerUrl.TrimEnd('/');
            using var client = CreateAuthenticatedClient(accessToken);

            // Push local changes
            var (pushedCount, pushSucceeded, pushSentChanges) = await PushChangesAsync(client, serverUrl, settings);

            // Pull remote changes
            var (pulled, decryptErrors, pullOk, serverTimestamp) = await PullChangesAsync(client, serverUrl, settings);

            // Only advance the sync cursor if the pull HTTP request succeeded.
            // Use the server's timestamp as the cursor to avoid clock-skew issues between devices.
            // If pull failed (network error, server 500, etc.), keep the old timestamp
            // so the next sync retries from the same point instead of permanently missing data.
            if (pullOk && serverTimestamp.HasValue)
            {
                settings.LastSyncTimestamp = serverTimestamp.Value;
                await _settingsService.SaveSettingsAsync(settings);
            }
            else if (!pullOk)
            {
                _logger.LogWarning("Pull failed — LastSyncTimestamp NOT updated to avoid missing data");
            }

            // Check for pending devices (only if this device is active with E2EE).
            // Interim (Phase 2.2): moved off the hot path onto a slower cadence — every Nth
            // eligible cycle instead of every cycle — until the pull response carries
            // PendingDevices (Phase 5). The first eligible cycle still checks so a device
            // awaiting approval is surfaced promptly. Worst-case latency at the backoff
            // ceiling is bounded by also checking on every 200 pull (ServerTimestamp != null):
            // under the Phase 1 server that is the only signal that something — possibly a
            // device registration — changed server-side, so it is cheap insurance against the
            // cadence alone taking up to ~90 min (6 cycles x the 15 min ceiling) to surface a
            // pending device.
            if (_deviceMgmt is not null && _e2ee?.IsReady() == true)
            {
                var got200Pull = pullOk && serverTimestamp.HasValue;
                var (shouldCheckDevices, nextDeviceCheckCounter) = AdvanceDeviceCheck(_deviceCheckCounter, got200Pull);
                _deviceCheckCounter = nextDeviceCheckCounter;
                if (shouldCheckDevices)
                {
                    await CheckForPendingDevicesAsync();
                }
            }

            // Adaptive scheduling: classify this cycle and update the consecutive-idle counter
            // that ArmNextSyncCycle reads to decide the next delay.
            var outcome = ClassifyCycle(pushSucceeded, pushSentChanges, pulled, pullOk, serverTimestamp);
            _consecutiveIdleCycles = UpdateIdleCycleCount(_consecutiveIdleCycles, outcome);

            result = new SyncResult(pushedCount, pulled, decryptErrors);
            syncSw.Stop();
            _logger.LogInformation("SyncNowAsync completed in {ElapsedMs}ms — Pushed: {Pushed}, Pulled: {Pulled}, DecryptionErrors: {DecryptErrors}",
                syncSw.ElapsedMilliseconds, result.PushedCount, result.PulledCount, result.DecryptionErrors);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sync cycle failed");
        }
        finally
        {
            _syncLock.Release();
        }
        return result;
    }

    public async Task PerformFirstSyncMigrationAsync()
    {
        if (!_authService.IsLoggedIn) return;

        // Blocking: wait for any in-progress sync to finish first
        await _syncLock.WaitAsync();
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            _logger.LogInformation("PerformFirstSyncMigrationAsync: SyncEnabled={Enabled}, ServerUrl={Url}, LastSyncTimestamp={LastSync}, IsE2EEEnabled={E2EE}",
                settings.SyncEnabled, SafeUrl.Format(settings.ServerUrl),
                settings.LastSyncTimestamp?.ToString("O") ?? "(null)", settings.IsE2EEEnabled);

            if (!settings.SyncEnabled || string.IsNullOrEmpty(settings.ServerUrl))
                return;

            // Same readiness gate SyncNowAsync applies. This path pushes EVERY local row with no
            // dirty filter, so running it while the UMK is missing does double damage: it pulls
            // ciphertext it cannot read, and it re-uploads whatever is in the local store over the
            // server's copy. On a restored device the server holds the only copy.
            if (_e2ee is not null && settings.IsE2EEEnabled && !_e2ee.IsReady())
            {
                _logger.LogWarning("E2EE enabled but UMK not available; skipping first-sync migration");
                NotifyE2EEOnboardingRequired();
                return;
            }

            // Local E2EE off is not proof the account's is: a restored device has not activated yet.
            // Callers reach this method after a CheckE2EEStatusAsync that returns null on an
            // unreachable server, and null used to read as "no E2EE" and fall straight through.
            if (_e2ee is not null && _deviceMgmt is not null && !settings.IsE2EEEnabled)
            {
                var serverStatus = await _deviceMgmt.CheckE2EEStatusAsync();
                if (serverStatus is null or { IsEnabled: true })
                {
                    _logger.LogWarning(
                        "Server E2EE status is {Status}; skipping first-sync migration",
                        serverStatus is null ? "unknown" : "enabled");
                    if (serverStatus is not null)
                        NotifyE2EEOnboardingRequired();
                    return;
                }
            }

            var accessToken = await _authService.GetAccessTokenAsync();
            if (string.IsNullOrEmpty(accessToken))
                return;

            var serverUrl = settings.ServerUrl.TrimEnd('/');
            using var client = CreateAuthenticatedClient(accessToken);

            // Build full push request with all local data
            var templates = await _templateService.GetTemplatesAsync();
            var personas = _personaService is not null
                ? await _personaService.GetPersonasAsync()
                : [];
            var providers = await _providerService.GetProvidersAsync();
            // Load every session in pages instead of the old silent 10k cap (GetSessionsAsync(0, 10_000)),
            // so accounts with more than 10k sessions still migrate fully. The chunked push below bounds
            // the request bodies, so there is no reason to cap the load either.
            var sessions = new List<OptimizationSession>();
            for (var offset = 0; ; offset += SessionLoadPageSize)
            {
                var batch = await _historyService.GetSessionsAsync(offset, SessionLoadPageSize);
                if (batch.Count == 0) break;
                sessions.AddRange(batch);
                if (batch.Count < SessionLoadPageSize) break;
            }
            var memories = await _memoryService.GetAllObjectsAsync();
            var kanbanColumns = _columnService is not null
                ? await _columnService.GetAllAsync()
                : Array.Empty<KanbanColumn>();
            var todos = _todoService is not null
                ? await _todoService.GetAllAsync()
                : [];
            // MinValue = "everything": scheduled jobs are cursor-gated in the delta push and
            // were previously omitted here, so a job edited during an E2EE-onboarding window
            // (pushed plaintext -> 403, cursor advanced on pull) could never re-sync. Include
            // them in the full re-push like every other entity.
            var scheduledJobs = _scheduledJobService is not null
                ? await _scheduledJobService.GetModifiedSinceAsync(DateTime.MinValue)
                : [];

            var isE2EE = _e2ee?.IsReady() == true;
            var userId = isE2EE ? settings.SyncUserId : null;

            // Unlike the delta push, first sync never gates Settings on the hash: it's the one
            // path where an unchanged-since-last-push local hash can still mean the server needs
            // a fresh Settings row — e.g. EnableE2EEAsync migrating a previously-plaintext-synced
            // account (settings content unchanged, but the server's row must switch from
            // plaintext to E2EE), or a first sync to a brand-new account/server that has no
            // Settings row at all yet. The byte win from omitting Settings only matters on the
            // frequent delta push; first sync is rare, so always include them here.
            var settingsHash = SyncMapper.ComputeSettingsHash(settings);
            const bool settingsChanged = true;

            // Project every entity to its Sync DTO once, up front. Sessions dominate the volume, so
            // the push is chunked below by session slices; the (typically small) non-session entities
            // and Settings ride only the first chunk.
            var templateDtos = templates.Where(t => !t.IsBuiltIn).Select(t => _mapper.ToSyncTemplate(t, userId)).ToList();
            // !IsManaged as well as !IsBuiltIn: GetPersonasAsync returns built-ins ∪ managed ∪ user rows, and
            // managed rows are pull-only — an id in personas.upserted would be quarantined server-side, but
            // the client contract is to never emit one. The filter states that invariant at the push site.
            var personaDtos = personas.Where(p => !p.IsBuiltIn && !p.IsManaged).Select(p => _mapper.ToSyncPersona(p, userId)).ToList();
            var providerDtos = providers.Where(p => p.ProviderType != AiProviderType.PiaCloud).Select(p => _mapper.ToSyncProvider(p, userId)).ToList();
            var sessionDtos = sessions.Select(s => _mapper.ToSyncSession(s, userId)).ToList();
            var memoryDtos = memories.Select(m => _mapper.ToSyncMemory(m, userId)).ToList();
            var kanbanDtos = kanbanColumns.Select(c => _mapper.ToSyncKanbanColumn(c, userId)).ToList();
            var todoDtos = todos.Select(t => _mapper.ToSyncTodo(t, userId)).ToList();
            var jobDtos = scheduledJobs.Select(j => _mapper.ToSyncScheduledJob(j, userId)).ToList();

            // Chunk the push into <=FirstSyncBatchSize-session bodies (Sec 6.4) so a large first sync
            // stays well within the 30/60s rate limit and never ships one multi-MB body. At least one
            // batch always runs (so an account with no sessions still pushes its other entities +
            // Settings). Each batch is gzipped via the shared PostPushAsync (server runs
            // UseRequestDecompression), same as the delta push.
            var batchCount = Math.Max(1, (int)Math.Ceiling(sessionDtos.Count / (double)FirstSyncBatchSize));
            var batchSendTimes = new Queue<DateTime>();
            for (var batch = 0; batch < batchCount; batch++)
            {
                await PaceBatchPushAsync(batchSendTimes);

                var isFirstBatch = batch == 0;
                var sessionSlice = sessionDtos.Skip(batch * FirstSyncBatchSize).Take(FirstSyncBatchSize).ToList();

                var request = new SyncPushRequest
                {
                    ClientTimestamp = DateTime.UtcNow,
                    LastSyncTimestamp = DateTime.MinValue,
                    DeviceId = settings.SyncDeviceId,
                    IsE2EEEncrypted = isE2EE,
                    Settings = isFirstBatch && settingsChanged ? _mapper.ToSyncSettings(settings, userId) : null,
                    Templates = new SyncEntityChanges<SyncTemplate> { Upserted = isFirstBatch ? templateDtos : [] },
                    Personas = new SyncEntityChanges<SyncPersona> { Upserted = isFirstBatch ? personaDtos : [] },
                    Providers = new SyncEntityChanges<SyncProvider> { Upserted = isFirstBatch ? providerDtos : [] },
                    Sessions = new SyncSessionChanges { Added = sessionSlice },
                    Memories = new SyncEntityChanges<SyncMemory> { Upserted = isFirstBatch ? memoryDtos : [] },
                    KanbanColumns = new SyncEntityChanges<SyncKanbanColumn> { Upserted = isFirstBatch ? kanbanDtos : [] },
                    Todos = new SyncEntityChanges<SyncTodo> { Upserted = isFirstBatch ? todoDtos : [] },
                    ScheduledJobs = new SyncEntityChanges<SyncScheduledJob> { Upserted = isFirstBatch ? jobDtos : [] }
                };

                using var response = await PostPushAsync(client, serverUrl, request);
                batchSendTimes.Enqueue(DateTime.UtcNow);
                await EnsureSuccessAsync(response, $"First-sync push (batch {batch + 1}/{batchCount})");

                _logger.LogInformation("First-sync push batch {Batch}/{BatchCount} completed (settingsIncluded: {SettingsIncluded}, templates: {Templates}, personas: {Personas}, providers: {Providers}, sessions: {Sessions}, memories: {Memories}, kanbanColumns: {KanbanColumns}, todos: {Todos}, scheduledJobs: {Jobs})",
                    batch + 1, batchCount, request.Settings is not null,
                    request.Templates.Upserted.Count, request.Personas.Upserted.Count, request.Providers.Upserted.Count,
                    request.Sessions.Added.Count, request.Memories.Upserted.Count,
                    request.KanbanColumns.Upserted.Count, request.Todos.Upserted.Count, request.ScheduledJobs.Upserted.Count);
            }

            // Persist the settings hash only after every batch pushed successfully (EnsureSuccessAsync
            // throws otherwise), so a failed push never strands settings behind an advanced hash.
            await PersistSettingsHashIfChangedAsync(settings, settingsHash, settingsChanged);

            _logger.LogInformation("First-sync push completed in {BatchCount} batch(es): {Sessions} sessions, {Templates} templates, {Personas} personas, {Providers} providers, {Memories} memories, {KanbanColumns} kanbanColumns, {Todos} todos, {Jobs} scheduledJobs",
                batchCount, sessionDtos.Count, templateDtos.Count, personaDtos.Count, providerDtos.Count,
                memoryDtos.Count, kanbanDtos.Count, todoDtos.Count, jobDtos.Count);

            // Pull all data from server (including other devices' data)
            var (pulled, decryptErrors, pullOk, serverTimestamp) = await PullChangesAsync(client, serverUrl, settings);
            _logger.LogInformation("First-sync pull: {Pulled} pulled, {Errors} decrypt errors, pullOk={PullOk}", pulled, decryptErrors, pullOk);

            if (pullOk && serverTimestamp.HasValue)
            {
                settings.LastSyncTimestamp = serverTimestamp.Value;
                await _settingsService.SaveSettingsAsync(settings);
            }
            else
            {
                _logger.LogWarning("First-sync pull failed — LastSyncTimestamp NOT updated; next sync will retry full pull");
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Blocks before a first-sync batch send once <see cref="SyncRateLimitPermits"/> minus the
    /// safety margin worth of batches have already been sent within the trailing
    /// <see cref="SyncRateLimitWindow"/>, waiting out the remainder of the window so the sliding
    /// per-user "sync" rate limit is never exhausted mid-migration. No-op for small syncs — <paramref
    /// name="sendTimes"/> never reaches the margin, so this returns immediately without delay.
    /// </summary>
    private static async Task PaceBatchPushAsync(Queue<DateTime> sendTimes)
    {
        var now = DateTime.UtcNow;
        while (sendTimes.Count > 0 && now - sendTimes.Peek() > SyncRateLimitWindow)
            sendTimes.Dequeue();

        const int safeBatchesPerWindow = SyncRateLimitPermits - SyncRateLimitSafetyMargin;
        if (sendTimes.Count < safeBatchesPerWindow)
            return;

        var wait = SyncRateLimitWindow - (now - sendTimes.Peek());
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait);
    }

    /// <summary>
    /// Repairs provider rows a pre-guard E2EE pull blanked, by resetting the sync cursor so the
    /// server's intact ciphertext is fetched again.
    /// </summary>
    /// <remarks>
    /// The tell is unambiguous: a provider typed <see cref="AiProviderType.PiaCloud"/> whose Id is not
    /// the well-known Pia Cloud Id. Sync never carries a real one — the push filters PiaCloud out — so
    /// such a row can only be the all-defaults entity the old plaintext fallback produced from a row
    /// whose plaintext columns the server had blanked.
    ///
    /// Providers are repairable because that same push filter kept the blanked rows from being
    /// uploaded over the server's copies. Templates were not so lucky and are not repairable here.
    /// </remarks>
    public async Task<bool> RepairBlankedSyncRowsAsync()
    {
        if (!_authService.IsLoggedIn) return false;

        var settings = await _settingsService.GetSettingsAsync();
        if (!settings.SyncEnabled || settings.BlankedSyncRowRepairAt is not null)
            return false;

        // Resyncing before onboarding would only hit the pull refusal below. Leave the marker unset
        // so the launch that finally has the UMK still gets its attempt.
        if (settings.IsE2EEEnabled && _e2ee?.IsReady() != true)
            return false;

        var providers = await _providerService.GetProvidersAsync();
        var blanked = providers.Count(p =>
            p.ProviderType == AiProviderType.PiaCloud
            && p.Id != ProviderService.PiaCloudProviderId);

        if (blanked == 0) return false;

        _logger.LogWarning(
            "Found {Count} provider row(s) blanked by an E2EE pull that could not decrypt; forcing a full resync",
            blanked);

        // Mark only once a cycle actually RAN. SyncNowAsync returns null for every reason a cycle can
        // be skipped — no token, server unreachable, another sync holding the lock — and burning the
        // one-shot on one of those would strand the rows blank forever. A cycle that ran and left them
        // blank means the server copy is gone, so there is nothing to retry.
        var result = await ForceFullResyncAsync();
        if (result is null)
        {
            _logger.LogWarning("Repair resync did not run; leaving the repair armed for a later launch");
            return false;
        }

        var latest = await _settingsService.GetSettingsAsync();
        latest.BlankedSyncRowRepairAt = DateTime.UtcNow;
        await _settingsService.SaveSettingsAsync(latest);
        return true;
    }

    public async Task<SyncResult?> ForceFullResyncAsync()
    {
        _logger.LogInformation("ForceFullResyncAsync: resetting LastSyncTimestamp to trigger full pull");
        await _syncLock.WaitAsync();
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            settings.LastSyncTimestamp = null;
            await _settingsService.SaveSettingsAsync(settings);
        }
        finally
        {
            _syncLock.Release();
        }

        return await SyncNowAsync();
    }

    public async Task StopBackgroundSyncAndWaitAsync()
    {
        StopBackgroundSync();
        // Acquire and release the lock to ensure any in-progress sync is done
        await _syncLock.WaitAsync();
        _syncLock.Release();
    }

    /// <summary>
    /// Serializes a push request with the shared camelCase/null-eliding serializer, gzip-compresses
    /// it (the server runs UseRequestDecompression), and POSTs it to /api/sync/push. Shared by the
    /// delta push and the first-sync migration so both use one serializer and one compression path.
    /// The caller owns the returned response (dispose it).
    /// </summary>
    private async Task<HttpResponseMessage> PostPushAsync(HttpClient client, string serverUrl, SyncPushRequest request)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(request, PushSerializerOptions);
        using var compressedStream = new MemoryStream();
        using (var gzipStream = new GZipStream(compressedStream, CompressionLevel.Fastest, leaveOpen: true))
        {
            await gzipStream.WriteAsync(jsonBytes);
        }
        compressedStream.Position = 0;
        var compressionRatio = jsonBytes.Length > 0 ? (int)((1.0 - (double)compressedStream.Length / jsonBytes.Length) * 100) : 0;
        _logger.LogInformation("Push compressed: {OriginalSize}B → {CompressedSize}B ({Ratio}% reduction)",
            jsonBytes.Length, compressedStream.Length, compressionRatio);

        using var content = new StreamContent(compressedStream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        var pushSw = Stopwatch.StartNew();
        var response = await client.PostAsync($"{serverUrl}/api/sync/push", content);
        pushSw.Stop();
        _logger.LogInformation("Push HTTP: {StatusCode} in {ElapsedMs}ms", (int)response.StatusCode, pushSw.ElapsedMilliseconds);
        return response;
    }

    /// <summary>
    /// Pushes local changes and reports the outcome as a (count, succeeded, sent-changes)
    /// tuple so callers can distinguish "nothing to push" from "the POST failed" — both
    /// otherwise collapse to a pushed count of 0, but only the former is safe to treat as an
    /// idle cycle for backoff purposes (see <see cref="ClassifyCycle"/>). <c>SentChanges</c>
    /// is true whenever the short-circuit below was NOT taken and the POST succeeded, so it
    /// also covers a deletes-only or plugin-prefs-only push that <c>PushedCount</c> (upserts
    /// only) would otherwise miss.
    /// </summary>
    private async Task<(int PushedCount, bool PushSucceeded, bool SentChanges)> PushChangesAsync(HttpClient client, string serverUrl, AppSettings settings)
    {
        var lastSync = settings.LastSyncTimestamp ?? DateTime.MinValue;

        var templates = await _templateService.GetTemplatesAsync();
        var personas = _personaService is not null
            ? await _personaService.GetPersonasAsync()
            : [];
        var providers = await _providerService.GetProvidersAsync();

        // Only push sessions created since last sync
        var sessions = await _historyService.SearchSessionsAsync(fromDate: lastSync);

        var memories = await _memoryService.GetAllObjectsAsync();
        var kanbanColumns = _columnService is not null
            ? await _columnService.GetAllAsync()
            : Array.Empty<KanbanColumn>();
        var todos = _todoService is not null
            ? await _todoService.GetAllAsync()
            : [];
        var scheduledJobs = _scheduledJobService is not null
            ? await _scheduledJobService.GetModifiedSinceAsync(lastSync)
            : [];

        var dirtyTemplates = templates.Where(t => !t.IsBuiltIn).Where(t => (t.ModifiedAt ?? t.CreatedAt).ToUniversalTime() >= lastSync).Count();
        // Same !IsManaged filter as the request builder below: this only feeds the diagnostic log line (the
        // push short-circuit reads request.*.Upserted.Count, not these), but a freshly-replaced managed
        // snapshot bumps UpdatedAt on every row, so without it the log would claim N dirty personas on a
        // cycle with nothing pushable and send someone hunting a phantom.
        var dirtyPersonas = personas.Where(p => !p.IsBuiltIn && !p.IsManaged).Where(p => p.UpdatedAt.ToUniversalTime() >= lastSync).Count();
        var dirtyProviders = providers.Where(p => p.ProviderType != AiProviderType.PiaCloud).Where(p => p.UpdatedAt.ToUniversalTime() >= lastSync).Count();
        var dirtySessions = sessions.Count;
        var dirtyMemories = memories.Where(m => m.UpdatedAt.ToUniversalTime() >= lastSync).Count();
        var dirtyKanbanCols = kanbanColumns.Where(c => c.UpdatedAt.ToUniversalTime() >= lastSync).Count();
        var dirtyTodos = todos.Where(t => t.UpdatedAt.ToUniversalTime() >= lastSync).Count();
        _logger.LogInformation("Push dirty tracking: {Templates}T, {Personas}Pe, {Providers}P, {Sessions}S, {Memories}M, {KanbanCols}K, {Todos}Todo, {Jobs}Job changed since {LastSync}",
            dirtyTemplates, dirtyPersonas, dirtyProviders, dirtySessions, dirtyMemories, dirtyKanbanCols, dirtyTodos,
            scheduledJobs.Count, lastSync);

        var isE2EE = _e2ee?.IsReady() == true;
        var userId = isE2EE ? settings.SyncUserId : null;

        var pendingDeletes = _deleteTracker.GetPendingDeletes();

        // Settings-hash gate: omit Settings from the push unless the plaintext projection changed
        // since the last successful push. ToSyncSettings stamps ModifiedAt = UtcNow and (under
        // E2EE) re-encrypts with a fresh DEK/nonce every call, so the payload always differs on
        // the wire — hashing plaintext is the only stable change-signal. The server treats absent
        // Settings as no-change, so omitting them is safe.
        var settingsHash = SyncMapper.ComputeSettingsHash(settings);
        var settingsChanged = settingsHash != settings.LastPushedSettingsHash;

        var request = new SyncPushRequest
        {
            ClientTimestamp = DateTime.UtcNow,
            LastSyncTimestamp = lastSync,
            DeviceId = settings.SyncDeviceId,
            IsE2EEEncrypted = isE2EE,
            Settings = settingsChanged ? _mapper.ToSyncSettings(settings, userId) : null,
            Templates = new SyncEntityChanges<SyncTemplate>
            {
                Upserted = templates
                    .Where(t => !t.IsBuiltIn)
                    .Where(t => (t.ModifiedAt ?? t.CreatedAt).ToUniversalTime() >= lastSync)
                    .Select(t => _mapper.ToSyncTemplate(t, userId))
                    .ToList(),
                Deleted = pendingDeletes.GetValueOrDefault("templates", [])
            },
            Personas = new SyncEntityChanges<SyncPersona>
            {
                Upserted = personas
                    // !IsManaged as well as !IsBuiltIn: GetPersonasAsync returns built-ins ∪ managed ∪ user
                    // rows, and managed personas are pull-only. The server quarantines a managed id it
                    // receives here, but the client contract is to never emit one — this filter is where
                    // that invariant is stated.
                    .Where(p => !p.IsBuiltIn && !p.IsManaged)
                    .Where(p => p.UpdatedAt.ToUniversalTime() >= lastSync)
                    .Select(p => _mapper.ToSyncPersona(p, userId))
                    .ToList(),
                Deleted = pendingDeletes.GetValueOrDefault("personas", [])
            },
            Providers = new SyncEntityChanges<SyncProvider>
            {
                Upserted = providers
                    .Where(p => p.ProviderType != AiProviderType.PiaCloud)
                    .Where(p => p.UpdatedAt.ToUniversalTime() >= lastSync)
                    .Select(p => _mapper.ToSyncProvider(p, userId))
                    .ToList(),
                Deleted = pendingDeletes.GetValueOrDefault("providers", [])
            },
            Sessions = new SyncSessionChanges
            {
                Added = sessions
                    .Select(s => _mapper.ToSyncSession(s, userId))
                    .ToList()
            },
            Memories = new SyncEntityChanges<SyncMemory>
            {
                Upserted = memories
                    .Where(m => m.UpdatedAt.ToUniversalTime() >= lastSync)
                    .Select(m => _mapper.ToSyncMemory(m, userId))
                    .ToList(),
                Deleted = pendingDeletes.GetValueOrDefault("memories", [])
            },
            KanbanColumns = new SyncEntityChanges<SyncKanbanColumn>
            {
                Upserted = kanbanColumns
                    .Where(c => c.UpdatedAt.ToUniversalTime() >= lastSync)
                    .Select(c => _mapper.ToSyncKanbanColumn(c, userId))
                    .ToList(),
                Deleted = pendingDeletes.GetValueOrDefault("kanbanColumns", [])
            },
            Todos = new SyncEntityChanges<SyncTodo>
            {
                Upserted = todos
                    .Where(t => t.UpdatedAt.ToUniversalTime() >= lastSync)
                    .Select(t => _mapper.ToSyncTodo(t, userId))
                    .ToList(),
                Deleted = pendingDeletes.GetValueOrDefault("todos", [])
            },
            ScheduledJobs = new SyncEntityChanges<SyncScheduledJob>
            {
                Upserted = scheduledJobs
                    .Select(j => _mapper.ToSyncScheduledJob(j, userId))
                    .ToList(),
                Deleted = pendingDeletes.GetValueOrDefault("scheduledJobs", [])
            },
            // Research sessions are no longer produced by the client (results are assistant chats).
            // The field is kept empty for server wire-contract compatibility.
            ResearchSessions = new SyncEntityChanges<SyncResearchSession>(),
            PluginPreferences = _pluginService?.GetPendingPreferenceChanges() ?? []
        };

        _logger.LogInformation("Push pending deletes: {Templates}T, {Personas}Pe, {Providers}P, {Memories}M, {Todos}Todo, {KanbanCols}K, {Jobs}Job",
            request.Templates.Deleted.Count, request.Personas.Deleted.Count, request.Providers.Deleted.Count,
            request.Memories.Deleted.Count, request.Todos.Deleted.Count,
            request.KanbanColumns.Deleted.Count,
            request.ScheduledJobs.Deleted.Count);

        var pushedCount = request.Templates.Upserted.Count
            + request.Personas.Upserted.Count
            + request.Providers.Upserted.Count
            + request.Sessions.Added.Count
            + request.Memories.Upserted.Count
            + request.KanbanColumns.Upserted.Count
            + request.Todos.Upserted.Count
            + request.ScheduledJobs.Upserted.Count;

        _logger.LogInformation(
            "Push request — Templates: {Templates}, Personas: {Personas}, Providers: {Providers}, Sessions: {Sessions}, Memories: {Memories}, KanbanColumns: {KanbanColumns}, Todos: {Todos}, ScheduledJobs: {Jobs}, LastSync: {LastSync}, DeviceId: {DeviceId}, IsE2EE: {IsE2EE}",
            request.Templates.Upserted.Count, request.Personas.Upserted.Count, request.Providers.Upserted.Count,
            request.Sessions.Added.Count, request.Memories.Upserted.Count,
            request.KanbanColumns.Upserted.Count, request.Todos.Upserted.Count,
            request.ScheduledJobs.Upserted.Count,
            request.LastSyncTimestamp, request.DeviceId, request.IsE2EEEncrypted);

        // Short-circuit: skip HTTP POST when there are no changes to push. Plugin prefs are
        // NOT in pushedCount, so they must be checked explicitly — otherwise a prefs-only
        // change is short-circuited away and (with peek semantics) never leaves the device.
        // A settings-only change also isn't in pushedCount/deletes/prefs, so it must be counted
        // here too, or a genuine settings edit would be short-circuited away and never sync.
        if (pushedCount == 0
            && pendingDeletes.Values.All(v => v.Count == 0)
            && request.PluginPreferences.Count == 0
            && !settingsChanged)
        {
            _logger.LogInformation("Push short-circuited: no changes to push");
            return (0, true, false);
        }

        using var response = await PostPushAsync(client, serverUrl, request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Push failed with status {Status}", response.StatusCode);
            _logger.SensitiveDebug("Push failure body: {Body}", body);
            if ((int)response.StatusCode == 403 && body.Contains("e2ee_required"))
            {
                // The account is E2EE-enabled server-side but this client pushed
                // plaintext (no local UMK / stale settings). Silent retries can never
                // succeed — route into device onboarding instead.
                _logger.LogWarning("Server requires E2EE for this account; onboarding required");
                NotifyE2EEOnboardingRequired();
            }
            // Failed, not idle: local changes are still pending and must not be treated as
            // "nothing to push" by ClassifyCycle, or persistent failures would engage backoff
            // and slow the retry of unpushed data.
            return (0, false, false);
        }

        var pushResponse = await response.Content.ReadFromJsonAsync<SyncPushResponse>();
        if (pushResponse is not null && pushResponse.Conflicts.Count > 0)
        {
            _logger.LogWarning("Push returned {ConflictCount} conflict(s)", pushResponse.Conflicts.Count);
            foreach (var c in pushResponse.Conflicts)
                _logger.LogWarning("Push conflict: {Entity} {Id}", c.Entity, c.Id);
        }

        _deleteTracker.ClearAfterSuccessfulPush();
        // Only now that the push succeeded is it safe to drop the pending plugin prefs; a
        // 403/failure/short-circuit above returned without reaching here, so they persist.
        _pluginService?.ClearPreferenceChangesAfterSuccessfulPush();

        // Persist the settings hash only after a successful push (mirrors the cursor save) —
        // never before, or a failed push would strand the settings behind an advanced hash and
        // they'd never re-sync.
        await PersistSettingsHashIfChangedAsync(settings, settingsHash, settingsChanged);

        return (pushedCount, true, true);
    }

    /// <summary>
    /// Persists <see cref="AppSettings.LastPushedSettingsHash"/> after a successful push, shared
    /// by the delta and first-sync push paths. No-ops when the settings did not change, so a
    /// push that omitted Settings never rewrites an already-current hash.
    /// </summary>
    private async Task PersistSettingsHashIfChangedAsync(AppSettings settings, string settingsHash, bool settingsChanged)
    {
        if (!settingsChanged) return;

        settings.LastPushedSettingsHash = settingsHash;
        await _settingsService.SaveSettingsAsync(settings);
    }

    // Pull page size (Sec 6.3): caps each collection per response so a large first delta (many
    // sessions) streams in bounded chunks instead of one huge body. Opt-in — a pre-upgrade server
    // ignores the param and returns everything in one page (HasMore stays null, the loop runs once).
    private const int PullPageLimit = 500;
    // Hard ceiling on drain iterations so a server that keeps reporting HasMore without advancing the
    // cursor can never spin forever.
    private const int MaxPullPages = 1000;

    /// <summary>
    /// Pulls remote changes, draining the opt-in <c>?limit=</c> pagination (Sec 6.3): each page is
    /// applied, and while the server reports <see cref="SyncPullResponse.HasMore"/> the loop re-pulls
    /// with the advanced <c>since</c> cursor (the continuation token). The returned tuple reports the
    /// aggregate across all drained pages; the returned <c>ServerTimestamp</c> is the last page's, so
    /// the caller advances the cursor past everything drained.
    ///
    /// Server contract this drain depends on (not verifiable from this repo — see the Pia-server
    /// unit that implements <c>?limit=</c>): on a <c>HasMore=true</c> page, <c>ServerTimestamp</c>
    /// must be the max <c>SyncedAt</c> actually included in that page (never "server now"), and
    /// pagination must never split rows that share the same <c>SyncedAt</c> across two pages.
    /// Otherwise advancing <c>since</c> to it — including the later-page-drain-failure path below,
    /// which advances the cursor past only the pages successfully applied so far — would silently
    /// and permanently skip un-drained rows.
    /// </summary>
    private async Task<(int Pulled, int DecryptionErrors, bool PullSucceeded, DateTime? ServerTimestamp)> PullChangesAsync(HttpClient client, string serverUrl, AppSettings settings)
    {
        var lastSync = settings.LastSyncTimestamp ?? DateTime.MinValue;
        // Ensure UTC Kind so ToString("O") includes the Z suffix — prevents Npgsql
        // timestamptz comparison failures when the server uses PostgreSQL.
        if (lastSync.Kind != DateTimeKind.Utc)
            lastSync = DateTime.SpecifyKind(lastSync, DateTimeKind.Utc);

        var totalPulled = 0;
        var totalDecryptErrors = 0;
        DateTime? lastServerTimestamp = null;

        for (var page = 0; page < MaxPullPages; page++)
        {
            // Only the first page is conditional / stores the ETag+catalog version: it is the one
            // request whose `since` equals the persisted cursor, so it is the only representation the
            // stored conditional-GET metadata can describe. Drain pages use a moving `since`.
            var pageResult = await PullPageAsync(client, serverUrl, settings, lastSync, isFirstPage: page == 0);

            if (pageResult.NotModified)
                return (0, 0, true, null); // only reachable on the first page

            if (!pageResult.PullSucceeded)
            {
                // First-page failure => nothing applied, keep the cursor. A later-page failure means
                // earlier pages were already applied: advance the cursor to the last drained page so
                // the next sync resumes after them instead of re-pulling from the start.
                if (page == 0)
                    return (0, 0, false, null);
                _logger.LogWarning("Pull drain failed on page {Page}; keeping earlier pages and advancing cursor", page);
                return (totalPulled, totalDecryptErrors, true, lastServerTimestamp);
            }

            totalPulled += pageResult.Pulled;
            totalDecryptErrors += pageResult.DecryptionErrors;
            lastServerTimestamp = pageResult.ServerTimestamp;

            // Continue draining only while the server both flags more data AND advances the cursor
            // (the `since` continuation token). A non-advancing cursor with HasMore would loop, so the
            // strict `>` guard (plus MaxPullPages) makes runaway pagination impossible.
            if (pageResult.HasMore && pageResult.ServerTimestamp is DateTime ts && ts > lastSync)
            {
                lastSync = DateTime.SpecifyKind(ts, DateTimeKind.Utc);
                continue;
            }
            break;
        }

        return (totalPulled, totalDecryptErrors, true, lastServerTimestamp);
    }

    /// <summary>
    /// Requests and applies a single pull page. Returns the page outcome including
    /// <c>HasMore</c> so the <see cref="PullChangesAsync"/> drain loop knows whether to continue.
    /// </summary>
    /// <summary>
    /// True when the page holds at least one E2EE row and this client is not in a state to decrypt it.
    /// </summary>
    private bool CarriesUnreadableCiphertext(SyncPullResponse pull, string? userId)
    {
        if (_e2ee?.IsReady() == true && userId is not null)
            return false;

        return pull.Settings?.EncryptedPayload is not null
            || pull.Templates.Upserted.Any(x => x.EncryptedPayload is not null)
            || pull.Personas.Upserted.Any(x => x.EncryptedPayload is not null)
            || pull.Providers.Upserted.Any(x => x.EncryptedPayload is not null)
            || pull.Sessions.Added.Any(x => x.EncryptedPayload is not null)
            || pull.Memories.Upserted.Any(x => x.EncryptedPayload is not null)
            || pull.Todos.Upserted.Any(x => x.EncryptedPayload is not null)
            || pull.KanbanColumns.Upserted.Any(x => x.EncryptedPayload is not null)
            || pull.ScheduledJobs.Upserted.Any(x => x.EncryptedPayload is not null);
    }

    private async Task<(bool NotModified, bool PullSucceeded, int Pulled, int DecryptionErrors, DateTime? ServerTimestamp, bool HasMore)> PullPageAsync(HttpClient client, string serverUrl, AppSettings settings, DateTime sinceUtc, bool isFirstPage)
    {
        var since = sinceUtc.ToString("O");

        // Catalog first-run rule, one flag per catalog channel: exactly one pull with BOTH conditional
        // mechanisms disabled, to force a full catalog snapshot. Needed because a build that predates a
        // channel still stored the catalogVersion it arrived with, so this profile can be echoing an
        // already-current token while holding no managed rows and no group policy at all — the server
        // would fast-skip the catalog forever. Same hole after a profile reset or DB rebuild. Both
        // mechanisms have to go: ?catalogVersion= gates the catalog block, If-None-Match gates the entire
        // body (a 304 carries neither managedPersonas nor clientPolicy).
        var forceFullCatalog = isFirstPage
            && (!settings.ManagedPersonaStoreInitialized || !settings.ClientPolicyInitialized);

        // ?limit caps each collection (Sec 6.3); ?catalogVersion lets the server skip re-sending the
        // full plugin catalog when it is unchanged (Sec 3.5). Both are opt-in — a pre-upgrade server
        // ignores them. catalogVersion is omitted on first run (null) so the server sends the full catalog.
        var pullUrl = $"{serverUrl}/api/sync/pull?since={since}&limit={PullPageLimit}";
        if (settings.LastCatalogVersion.HasValue && !forceFullCatalog)
            pullUrl += $"&catalogVersion={settings.LastCatalogVersion.Value}";
        _logger.LogInformation("Pull requesting: {Url}", SafeUrl.Format(pullUrl));

        var pullRequest = new HttpRequestMessage(HttpMethod.Get, pullUrl);
        // TryParse (not the EntityTagHeaderValue ctor) so a weak (W/"...") stored ETag never
        // throws FormatException and aborts the whole pull cycle — see AssistantChatSyncService's
        // identical guard for the chat ETag. Only the first page is conditional (see PullChangesAsync).
        if (isFirstPage && !forceFullCatalog && !string.IsNullOrEmpty(settings.LastPullETag) && EntityTagHeaderValue.TryParse(settings.LastPullETag, out var lastPullTag))
            pullRequest.Headers.IfNoneMatch.Add(lastPullTag);

        if (forceFullCatalog)
            _logger.LogInformation(
                "Pull forced unconditional: a catalog channel is uninitialized (managed personas: {Personas}, client policy: {Policy})",
                settings.ManagedPersonaStoreInitialized, settings.ClientPolicyInitialized);

        var pullSw = Stopwatch.StartNew();
        var response = await client.SendAsync(pullRequest);
        pullSw.Stop();
        _logger.LogInformation("Pull HTTP: {StatusCode} in {ElapsedMs}ms", (int)response.StatusCode, pullSw.ElapsedMilliseconds);

        if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
        {
            _logger.LogDebug("Pull returned 304 Not Modified — no changes since last sync");
            return (true, true, 0, 0, null, false);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Pull failed with status {Status}", response.StatusCode);
            return (false, false, 0, 0, null, false);
        }

        // Read the response ETag here but PERSIST IT ONLY AFTER EVERY APPLY BELOW SUCCEEDS — it rides the
        // same save as LastCatalogVersion, for a stronger version of the same reason. The ETag is
        // "v{userDataVersion}-c{catalogVersion}-s{sinceTicks}" and a page whose apply throws advances none
        // of the three (LastSyncTimestamp is only advanced by a completed pull), so storing it up here
        // would make the next pull echo a string the server recomputes identically and answer 304: the
        // un-applied page could never be re-fetched, and for the replace-all managed channel that means a
        // withdrawn persona stays in the store indefinitely — exactly what replace-all exists to prevent.
        // Keeping the OLD ETag on failure makes the retry unconditional: it mismatched once (that is why
        // this response is a 200 and not a 304), so it mismatches again. Same shape as
        // AssistantChatSyncService's LastChatPullETag, whose comment already claims to mirror this one.
        var newPullETag = isFirstPage && response.Headers.ETag is not null
            ? response.Headers.ETag.ToString()
            : null;

        var pullResponse = await response.Content.ReadFromJsonAsync<SyncPullResponse>();
        if (pullResponse is null) return (false, false, 0, 0, null, false);

        // The server's current catalog version is applied further down, only after the page's
        // entities have been fully applied (see the persist just before the return below). The
        // catalog query is gated solely on ?catalogVersion= with no SyncedAt filter (Sec 3.5), so
        // storing it here — before the apply step below has run — would let an apply exception
        // (thrown by any entity service and propagated out of this method) strand a stored
        // version that makes the next pull's ?catalogVersion= match the server's and skip resending
        // the very plugin changes this page never actually applied.
        var newCatalogVersion = isFirstPage && pullResponse.CatalogVersion.HasValue
            && pullResponse.CatalogVersion != settings.LastCatalogVersion
            ? pullResponse.CatalogVersion
            : null;

        // Managed personas are logged as COUNTS ONLY — a name, tagline or prompt is admin-authored user
        // content and must never reach a support log (CLAUDE.md privacy-first logging). 0u/0d therefore
        // reads the same whether the key was absent or present-and-empty; the two are distinguished at the
        // apply site below, not here.
        _logger.LogInformation(
            "Pull response — ServerTimestamp: {ServerTs}, Templates: {TU}u/{TD}d, Personas: {PeU}u/{PeD}d, Providers: {PU}u/{PD}d, Sessions: {SA}a/{SD}d, Memories: {MU}u/{MD}d, KanbanColumns: {KCU}u/{KCD}d, Todos: {ToU}u/{ToD}d, Plugins: {PlU}u/{PlD}d, ManagedPersonas: {MpU}u/{MpD}d, ClientPolicy: {PolicyPresent}",
            pullResponse.ServerTimestamp,
            pullResponse.Templates.Upserted.Count, pullResponse.Templates.Deleted.Count,
            pullResponse.Personas.Upserted.Count, pullResponse.Personas.Deleted.Count,
            pullResponse.Providers.Upserted.Count, pullResponse.Providers.Deleted.Count,
            pullResponse.Sessions.Added.Count, pullResponse.Sessions.Deleted.Count,
            pullResponse.Memories.Upserted.Count, pullResponse.Memories.Deleted.Count,
            pullResponse.KanbanColumns.Upserted.Count, pullResponse.KanbanColumns.Deleted.Count,
            pullResponse.Todos.Upserted.Count, pullResponse.Todos.Deleted.Count,
            pullResponse.Plugins.Upserted.Count, pullResponse.Plugins.Deleted.Count,
            pullResponse.ManagedPersonas?.Personas.Count ?? 0,
            pullResponse.ManagedPersonas?.RecentlyRemoved.Count ?? 0,
            pullResponse.ClientPolicy is not null);

        var userId = settings.SyncUserId;

        // Refuse the whole page rather than apply any of it, when it carries ciphertext this client
        // cannot read. The server blanks the plaintext columns of an E2EE row, so applying one row at
        // a time would write empty entities over real data — and because the cursor advances on a
        // successful pull, those rows would never be fetched again. Reporting the page as failed keeps
        // LastSyncTimestamp where it is, so the pull retries intact once onboarding completes.
        if (CarriesUnreadableCiphertext(pullResponse, userId))
        {
            _logger.LogError(
                "Pull page carries E2EE ciphertext but this client cannot decrypt it "
                + "(e2eeReady: {Ready}, userId present: {HasUserId}) — refusing the page and keeping the sync cursor",
                _e2ee?.IsReady() == true, userId is not null);
            NotifyE2EEOnboardingRequired();
            return (false, false, 0, 0, null, false);
        }

        var decryptionErrors = 0;
        var mergeInserted = 0;
        var mergeUpdated = 0;
        var mergeSkipped = 0;
        var mergeDeleted = 0;

        var settingsApplied = false;
        var providersApplied = false;

        // Apply settings
        if (pullResponse.Settings is not null)
        {
            try
            {
                var currentSettings = await _settingsService.GetSettingsAsync();
                _mapper.ApplySyncSettings(pullResponse.Settings, currentSettings, userId);
                await _settingsService.SaveSettingsAsync(currentSettings);
                settingsApplied = true;
                _logger.LogInformation("Imported synced settings");
            }
            catch (CryptographicException ex)
            {
                decryptionErrors++;
                _logger.LogWarning(ex, "Failed to decrypt synced settings; skipping");
            }
        }

        // Apply templates
        foreach (var template in pullResponse.Templates.Upserted)
        {
            try
            {
                var local = _mapper.FromSyncTemplate(template, userId);
                var existing = (await _templateService.GetTemplatesAsync())
                    .FirstOrDefault(t => t.Id == template.Id);

                if (existing is not null)
                {
                    if (existing.IsBuiltIn)
                    {
                        mergeSkipped++;
                        _logger.LogDebug("Skipped template {Id}: built-in templates cannot be updated via sync", template.Id);
                        continue;
                    }

                    var remoteTime = (local.ModifiedAt ?? local.CreatedAt).ToUniversalTime();
                    var localTime = (existing.ModifiedAt ?? existing.CreatedAt).ToUniversalTime();

                    if (remoteTime >= localTime)
                    {
                        await _templateService.UpdateTemplateAsync(local);
                        mergeUpdated++;
                        _logger.LogInformation("Updated template {Id}", template.Id);
                        _logger.SensitiveDebug("Updated template {Id} name: {Name}", template.Id, local.Name);
                    }
                    else
                    {
                        mergeSkipped++;
                        _logger.LogDebug("Skipped template {Id}: local is newer (local={Local}, remote={Remote})",
                            template.Id, localTime, remoteTime);
                    }
                }
                else
                {
                    await _templateService.AddTemplateAsync(local);
                    mergeInserted++;
                    _logger.LogInformation("Imported template {Id}", template.Id);
                    _logger.SensitiveDebug("Imported template {Id} name: {Name}", template.Id, local.Name);
                }
            }
            catch (CryptographicException ex)
            {
                decryptionErrors++;
                _logger.LogWarning(ex, "Failed to decrypt synced template {Id}; skipping", template.Id);
            }
        }

        foreach (var deletedId in pullResponse.Templates.Deleted)
        {
            _logger.LogDebug("Pull deleted: {EntityType} {Id}", "templates", deletedId);
            await _templateService.DeleteTemplateAsync(deletedId);
            mergeDeleted++;
        }
        if (pullResponse.Templates.Deleted.Count > 0)
            _logger.LogInformation("Pull {EntityType} deletions applied: {Count}", "templates", pullResponse.Templates.Deleted.Count);

        // Apply personas — skip built-ins, last-write-wins on UpdatedAt (mirrors templates).
        if (_personaService is not null)
        {
            foreach (var persona in pullResponse.Personas.Upserted)
            {
                try
                {
                    var local = _mapper.FromSyncPersona(persona, userId);
                    var existing = (await _personaService.GetPersonasAsync())
                        .FirstOrDefault(p => p.Id == persona.Id);

                    if (existing is not null)
                    {
                        // IsReadOnly, not IsBuiltIn: GetPersonasAsync also returns managed rows now, and
                        // UpdatePersonaAsync THROWS on a managed id. Without this, a user persona whose id
                        // collided with a managed one would abort the whole pull (the catch below only
                        // handles CryptographicException) on every cycle, forever.
                        if (existing.IsReadOnly)
                        {
                            mergeSkipped++;
                            _logger.LogDebug("Skipped persona {Id}: built-in and managed personas cannot be updated via sync", persona.Id);
                            continue;
                        }

                        var remoteTime = local.UpdatedAt.ToUniversalTime();
                        var localTime = existing.UpdatedAt.ToUniversalTime();

                        if (remoteTime >= localTime)
                        {
                            await _personaService.UpdatePersonaAsync(local);
                            mergeUpdated++;
                            _logger.LogInformation("Updated persona {Id}", persona.Id);
                            _logger.SensitiveDebug("Updated persona {Id} name: {Name}", persona.Id, local.Name);
                        }
                        else
                        {
                            mergeSkipped++;
                            _logger.LogDebug("Skipped persona {Id}: local is newer (local={Local}, remote={Remote})",
                                persona.Id, localTime, remoteTime);
                        }
                    }
                    else
                    {
                        await _personaService.AddPersonaAsync(local);
                        mergeInserted++;
                        _logger.LogInformation("Imported persona {Id}", persona.Id);
                        _logger.SensitiveDebug("Imported persona {Id} name: {Name}", persona.Id, local.Name);
                    }
                }
                catch (CryptographicException ex)
                {
                    decryptionErrors++;
                    _logger.LogWarning(ex, "Failed to decrypt synced persona {Id}; skipping", persona.Id);
                }
            }

            foreach (var deletedId in pullResponse.Personas.Deleted)
            {
                _logger.LogDebug("Pull deleted: {EntityType} {Id}", "personas", deletedId);
                await _personaService.DeletePersonaAsync(deletedId);
                mergeDeleted++;
            }
            if (pullResponse.Personas.Deleted.Count > 0)
                _logger.LogInformation("Pull {EntityType} deletions applied: {Count}", "personas", pullResponse.Personas.Deleted.Count);
        }

        // Apply providers — match by Id first, fall back to content fingerprint
        // so providers created independently on two devices (each with their own
        // Guid) collapse into one row instead of duplicating.
        var localProviders = (await _providerService.GetProvidersAsync()).ToList();
        var localByFingerprint = new Dictionary<string, AiProvider>(StringComparer.Ordinal);
        foreach (var p in localProviders)
        {
            if (p.Id == ProviderService.PiaCloudProviderId) continue;
            var fp = ProviderFingerprint.Compute(p);
            if (fp == ProviderFingerprint.PiaCloudSentinel) continue;
            // If two locals share a fingerprint, the most-recently-updated wins as the survivor;
            // the others get cleaned up by ConsolidateLocalDuplicatesAsync at startup.
            if (!localByFingerprint.TryGetValue(fp, out var current)
                || p.UpdatedAt.ToUniversalTime() > current.UpdatedAt.ToUniversalTime())
            {
                localByFingerprint[fp] = p;
            }
        }

        foreach (var provider in pullResponse.Providers.Upserted)
        {
            try
            {
                var local = _mapper.FromSyncProvider(provider, userId);
                var existing = await _providerService.GetProviderAsync(provider.Id);

                if (existing is not null)
                {
                    if (local.UpdatedAt.ToUniversalTime() >= existing.UpdatedAt.ToUniversalTime())
                    {
                        // API keys never travel plaintext (device-local without E2EE);
                        // under E2EE the mapper has already placed the synced key on
                        // local.EncryptedApiKey and UpdateProviderAsync honors it.
                        // The six fields SyncProvider omits are NOT on the wire in either mode, so
                        // `local` carries only C# defaults for them — carry this device's values over
                        // or the pull resets them (compaction budget -> null, streaming -> true).
                        _mapper.PreserveDeviceLocalProviderFields(local, existing);
                        await _providerService.UpdateProviderAsync(local);
                        mergeUpdated++;
                        _logger.LogInformation("Updated provider {Id}", provider.Id);
                        _logger.SensitiveDebug("Updated provider {Id} name: {Name}", provider.Id, local.Name);
                    }
                    else
                    {
                        mergeSkipped++;
                        _logger.LogDebug("Skipped provider {Id}: local is newer (local={Local}, remote={Remote})",
                            provider.Id, existing.UpdatedAt, local.UpdatedAt);
                    }
                    continue;
                }

                // Fingerprint match against a different local Id => reassign rather than insert.
                var fingerprint = ProviderFingerprint.Compute(local);
                if (fingerprint != ProviderFingerprint.PiaCloudSentinel
                    && localByFingerprint.TryGetValue(fingerprint, out var dup)
                    && dup.Id != provider.Id
                    && dup.Id != ProviderService.PiaCloudProviderId)
                {
                    // Server Id wins as the canonical identifier. Content from whichever
                    // side has the later UpdatedAt wins; UpdatedAt is set to the max
                    // so the next push does not regress the server row.
                    // Same preserve as the update branch above: `dup` is this device's row and holds the
                    // device-local fields, `local` holds only defaults for them. Doing it before the
                    // ternary covers both outcomes — when `dup` wins it already has them.
                    _mapper.PreserveDeviceLocalProviderFields(local, dup);
                    var serverNewer = local.UpdatedAt.ToUniversalTime()
                        >= dup.UpdatedAt.ToUniversalTime();
                    var merged = serverNewer ? local : dup;
                    merged.UpdatedAt = local.UpdatedAt.ToUniversalTime() > dup.UpdatedAt.ToUniversalTime()
                        ? local.UpdatedAt
                        : dup.UpdatedAt;

                    _logger.LogInformation(
                        "Provider fingerprint match: reassigning local {OldId} -> server {NewId}",
                        dup.Id, provider.Id);
                    await _providerService.ReassignProviderIdAsync(dup.Id, provider.Id, merged);
                    localByFingerprint[fingerprint] = merged;
                    mergeUpdated++;
                    continue;
                }

                // Plaintext wire keys are ignored (device-local policy); an E2EE-synced
                // key is already on local.EncryptedApiKey and survives AddProviderAsync.
                await _providerService.AddProviderAsync(local, null);
                if (fingerprint != ProviderFingerprint.PiaCloudSentinel)
                    localByFingerprint[fingerprint] = local;
                mergeInserted++;
                _logger.LogInformation("Imported provider {Id}", provider.Id);
                _logger.SensitiveDebug("Imported provider {Id} name: {Name}", provider.Id, local.Name);
            }
            catch (CryptographicException ex)
            {
                decryptionErrors++;
                _logger.LogWarning(ex, "Failed to decrypt synced provider {Id}; skipping", provider.Id);
            }
        }

        foreach (var deletedId in pullResponse.Providers.Deleted)
        {
            _logger.LogDebug("Pull deleted: {EntityType} {Id}", "providers", deletedId);
            await _providerService.DeleteProviderAsync(deletedId);
            mergeDeleted++;
        }
        if (pullResponse.Providers.Deleted.Count > 0)
            _logger.LogInformation("Pull {EntityType} deletions applied: {Count}", "providers", pullResponse.Providers.Deleted.Count);

        providersApplied = pullResponse.Providers.Upserted.Count > 0
            || pullResponse.Providers.Deleted.Count > 0;

        // Heal mode-defaults whose Ids may have been reassigned or that now point
        // at providers the pull removed. Cheap idempotent pass — only writes when
        // it actually finds something to fix.
        if (providersApplied || settingsApplied)
        {
            try { await _providerService.RepairModeDefaultsAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RepairModeDefaultsAsync failed after pull; continuing");
            }
        }

        // Apply sessions (append-only)
        foreach (var session in pullResponse.Sessions.Added)
        {
            try
            {
                var local = _mapper.FromSyncSession(session, userId);
                var existing = await _historyService.GetSessionAsync(session.Id);
                if (existing is null)
                {
                    await _historyService.AddSessionAsync(local);
                    mergeInserted++;
                    _logger.LogInformation("Imported session {Id}", session.Id);
                }
                else
                {
                    mergeSkipped++;
                }
            }
            catch (CryptographicException ex)
            {
                decryptionErrors++;
                _logger.LogWarning(ex, "Failed to decrypt synced session {Id}; skipping", session.Id);
            }
        }

        foreach (var deletedId in pullResponse.Sessions.Deleted)
        {
            _logger.LogDebug("Pull deleted: {EntityType} {Id}", "sessions", deletedId);
            await _historyService.DeleteSessionAsync(deletedId);
            mergeDeleted++;
        }
        if (pullResponse.Sessions.Deleted.Count > 0)
            _logger.LogInformation("Pull {EntityType} deletions applied: {Count}", "sessions", pullResponse.Sessions.Deleted.Count);

        // Apply memories
        foreach (var memory in pullResponse.Memories.Upserted)
        {
            try
            {
                var local = _mapper.FromSyncMemory(memory, userId);
                var existing = await _memoryService.GetObjectAsync(memory.Id);

                if (existing is not null)
                {
                    if (local.UpdatedAt.ToUniversalTime() >= existing.UpdatedAt.ToUniversalTime())
                    {
                        await _memoryService.UpdateObjectDataAsync(local.Id, local.Label, local.Data);
                        mergeUpdated++;
                        _logger.LogInformation("Updated memory {Id}: {Label}", memory.Id, local.Label);
                    }
                    else
                    {
                        mergeSkipped++;
                        _logger.LogDebug("Skipped memory {Id}: local is newer (local={Local}, remote={Remote})",
                            memory.Id, existing.UpdatedAt, local.UpdatedAt);
                    }
                }
                else
                {
                    await _memoryService.ImportObjectAsync(local);
                    mergeInserted++;
                    _logger.LogInformation("Imported memory {Id}: {Label}", memory.Id, local.Label);
                }
            }
            catch (CryptographicException ex)
            {
                decryptionErrors++;
                _logger.LogWarning(ex, "Failed to decrypt synced memory {Id}; skipping", memory.Id);
            }
        }

        foreach (var deletedId in pullResponse.Memories.Deleted)
        {
            _logger.LogDebug("Pull deleted: {EntityType} {Id}", "memories", deletedId);
            await _memoryService.DeleteObjectAsync(deletedId);
            mergeDeleted++;
        }
        if (pullResponse.Memories.Deleted.Count > 0)
            _logger.LogInformation("Pull {EntityType} deletions applied: {Count}", "memories", pullResponse.Memories.Deleted.Count);

        // Apply kanban columns (BEFORE todos, since todos reference columns)
        if (_columnService is not null)
        {
            foreach (var syncColumn in pullResponse.KanbanColumns.Upserted)
            {
                try
                {
                    var local = _mapper.FromSyncKanbanColumn(syncColumn, userId);
                    var existing = await _columnService.GetAsync(syncColumn.Id);

                    if (existing is not null)
                    {
                        if (local.UpdatedAt.ToUniversalTime() >= existing.UpdatedAt.ToUniversalTime())
                        {
                            await _columnService.ImportAsync(local);
                            mergeUpdated++;
                            _logger.LogInformation("Updated kanban column {Id}", syncColumn.Id);
                            _logger.SensitiveDebug("Updated kanban column {Id} name: {Name}", syncColumn.Id, local.Name);
                        }
                        else
                        {
                            mergeSkipped++;
                            _logger.LogDebug("Skipped kanban column {Id}: local is newer (local={Local}, remote={Remote})",
                                syncColumn.Id, existing.UpdatedAt, local.UpdatedAt);
                        }
                    }
                    else
                    {
                        await _columnService.ImportAsync(local);
                        mergeInserted++;
                    }
                }
                catch (CryptographicException ex)
                {
                    _logger.LogWarning(ex, "Failed to decrypt synced kanban column {Id}; skipping", syncColumn.Id);
                }
            }

            // Note: we don't process KanbanColumns.Deleted since column deletion
            // is only allowed for empty columns and is enforced client-side
        }

        // Apply todos
        if (_todoService is not null)
        {
            foreach (var todo in pullResponse.Todos.Upserted)
            {
                try
                {
                    var local = _mapper.FromSyncTodo(todo, userId);

                    // Backward compat: assign column based on status if no ColumnId
                    if (!local.ColumnId.HasValue && _columnService is not null)
                    {
                        if (local.Status == TodoStatus.Completed)
                        {
                            var closedCol = await _columnService.GetClosedColumnAsync();
                            local.ColumnId = closedCol.Id;
                        }
                        else
                        {
                            var defaultCol = await _columnService.GetDefaultViewColumnAsync();
                            local.ColumnId = defaultCol.Id;
                        }
                    }

                    var existing = await _todoService.GetAsync(todo.Id);

                    if (existing is not null)
                    {
                        if (local.UpdatedAt.ToUniversalTime() >= existing.UpdatedAt.ToUniversalTime())
                        {
                            await _todoService.ImportAsync(local);
                            mergeUpdated++;
                            _logger.LogInformation("Updated todo {Id}", todo.Id);
                            _logger.SensitiveDebug("Updated todo {Id} title: {Title}", todo.Id, local.Title);
                        }
                        else
                        {
                            mergeSkipped++;
                            _logger.LogDebug("Skipped todo {Id}: local is newer (local={Local}, remote={Remote})",
                                todo.Id, existing.UpdatedAt, local.UpdatedAt);
                        }
                    }
                    else
                    {
                        await _todoService.ImportAsync(local);
                        mergeInserted++;
                    }
                }
                catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException
                                            or System.Security.Cryptography.AuthenticationTagMismatchException)
                {
                    decryptionErrors++;
                    _logger.LogWarning(ex, "Failed to decrypt synced todo {Id}; skipping", todo.Id);
                }
            }

            foreach (var deletedId in pullResponse.Todos.Deleted)
            {
                _logger.LogDebug("Pull deleted: {EntityType} {Id}", "todos", deletedId);
                await _todoService.DeleteAsync(deletedId);
                mergeDeleted++;
            }
            if (pullResponse.Todos.Deleted.Count > 0)
                _logger.LogInformation("Pull {EntityType} deletions applied: {Count}", "todos", pullResponse.Todos.Deleted.Count);
        }

        // Apply scheduled jobs
        if (_scheduledJobService is not null)
        {
            foreach (var syncJob in pullResponse.ScheduledJobs.Upserted)
            {
                try
                {
                    var local = _mapper.FromSyncScheduledJob(syncJob, userId);
                    var existing = await _scheduledJobService.GetAsync(syncJob.Id);

                    if (existing is null)
                    {
                        await _scheduledJobService.UpsertFromSyncAsync(local);
                        mergeInserted++;
                    }
                    else if (local.UpdatedAt.ToUniversalTime() >= existing.UpdatedAt.ToUniversalTime())
                    {
                        await _scheduledJobService.UpsertFromSyncAsync(local);
                        mergeUpdated++;
                        _logger.LogInformation("Updated scheduled job {Id}", syncJob.Id);
                    }
                    else
                    {
                        mergeSkipped++;
                        _logger.LogDebug("Skipped scheduled job {Id}: local is newer (local={Local}, remote={Remote})",
                            syncJob.Id, existing.UpdatedAt, local.UpdatedAt);
                    }
                }
                catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException
                                            or System.Security.Cryptography.AuthenticationTagMismatchException)
                {
                    decryptionErrors++;
                    _logger.LogWarning(ex, "Failed to decrypt synced scheduled job {Id}; skipping", syncJob.Id);
                }
            }

            foreach (var deletedId in pullResponse.ScheduledJobs.Deleted)
            {
                _logger.LogDebug("Pull deleted: {EntityType} {Id}", "scheduledJobs", deletedId);
                await _scheduledJobService.DeleteAsync(deletedId);
                mergeDeleted++;
            }
            if (pullResponse.ScheduledJobs.Deleted.Count > 0)
                _logger.LogInformation("Pull {EntityType} deletions applied: {Count}", "scheduledJobs", pullResponse.ScheduledJobs.Deleted.Count);
        }

        // Research sessions are no longer stored by the client (results are assistant chats); the
        // server may still include the field on the wire — it is ignored on pull.

        if (decryptionErrors > 0)
        {
            _logger.LogWarning("Pull completed with {Count} decryption error(s) — data may have been encrypted with a different key", decryptionErrors);
        }

        // Apply plugins
        if (_pluginService is not null &&
            (pullResponse.Plugins.Upserted.Count > 0 || pullResponse.Plugins.Deleted.Count > 0))
        {
            await _pluginService.ApplyServerPluginsAsync(
                pullResponse.Plugins.Upserted, pullResponse.Plugins.Deleted);
            _logger.LogInformation("Applied {Upserted} plugin upserts, {Deleted} plugin deletions",
                pullResponse.Plugins.Upserted.Count, pullResponse.Plugins.Deleted.Count);
        }

        // Apply managed personas. Placed here — after every other apply, before the catalog-version and
        // ETag persist below — for exactly the reason the plugin apply above is here: a throw inside the
        // replace must leave BOTH conditional tokens UNCHANGED, so the next pull re-sends the catalog
        // instead of fast-skipping (or 304-ing away) a snapshot this page never stored. Only the first page
        // is considered, because that is the only page the catalog block can appear on at all.
        if (isFirstPage && _personaService is not null)
        {
            // REPLACE-ALL, unlike every other channel: a non-null managedPersonas is the authoritative
            // snapshot for this user's group. Null (⇒ key absent, the server omits nulls app-wide) means the
            // catalog fast-skip fired — keep the store. Never merge, or an unassignment (which carries no
            // tombstone) would never remove anything. The payload is a SyncManagedPersonaSnapshot, not a
            // SyncEntityChanges<T>, precisely so this cannot be handed to the merge helper by mistake.
            if (pullResponse.ManagedPersonas is { } managed)
            {
                // No E2EE path and no decryptionErrors bookkeeping: managed rows carry no encryptedPayload/
                // wrappedDek by design (a group-shared row cannot be wrapped with one user's UMK), so they
                // are plaintext even for an E2EE account — see FromSyncManagedPersona (handoff §5.3).
                await _personaService.ReplaceManagedPersonasAsync(
                    managed.Personas.Select(_mapper.FromSyncManagedPersona).ToList());

                // RecentlyRemoved needs no handling under replace-all — absence from `personas` is what
                // removes a row, and an unassignment never appears here at all. It is logged as
                // confirmation, not consumed as the mechanism. Counts only (admin-authored names are
                // user content).
                _logger.LogInformation(
                    "Applied managed persona snapshot: {Count} persona(s), {RecentlyRemoved} recently removed",
                    managed.Personas.Count, managed.RecentlyRemoved.Count);
            }
        }

        // A present clientPolicy is authoritative, "{}" included — that is how a withdrawn policy arrives,
        // and it clears the cache. Absent (null) means the catalog fast-skip fired: keep what is cached.
        if (isFirstPage && _policyService is not null && pullResponse.ClientPolicy is { } policy)
        {
            await _policyService.ReplaceServerPolicyAsync(policy.Document);
            _logger.LogInformation(
                "Stored client policy document: {Length} chars; a change takes effect within this sync cycle",
                policy.Document.Length);
        }

        // Persist the server's current catalog version now that every entity in this page (including
        // plugins, the managed snapshot and the policy document, applied just above) has been applied
        // without throwing. Storing it only here, not when it was first read off the response further up,
        // means a mid-apply exception leaves the stored version unchanged, so the next pull still
        // omits/mismatches ?catalogVersion= and the server resends the full catalog instead of skipping the
        // un-applied changes. LastPullETag rides the same save for the same reason (see where it is read,
        // above), because a stored ETag would otherwise 304 away the retry the un-applied page needs.
        //
        // Both catalog first-run latches close here, on ONE rule: the forced unconditional pull reached
        // this point — i.e. it returned 2xx with a body whose applies all succeeded. Deliberately NOT "the
        // channel's own block arrived": a pre-upgrade server has no such channel, so waiting for a non-null
        // block would keep every future pull unconditional and permanently lose the 304 fast path. Closing
        // it blind is safe because the server folds the caller's group into catalogVersion — a token
        // stored before the server upgrade can never equal a mixed one, so the upgrade itself forces exactly
        // one full-catalog pull anyway. Never latched on a 304, a non-2xx or a null body: all three return
        // above, before this point. forceFullCatalog already carries isFirstPage and both channel flags, so
        // it is the whole condition.
        var latchCatalogChannels = forceFullCatalog;
        var storePullETag = newPullETag is not null && newPullETag != settings.LastPullETag;

        if (newCatalogVersion.HasValue || latchCatalogChannels || storePullETag)
        {
            if (newCatalogVersion.HasValue)
                settings.LastCatalogVersion = newCatalogVersion;
            if (latchCatalogChannels)
            {
                settings.ManagedPersonaStoreInitialized = true;
                settings.ClientPolicyInitialized = true;
            }
            if (storePullETag)
                settings.LastPullETag = newPullETag;

            await _settingsService.SaveSettingsAsync(settings);
            _logger.LogDebug(
                "Pull catalog version stored: {CatalogVersion} (managed store initialized: {Initialized}, client policy initialized: {PolicyInitialized}, ETag stored: {ETagStored})",
                newCatalogVersion, settings.ManagedPersonaStoreInitialized, settings.ClientPolicyInitialized, storePullETag);
        }

        var pulledCount = pullResponse.Templates.Upserted.Count
            + pullResponse.Personas.Upserted.Count
            + pullResponse.Providers.Upserted.Count
            + pullResponse.Sessions.Added.Count
            + pullResponse.Memories.Upserted.Count
            + pullResponse.KanbanColumns.Upserted.Count
            + pullResponse.Todos.Upserted.Count
            + pullResponse.ScheduledJobs.Upserted.Count
            + pullResponse.Plugins.Upserted.Count;

        _logger.LogInformation("Pull merge: {Inserted} inserted, {Updated} updated, {Skipped} skipped, {Deleted} deleted, {DecryptErrors} decrypt errors",
            mergeInserted, mergeUpdated, mergeSkipped, mergeDeleted, decryptionErrors);

        // Note: SyncCompleted fires once per drained page (not once per overall pull) — a large
        // multi-page drain raises it repeatedly. This is intentional for now; consumers that refresh
        // UI state on this event should debounce if that churn becomes noticeable.
        try
        {
            SyncCompleted?.Invoke(this, new SyncCompletedEventArgs
            {
                MergeInserted = mergeInserted,
                MergeUpdated = mergeUpdated,
                MergeDeleted = mergeDeleted,
                DecryptionErrors = decryptionErrors,
                SettingsChanged = settingsApplied,
                ProvidersChanged = providersApplied,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SyncCompleted handler threw — sync continues");
        }

        return (false, true, pulledCount, decryptionErrors, pullResponse.ServerTimestamp, pullResponse.HasMore == true);
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync();
        _logger.LogError("{Operation} failed ({Status})", operation, (int)response.StatusCode);
        _logger.SensitiveDebug("{Operation} failure body: {Body}", operation, body);
        throw new HttpRequestException(
            $"{operation} failed ({(int)response.StatusCode}): {body}");
    }

    private async Task CheckForPendingDevicesAsync()
    {
        try
        {
            var response = await _deviceMgmt!.GetDevicesAsync();

            // Check if current device still exists and is active
            if (_deviceKeys is not null)
            {
                var currentDeviceId = _deviceKeys.GetDeviceId();
                var currentDevice = response.Devices
                    .FirstOrDefault(d => d.DeviceId == currentDeviceId);

                if (currentDevice is null || currentDevice.Status == DeviceStatus.Revoked)
                {
                    _logger.LogWarning(
                        "Current device {DeviceId} was {Status} on server — raising CurrentDeviceRevoked",
                        currentDeviceId,
                        currentDevice is null ? "not found" : "revoked");
                    CurrentDeviceRevoked?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }

            var pending = response.Devices
                .Where(d => d.Status == DeviceStatus.Pending && d.OnboardingSessionId is not null)
                .ToList();

            if (pending.Count > 0)
            {
                _logger.LogInformation("Found {Count} pending device(s) awaiting approval", pending.Count);
                PendingDeviceDetected?.Invoke(this, new PendingDeviceEventArgs
                {
                    PendingDevices = pending
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check for pending devices");
        }
    }

    private HttpClient CreateAuthenticatedClient(string accessToken)
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);
        client.Timeout = TimeSpan.FromSeconds(60);
        return client;
    }

    public void Dispose()
    {
        _authService.LoginStateChanged -= OnAuthLoginStateChanged;
        _syncTimer?.Dispose();
        _syncLock.Dispose();
    }
}

/// <summary>Classification of a completed background-sync cycle for adaptive-polling backoff.</summary>
internal enum SyncCycleOutcome
{
    /// <summary>The cycle moved data (a push sent changes or a pull returned rows). Resets backoff.</summary>
    Active,
    /// <summary>The pull succeeded with no changes (304). Advances backoff.</summary>
    Idle,
    /// <summary>The cycle was neither active nor a clean no-change (e.g. a failed pull). Leaves backoff unchanged.</summary>
    Inconclusive,
}
