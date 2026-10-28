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
    private readonly ILocalDataResetIndicator? _resetIndicator;

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
    // Push and pull may both report the same removal in one cycle.
    private bool _revokedNoticeRaised;
    // Bumped on every Start/Stop so a sync cycle in flight during a Stop+Start cannot re-arm
    // the newly-started timer with a stale (non-InitialSyncDelay) period.
    private int _syncGeneration;
    // Set for one SyncNowAsync cycle; _syncLock keeps cycles from overlapping.
    private CancellationToken _cycleToken;
    // Bumped on every sign-in and sign-out, so a cycle can tell that the account changed under it.
    private int _accountEpoch;
    // Set for one pull/apply holder of _syncLock.
    private AccountScope? _cycleScope;

    private readonly record struct AccountScope(int Epoch, string? UserId);

    private sealed class AccountChangedException : Exception;

    public bool IsSyncActive => _syncTimer is not null;
    public bool IsE2EEOnboardingRequired { get; private set; }
    public bool IsE2EEDowngradeDetected { get; private set; }
    public CurrentDeviceRevokedEventArgs? PendingDeviceRevocation { get; private set; }
    public event EventHandler? E2EEOnboardingRequired;
    public event EventHandler? E2EEOnboardingCleared;
    public event EventHandler? E2EEDowngradeChanged;
    public event EventHandler<PendingDeviceEventArgs>? PendingDeviceDetected;
    public event EventHandler? EncryptedMigrationCompleted;
    public event EventHandler<CurrentDeviceRevokedEventArgs>? CurrentDeviceRevoked;
    public event EventHandler<SyncCompletedEventArgs>? SyncCompleted;

    public void NotifyE2EEOnboardingRequired()
    {
        if (IsE2EEOnboardingRequired) return;
        IsE2EEOnboardingRequired = true;
        E2EEOnboardingRequired?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyE2EEDowngradeDetected()
    {
        if (IsE2EEDowngradeDetected) return;
        IsE2EEDowngradeDetected = true;
        E2EEDowngradeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearE2EEDowngrade()
    {
        if (!IsE2EEDowngradeDetected) return;
        IsE2EEDowngradeDetected = false;
        E2EEDowngradeChanged?.Invoke(this, EventArgs.Empty);
    }

    // A token refresh can be what marks the account as requiring E2EE, so the gate runs again before any push.
    private async Task<string?> GetAccessTokenPastE2EEGateAsync()
    {
        var accessToken = await _authService.GetAccessTokenAsync();
        if (string.IsNullOrEmpty(accessToken))
            return null;
        return await BlocksMarkedAccountWithoutKeyAsync(await _settingsService.GetSettingsAsync()) ? null : accessToken;
    }

    public async Task<bool> AllowUnencryptedSyncAsync(string accountId)
    {
        var settings = await _settingsService.GetSettingsAsync();
        if (!IsE2EEDowngradeDetected || settings.IsE2EEEnabled || !settings.IsE2EERequiredForAccount()
            || !string.Equals(settings.SyncUserId, accountId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Unencrypted sync not allowed: the downgrade has cleared or the account changed");
            return false;
        }

        // Asked again: the server may have turned E2EE back on while the user read the warning.
        var serverStatus = _deviceMgmt is null ? null : await _deviceMgmt.CheckE2EEStatusAsync();
        if (serverStatus is not { IsEnabled: false })
        {
            if (serverStatus is not null)
            {
                ClearE2EEDowngrade();
                NotifyE2EEOnboardingRequired();
            }
            _logger.LogInformation("Unencrypted sync not allowed: the server did not confirm E2EE is off");
            return false;
        }

        settings.UnmarkE2EERequiredForAccount();
        await _settingsService.SaveSettingsAsync(settings);
        _logger.LogWarning("User allowed unencrypted sync for an account that previously required E2EE");
        ClearE2EEDowngrade();
        return true;
    }

    private async Task<bool> BlocksMarkedAccountWithoutKeyAsync(AppSettings settings)
    {
        if (settings.IsE2EEEnabled || !settings.IsE2EERequiredForAccount())
        {
            ClearE2EEDowngrade();
            return false;
        }

        var serverStatus = _deviceMgmt is null ? null : await _deviceMgmt.CheckE2EEStatusAsync();
        await ThrowIfAccountChangedAsync();
        if (serverStatus is { IsEnabled: true })
        {
            _logger.LogWarning("E2EE required for this account but UMK not available; onboarding required");
            ClearE2EEDowngrade();
            NotifyE2EEOnboardingRequired();
        }
        else if (serverStatus is not null)
        {
            _logger.LogWarning("Server reports E2EE off for an account that required it; refusing plaintext sync");
            NotifyE2EEDowngradeDetected();
        }
        else
        {
            _logger.LogWarning("Could not determine server E2EE status for an account that required it; skipping sync");
        }
        return true;
    }

    public void NotifyE2EEOnboardingCompleted()
    {
        PendingDeviceRevocation = null;
        _revokedNoticeRaised = false;
        ClearE2EEDowngrade();
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
        IPolicyService? policyService = null,
        ILocalDataResetIndicator? resetIndicator = null)
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
        _resetIndicator = resetIndicator;

        _authService.LoginStateChanged += OnAuthLoginStateChanged;
    }

    private void OnAuthLoginStateChanged(object? sender, bool isLoggedIn)
    {
        Interlocked.Increment(ref _accountEpoch);
        if (!isLoggedIn)
        {
            _hasVerifiedServerE2EEStatus = false;
            NotifyE2EEOnboardingCompleted();
            return;
        }

        _revokedNoticeRaised = false;
        PendingDeviceRevocation = null;
    }

    // A clear that failed may have left the old account signed in, so nothing may sync until it lands.
    private bool ResetPending()
    {
        if (_resetIndicator?.IsResetPending != true) return false;
        _logger.LogInformation("Sync skipped: a local-data clear is pending");
        return true;
    }

    private async Task<AccountScope> CaptureAccountScopeAsync(int epoch) =>
        new(epoch, (await _settingsService.GetSettingsAsync())?.SyncUserId);

    private async Task<bool> AccountChangedAsync(AccountScope scope)
    {
        if (scope.Epoch == Volatile.Read(ref _accountEpoch)
            && string.Equals((await _settingsService.GetSettingsAsync())?.SyncUserId, scope.UserId, StringComparison.OrdinalIgnoreCase))
            return false;

        _logger.LogWarning("Sync abandoned: the signed-in account changed while it ran; nothing from it is applied or saved");
        return true;
    }

    // Once the account changed, the server's answers belong to the old one and the shared settings to the new one.
    private async Task ThrowIfAccountChangedAsync()
    {
        if (_cycleScope is { } scope && await AccountChangedAsync(scope))
            throw new AccountChangedException();
    }

    public void StartBackgroundSync()
    {
        if (_syncTimer is not null || PendingDeviceRevocation is not null || ResetPending()) return;

        _consecutiveIdleCycles = 0;
        _deviceCheckCounter = 0;
        _revokedNoticeRaised = false;
        var generation = ++_syncGeneration;

        // One-shot, re-armed by each cycle; the closure pins timer and generation so a cycle racing a
        // Stop+Start cannot re-arm the new session's timer.
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

    public async Task<SyncResult?> SyncNowAsync(CancellationToken ct = default)
    {
        var epoch = Volatile.Read(ref _accountEpoch);
        if (!_authService.IsLoggedIn || ResetPending()) return null;

        if (!await _syncLock.WaitAsync(0)) return null;

        SyncResult? result = null;
        var syncSw = Stopwatch.StartNew();
        _cycleToken = ct;
        try
        {
            _cycleScope = await CaptureAccountScopeAsync(epoch);
            var settings = await _settingsService.GetSettingsAsync();
            _logger.LogInformation("SyncNowAsync started — LastSyncTimestamp: {LastSync}", settings.LastSyncTimestamp);
            if (!settings.SyncEnabled || string.IsNullOrEmpty(settings.ServerUrl))
                return null;

            if (_e2ee is not null && settings.IsE2EEEnabled && !_e2ee.IsReady())
            {
                _logger.LogWarning("E2EE enabled but UMK not available; onboarding required");
                NotifyE2EEOnboardingRequired();
                return null;
            }

            if (await BlocksMarkedAccountWithoutKeyAsync(settings))
                return null;

            // E2EE may be on for the account from another device. Latch only a conclusive "off": an onboarding
            // device or an unreachable server must keep skipping cycles rather than pull ciphertext it cannot read.
            if (!_hasVerifiedServerE2EEStatus && _deviceMgmt is not null && !settings.IsE2EEEnabled)
            {
                var serverStatus = await _deviceMgmt.CheckE2EEStatusAsync();
                await ThrowIfAccountChangedAsync();
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

            var accessToken = await GetAccessTokenPastE2EEGateAsync();
            if (accessToken is null)
                return null;
            await ThrowIfAccountChangedAsync();

            var serverUrl = settings.ServerUrl.TrimEnd('/');
            using var client = CreateAuthenticatedClient(accessToken);

            var (pushedCount, pushSucceeded, pushSentChanges) = await PushChangesAsync(client, serverUrl, settings);
            if (PendingDeviceRevocation is not null)
                return null;

            var (pulled, decryptErrors, pullOk, serverTimestamp, settingsUndecryptable) = await PullChangesAsync(client, serverUrl, settings);
            if (PendingDeviceRevocation is not null)
                return null;
            await ThrowIfAccountChangedAsync();

            // Keep skipped account settings in the next pull until reset preferences can be replaced.
            if (pullOk && serverTimestamp.HasValue && !(settings.SuppressSettingsPushAfterReset && settingsUndecryptable))
            {
                // The push selects rows by this cursor too, so moving it past a failed push strands those edits.
                if (pushSucceeded)
                    settings.LastSyncTimestamp = serverTimestamp.Value;
                else
                    _logger.LogWarning("Push failed — LastSyncTimestamp NOT updated so the next push retries the same changes");
                if (!settingsUndecryptable)
                    settings.SuppressSettingsPushAfterReset = false;
                await _settingsService.SaveSettingsAsync(settings);
            }
            else if (!pullOk)
            {
                _logger.LogWarning("Pull failed — LastSyncTimestamp NOT updated to avoid missing data");
            }

            // Pending devices are checked every Nth cycle, and on any 200 pull since that is the only hint a
            // device registered; otherwise the cadence at the backoff ceiling could take ~90 min.
            if (_deviceMgmt is not null && _e2ee?.IsReady() == true)
            {
                await _deviceMgmt.EnsureRecoveryProofKeyAsync();

                var got200Pull = pullOk && serverTimestamp.HasValue;
                var (shouldCheckDevices, nextDeviceCheckCounter) = AdvanceDeviceCheck(_deviceCheckCounter, got200Pull);
                _deviceCheckCounter = nextDeviceCheckCounter;
                if (shouldCheckDevices)
                {
                    await CheckForPendingDevicesAsync();
                }
            }

            var outcome = ClassifyCycle(pushSucceeded, pushSentChanges, pulled, pullOk, serverTimestamp);
            _consecutiveIdleCycles = UpdateIdleCycleCount(_consecutiveIdleCycles, outcome);

            result = new SyncResult(pushedCount, pulled, decryptErrors, pushSucceeded);
            syncSw.Stop();
            _logger.LogInformation("SyncNowAsync completed in {ElapsedMs}ms — Pushed: {Pushed}, Pulled: {Pulled}, DecryptionErrors: {DecryptErrors}",
                syncSw.ElapsedMilliseconds, result.PushedCount, result.PulledCount, result.DecryptionErrors);
        }
        catch (AccountChangedException)
        {
            result = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sync cycle failed");
        }
        finally
        {
            _cycleToken = default;
            _cycleScope = null;
            _syncLock.Release();
        }
        return result;
    }

    public async Task PerformFirstSyncMigrationAsync()
    {
        var epoch = Volatile.Read(ref _accountEpoch);
        if (!_authService.IsLoggedIn || ResetPending()) return;

        await _syncLock.WaitAsync();
        try
        {
            _cycleScope = await CaptureAccountScopeAsync(epoch);
            var settings = await _settingsService.GetSettingsAsync();
            _logger.LogInformation("PerformFirstSyncMigrationAsync: SyncEnabled={Enabled}, ServerUrl={Url}, LastSyncTimestamp={LastSync}, IsE2EEEnabled={E2EE}",
                settings.SyncEnabled, SafeUrl.Format(settings.ServerUrl),
                settings.LastSyncTimestamp?.ToString("O") ?? "(null)", settings.IsE2EEEnabled);

            if (!settings.SyncEnabled || string.IsNullOrEmpty(settings.ServerUrl))
                return;

            // Every local row goes up unfiltered: without the key it would overwrite the server's only copy.
            if (_e2ee is not null && settings.IsE2EEEnabled && !_e2ee.IsReady())
            {
                _logger.LogWarning("E2EE enabled but UMK not available; skipping first-sync migration");
                NotifyE2EEOnboardingRequired();
                return;
            }

            if (await BlocksMarkedAccountWithoutKeyAsync(settings))
                return;

            // Local E2EE off is not proof the account's is: a restored device has not activated yet.
            if (_e2ee is not null && _deviceMgmt is not null && !settings.IsE2EEEnabled)
            {
                var serverStatus = await _deviceMgmt.CheckE2EEStatusAsync();
                await ThrowIfAccountChangedAsync();
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

            var accessToken = await GetAccessTokenPastE2EEGateAsync();
            if (accessToken is null)
                return;
            await ThrowIfAccountChangedAsync();

            var serverUrl = settings.ServerUrl.TrimEnd('/');
            using var client = CreateAuthenticatedClient(accessToken);

            var templates = await _templateService.GetTemplatesAsync();
            var personas = _personaService is not null
                ? await _personaService.GetPersonasAsync()
                : [];
            var providers = await _providerService.GetProvidersAsync();
            // Uncapped: the chunked push below already bounds the request bodies.
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
            // MinValue: the delta push is cursor-gated, so only a full re-push recovers a job rejected mid-onboarding.
            var scheduledJobs = _scheduledJobService is not null
                ? await _scheduledJobService.GetModifiedSinceAsync(DateTime.MinValue)
                : [];

            var isE2EE = _e2ee?.IsReady() == true;
            var userId = isE2EE ? settings.SyncUserId : null;

            // No hash gate: an E2EE migration or a new account needs a fresh server row even when unchanged.
            var settingsHash = SyncMapper.ComputeSettingsHash(settings);
            var settingsChanged = !settings.SuppressSettingsPushAfterReset;

            // Sessions dominate the volume, so only they are chunked; everything else rides the first batch.
            var templateDtos = templates.Where(t => !t.IsBuiltIn).Select(t => _mapper.ToSyncTemplate(t, userId)).ToList();
            // Managed personas are pull-only; the server would quarantine one sent back.
            var personaDtos = personas.Where(p => !p.IsBuiltIn && !p.IsManaged).Select(p => _mapper.ToSyncPersona(p, userId)).ToList();
            var providerDtos = providers.Where(p => p.ProviderType != AiProviderType.PiaCloud).Select(p => _mapper.ToSyncProvider(p, userId)).ToList();
            var sessionDtos = sessions.Select(s => _mapper.ToSyncSession(s, userId)).ToList();
            var memoryDtos = memories.Select(m => _mapper.ToSyncMemory(m, userId)).ToList();
            var kanbanDtos = kanbanColumns.Select(c => _mapper.ToSyncKanbanColumn(c, userId)).ToList();
            var todoDtos = todos.Select(t => _mapper.ToSyncTodo(t, userId)).ToList();
            // Device-local: the MeetingUrl is a bearer token with no wire field, and an older peer would run the
            // unknown Kind as Research.
            var jobDtos = scheduledJobs
                .Where(j => j.Kind != ScheduledJobKind.MeetingAttendance)
                .Select(j => _mapper.ToSyncScheduledJob(j, userId)).ToList();

            // Bounded bodies keep a large first sync inside the 30/60 s rate limit; at least one batch always
            // runs so an account without sessions still pushes everything else.
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
                await ThrowIfAccountChangedAsync();
                await EnsureSuccessAsync(response, $"First-sync push (batch {batch + 1}/{batchCount})");

                _logger.LogInformation("First-sync push batch {Batch}/{BatchCount} completed (settingsIncluded: {SettingsIncluded}, templates: {Templates}, personas: {Personas}, providers: {Providers}, sessions: {Sessions}, memories: {Memories}, kanbanColumns: {KanbanColumns}, todos: {Todos}, scheduledJobs: {Jobs})",
                    batch + 1, batchCount, request.Settings is not null,
                    request.Templates.Upserted.Count, request.Personas.Upserted.Count, request.Providers.Upserted.Count,
                    request.Sessions.Added.Count, request.Memories.Upserted.Count,
                    request.KanbanColumns.Upserted.Count, request.Todos.Upserted.Count, request.ScheduledJobs.Upserted.Count);
            }

            // Only after every batch landed (EnsureSuccessAsync throws), or failed settings hide behind the hash.
            await PersistSettingsHashIfChangedAsync(settings, settingsHash, settingsChanged);

            if (isE2EE)
                EncryptedMigrationCompleted?.Invoke(this, EventArgs.Empty);

            _logger.LogInformation("First-sync push completed in {BatchCount} batch(es): {Sessions} sessions, {Templates} templates, {Personas} personas, {Providers} providers, {Memories} memories, {KanbanColumns} kanbanColumns, {Todos} todos, {Jobs} scheduledJobs",
                batchCount, sessionDtos.Count, templateDtos.Count, personaDtos.Count, providerDtos.Count,
                memoryDtos.Count, kanbanDtos.Count, todoDtos.Count, jobDtos.Count);

            var (pulled, decryptErrors, pullOk, serverTimestamp, settingsUndecryptable) = await PullChangesAsync(client, serverUrl, settings);
            _logger.LogInformation("First-sync pull: {Pulled} pulled, {Errors} decrypt errors, pullOk={PullOk}", pulled, decryptErrors, pullOk);
            await ThrowIfAccountChangedAsync();

            if (pullOk && serverTimestamp.HasValue && !(settings.SuppressSettingsPushAfterReset && settingsUndecryptable))
            {
                settings.LastSyncTimestamp = serverTimestamp.Value;
                if (!settingsUndecryptable)
                    settings.SuppressSettingsPushAfterReset = false;
                await _settingsService.SaveSettingsAsync(settings);
            }
            else
            {
                _logger.LogWarning("First-sync pull failed — LastSyncTimestamp NOT updated; next sync will retry full pull");
            }
        }
        catch (AccountChangedException)
        {
        }
        finally
        {
            _cycleScope = null;
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

    /// <summary>Refetches provider rows an E2EE pull without the key blanked, by resetting the sync cursor.</summary>
    public async Task<bool> RepairBlankedSyncRowsAsync()
    {
        var scope = await CaptureAccountScopeAsync(Volatile.Read(ref _accountEpoch));
        if (!_authService.IsLoggedIn) return false;

        var settings = await _settingsService.GetSettingsAsync();
        if (!settings.SyncEnabled || settings.BlankedSyncRowRepairAt is not null)
            return false;

        // Before onboarding the resync would only meet the pull refusal; the launch that has the key gets the attempt.
        if (settings.IsE2EEEnabled && _e2ee?.IsReady() != true)
            return false;

        // Sync never carries a real Pia Cloud row, so one under another id is a blanked entity; the push filter
        // kept such rows off the server, which is why the server's copy is still intact.
        var providers = await _providerService.GetProvidersAsync();
        var blanked = providers.Count(p =>
            p.ProviderType == AiProviderType.PiaCloud
            && p.Id != ProviderService.PiaCloudProviderId);

        if (blanked == 0) return false;

        _logger.LogWarning(
            "Found {Count} provider row(s) blanked by an E2EE pull that could not decrypt; forcing a full resync",
            blanked);

        // Marked only once a cycle ran: a skipped one returns null, and burning the one-shot there strands the rows.
        var result = await ForceFullResyncAsync();
        if (result is null || await AccountChangedAsync(scope))
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
        if (ResetPending()) return null;
        var scope = await CaptureAccountScopeAsync(Volatile.Read(ref _accountEpoch));
        _logger.LogInformation("ForceFullResyncAsync: resetting LastSyncTimestamp to trigger full pull");
        await _syncLock.WaitAsync();
        try
        {
            if (await AccountChangedAsync(scope))
                return null;
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
        var response = await client.PostAsync($"{serverUrl}/api/sync/push", content, _cycleToken);
        pushSw.Stop();
        _logger.LogInformation("Push HTTP: {StatusCode} in {ElapsedMs}ms", (int)response.StatusCode, pushSw.ElapsedMilliseconds);
        return response;
    }

    /// <summary>Separates "nothing to push" from a failed POST, since only the first is an idle cycle for backoff.</summary>
    private async Task<(int PushedCount, bool PushSucceeded, bool SentChanges)> PushChangesAsync(HttpClient client, string serverUrl, AppSettings settings)
    {
        var lastSync = settings.LastSyncTimestamp ?? DateTime.MinValue;

        var templates = await _templateService.GetTemplatesAsync();
        var personas = _personaService is not null
            ? await _personaService.GetPersonasAsync()
            : [];
        var providers = await _providerService.GetProvidersAsync();

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
        // A replaced managed snapshot bumps every row's UpdatedAt, so without !IsManaged this log counts phantoms.
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

        // Hash the plaintext: the wire payload differs every call (ModifiedAt, fresh DEK). Absent
        // Settings is no-change to the server; reset defaults are held back until a pull lands.
        var settingsHash = SyncMapper.ComputeSettingsHash(settings);
        var settingsChanged = !settings.SuppressSettingsPushAfterReset && settingsHash != settings.LastPushedSettingsHash;

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
                    // Managed personas are pull-only; the server would quarantine one sent back.
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
                // Device-local, for the reasons given at the first-sync push site above.
                Upserted = scheduledJobs
                    .Where(j => j.Kind != ScheduledJobKind.MeetingAttendance)
                    .Select(j => _mapper.ToSyncScheduledJob(j, userId))
                    .ToList(),
                Deleted = pendingDeletes.GetValueOrDefault("scheduledJobs", [])
            },
            // Always empty: research results are assistant chats, and the wire contract still has the field.
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

        // Plugin prefs and settings are not in pushedCount, so a change to only those must still be sent.
        if (pushedCount == 0
            && pendingDeletes.Values.All(v => v.Count == 0)
            && request.PluginPreferences.Count == 0
            && !settingsChanged)
        {
            _logger.LogInformation("Push short-circuited: no changes to push");
            return (0, true, false);
        }

        using var response = await PostPushAsync(client, serverUrl, request);
        await ThrowIfAccountChangedAsync();
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Push failed with status {Status}", response.StatusCode);
            _logger.SensitiveDebug("Push failure body: {Body}", body);
            var code = ReadErrorCodeFromBody(body);
            if (code == E2EEErrorCodes.DeviceRevoked)
            {
                await RaiseCurrentDeviceRevokedAsync(wasRevoked: true);
            }
            else if ((int)response.StatusCode == 403 && code == E2EEErrorCodes.E2EERequired)
            {
                // A plaintext push to an E2EE account never succeeds on retry, so onboarding is the only way out.
                _logger.LogWarning("Server requires E2EE for this account; onboarding required");
                NotifyE2EEOnboardingRequired();
            }
            // Failed, not idle: backoff would slow the retry of changes that are still pending.
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
        _pluginService?.ClearPreferenceChangesAfterSuccessfulPush();

        // Only after a successful push, or failed settings would hide behind the advanced hash.
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

    /// <summary>Drains every pull page. Relies on the server stamping a HasMore page with its own max SyncedAt
    /// and never splitting a SyncedAt across pages; otherwise advancing <c>since</c> skips rows.</summary>
    private async Task<(int Pulled, int DecryptionErrors, bool PullSucceeded, DateTime? ServerTimestamp, bool SettingsUndecryptable)> PullChangesAsync(HttpClient client, string serverUrl, AppSettings settings)
    {
        // The reset guard also covers profiles whose cursor already passed unreadable account settings.
        var lastSync = settings.SuppressSettingsPushAfterReset ? DateTime.MinValue : settings.LastSyncTimestamp ?? DateTime.MinValue;
        // Ensure UTC Kind so ToString("O") includes the Z suffix — prevents Npgsql
        // timestamptz comparison failures when the server uses PostgreSQL.
        if (lastSync.Kind != DateTimeKind.Utc)
            lastSync = DateTime.SpecifyKind(lastSync, DateTimeKind.Utc);

        var totalPulled = 0;
        var totalDecryptErrors = 0;
        DateTime? lastServerTimestamp = null;
        var settingsUndecryptable = false;

        for (var page = 0; page < MaxPullPages; page++)
        {
            // Only the first page is conditional / stores the ETag+catalog version: it is the one
            // request whose `since` equals the persisted cursor, so it is the only representation the
            // stored conditional-GET metadata can describe. Drain pages use a moving `since`.
            var pageResult = await PullPageAsync(client, serverUrl, settings, lastSync, isFirstPage: page == 0);

            if (pageResult.NotModified)
                return (0, 0, true, null, false); // only reachable on the first page

            if (!pageResult.PullSucceeded)
            {
                // First-page failure => nothing applied, keep the cursor. A later-page failure means
                // earlier pages were already applied: advance the cursor to the last drained page so
                // the next sync resumes after them instead of re-pulling from the start.
                if (page == 0)
                    return (0, 0, false, null, false);
                _logger.LogWarning("Pull drain failed on page {Page}; keeping earlier pages and advancing cursor", page);
                return (totalPulled, totalDecryptErrors, true, lastServerTimestamp, settingsUndecryptable);
            }

            totalPulled += pageResult.Pulled;
            totalDecryptErrors += pageResult.DecryptionErrors;
            settingsUndecryptable |= pageResult.SettingsUndecryptable;
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

        return (totalPulled, totalDecryptErrors, true, lastServerTimestamp, settingsUndecryptable);
    }

    /// <summary>True when the page holds at least one E2EE row and this client is not in a state to decrypt it.</summary>
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

    /// <summary>
    /// Drops rows that arrived with neither ciphertext nor content, returning how many were dropped.
    /// </summary>
    /// <remarks>
    /// The account is flagged E2EE from the moment the recovery key is stored, which is before any row
    /// has been encrypted. Until each row is migrated the server takes its E2EE projection branch and
    /// emits a shell: no ciphertext, and the plaintext columns skipped. Applying one blanks the local
    /// copy exactly like the unreadable-ciphertext case, but there is no ciphertext here to detect.
    ///
    /// Dropping the row leaves the local copy alone; the row's SyncedAt bumps when it is finally
    /// migrated, so it comes down intact then. Deliberately NOT a page-level refusal: a migration that
    /// died partway (the push has no retry) would otherwise wedge sync for good.
    ///
    /// Settings is excluded on purpose — there the server falls back to real plaintext rather than a
    /// shell, so a Settings row without ciphertext still carries the user's actual values.
    /// </remarks>
    private int DropUnmigratedShells(SyncPullResponse pull)
    {
        if (_e2ee?.IsReady() != true) return 0;

        return pull.Templates.Upserted.RemoveAll(x => x.EncryptedPayload is null)
            + pull.Personas.Upserted.RemoveAll(x => x.EncryptedPayload is null)
            + pull.Providers.Upserted.RemoveAll(x => x.EncryptedPayload is null)
            + pull.Sessions.Added.RemoveAll(x => x.EncryptedPayload is null)
            + pull.Memories.Upserted.RemoveAll(x => x.EncryptedPayload is null)
            + pull.Todos.Upserted.RemoveAll(x => x.EncryptedPayload is null)
            + pull.KanbanColumns.Upserted.RemoveAll(x => x.EncryptedPayload is null)
            + pull.ScheduledJobs.Upserted.RemoveAll(x => x.EncryptedPayload is null);
    }

    private async Task<(bool NotModified, bool PullSucceeded, int Pulled, int DecryptionErrors, DateTime? ServerTimestamp, bool HasMore, bool SettingsUndecryptable)> PullPageAsync(HttpClient client, string serverUrl, AppSettings settings, DateTime sinceUtc, bool isFirstPage)
    {
        var since = sinceUtc.ToString("O");

        // One unconditional pull per uninitialized catalog channel: an older build may echo a current token while
        // holding no managed rows or policy, and a 304 carries neither, so both conditionals must go.
        var forceFullCatalog = isFirstPage
            && (!settings.ManagedPersonaStoreInitialized || !settings.ClientPolicyInitialized);

        // Both parameters are opt-in, so an older server ignores them; no catalogVersion means the full catalog.
        var pullUrl = $"{serverUrl}/api/sync/pull?since={since}&limit={PullPageLimit}";
        if (settings.LastCatalogVersion.HasValue && !forceFullCatalog)
            pullUrl += $"&catalogVersion={settings.LastCatalogVersion.Value}";
        _logger.LogInformation("Pull requesting: {Url}", SafeUrl.Format(pullUrl));

        var pullRequest = new HttpRequestMessage(HttpMethod.Get, pullUrl);
        // TryParse, so a weak stored ETag cannot throw and abort the pull.
        if (isFirstPage && !forceFullCatalog && !settings.SuppressSettingsPushAfterReset
            && !string.IsNullOrEmpty(settings.LastPullETag) && EntityTagHeaderValue.TryParse(settings.LastPullETag, out var lastPullTag))
            pullRequest.Headers.IfNoneMatch.Add(lastPullTag);

        if (forceFullCatalog)
            _logger.LogInformation(
                "Pull forced unconditional: a catalog channel is uninitialized (managed personas: {Personas}, client policy: {Policy})",
                settings.ManagedPersonaStoreInitialized, settings.ClientPolicyInitialized);

        var pullSw = Stopwatch.StartNew();
        var response = await client.SendAsync(pullRequest, _cycleToken);
        pullSw.Stop();
        await ThrowIfAccountChangedAsync();
        _logger.LogInformation("Pull HTTP: {StatusCode} in {ElapsedMs}ms", (int)response.StatusCode, pullSw.ElapsedMilliseconds);

        if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
        {
            _logger.LogDebug("Pull returned 304 Not Modified — no changes since last sync");
            return (true, true, 0, 0, null, false, false);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Pull failed with status {Status}", response.StatusCode);
            _logger.SensitiveDebug("Pull failure body: {Body}", body);
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden
                && ReadErrorCodeFromBody(body) == E2EEErrorCodes.DeviceRevoked)
            {
                await RaiseCurrentDeviceRevokedAsync(wasRevoked: true);
            }
            return (false, false, 0, 0, null, false, false);
        }

        // Stored only after every apply below succeeds: a stored ETag would 304 away the retry of a page whose
        // apply threw, and a withdrawn managed persona would then never leave the store.
        var newPullETag = isFirstPage && response.Headers.ETag is not null
            ? response.Headers.ETag.ToString()
            : null;

        var pullResponse = await response.Content.ReadFromJsonAsync<SyncPullResponse>();
        if (pullResponse is null) return (false, false, 0, 0, null, false, false);

        // Stored after the apply too: the catalog query has no SyncedAt filter, so a stored version would skip
        // resending plugin changes this page never applied.
        var newCatalogVersion = isFirstPage && pullResponse.CatalogVersion.HasValue
            && pullResponse.CatalogVersion != settings.LastCatalogVersion
            ? pullResponse.CatalogVersion
            : null;

        // Managed personas as counts only: names, taglines and prompts are admin-authored user content.
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

        // Refuse the whole page when its ciphertext cannot be read: applying blanked rows overwrites real data,
        // and the kept cursor brings the page down again after onboarding.
        if (CarriesUnreadableCiphertext(pullResponse, userId))
        {
            _logger.LogError(
                "Pull page carries E2EE ciphertext but this client cannot decrypt it "
                + "(e2eeReady: {Ready}, userId present: {HasUserId}) — refusing the page and keeping the sync cursor",
                _e2ee?.IsReady() == true, userId is not null);
            NotifyE2EEOnboardingRequired();
            return (false, false, 0, 0, null, false, false);
        }

        var shellsDropped = DropUnmigratedShells(pullResponse);
        if (shellsDropped > 0)
        {
            _logger.LogWarning(
                "Dropped {Count} row(s) the server sent with neither ciphertext nor content; "
                + "they are not migrated to E2EE yet and applying them would blank the local copy",
                shellsDropped);
        }

        var decryptionErrors = 0;
        var mergeInserted = 0;
        var mergeUpdated = 0;
        var mergeSkipped = 0;
        var mergeDeleted = 0;

        var settingsApplied = false;
        var settingsUndecryptable = false;
        var providersApplied = false;

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
                settingsUndecryptable = true;
                _logger.LogWarning(ex, "Failed to decrypt synced settings; skipping");
            }
        }

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
            await _templateService.DeleteTemplateAsync(deletedId, trackForSync: false);
            mergeDeleted++;
        }
        if (pullResponse.Templates.Deleted.Count > 0)
            _logger.LogInformation("Pull {EntityType} deletions applied: {Count}", "templates", pullResponse.Templates.Deleted.Count);

        // Last write wins on UpdatedAt.
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
                        // IsReadOnly, not IsBuiltIn: UpdatePersonaAsync throws on a managed id, which would abort every pull.
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
                await _personaService.DeletePersonaAsync(deletedId, trackForSync: false);
                mergeDeleted++;
            }
            if (pullResponse.Personas.Deleted.Count > 0)
                _logger.LogInformation("Pull {EntityType} deletions applied: {Count}", "personas", pullResponse.Personas.Deleted.Count);
        }

        // Matched by id, then by content fingerprint, so a provider created on two devices becomes one row.
        var localProviders = (await _providerService.GetProvidersAsync()).ToList();
        var localByFingerprint = new Dictionary<string, AiProvider>(StringComparer.Ordinal);
        foreach (var p in localProviders)
        {
            if (p.Id == ProviderService.PiaCloudProviderId) continue;
            var fp = ProviderFingerprint.Compute(p);
            if (fp == ProviderFingerprint.PiaCloudSentinel) continue;
            // The newest local wins a shared fingerprint; ConsolidateLocalDuplicatesAsync removes the rest at startup.
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
                        // Keys never travel in plaintext, and under E2EE the mapper has set local.EncryptedApiKey. The fields
                        // SyncProvider omits hold only defaults in `local`, so this device's values are carried over.
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
                    // The server id wins, the later UpdatedAt wins the content, and UpdatedAt takes the max so the next push
                    // does not regress the server row; the device-local fields come from `dup` either way.
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

                // Plaintext wire keys are ignored; an E2EE-synced key is already on local.EncryptedApiKey.
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
            await _providerService.DeleteProviderAsync(deletedId, trackForSync: false);
            mergeDeleted++;
        }
        if (pullResponse.Providers.Deleted.Count > 0)
            _logger.LogInformation("Pull {EntityType} deletions applied: {Count}", "providers", pullResponse.Providers.Deleted.Count);

        providersApplied = pullResponse.Providers.Upserted.Count > 0
            || pullResponse.Providers.Deleted.Count > 0;

        // Heals mode defaults whose provider was reassigned or removed; writes only when it finds one.
        if (providersApplied || settingsApplied)
        {
            try { await _providerService.RepairModeDefaultsAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RepairModeDefaultsAsync failed after pull; continuing");
            }
        }

        // Sessions are append-only.
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
            await _memoryService.DeleteObjectAsync(deletedId, trackForSync: false);
            mergeDeleted++;
        }
        if (pullResponse.Memories.Deleted.Count > 0)
            _logger.LogInformation("Pull {EntityType} deletions applied: {Count}", "memories", pullResponse.Memories.Deleted.Count);

        // Columns before todos, which reference them.
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

            // Column deletes are not applied: only an empty column can be deleted, and the client enforces that.
        }

        if (_todoService is not null)
        {
            foreach (var todo in pullResponse.Todos.Upserted)
            {
                try
                {
                    var local = _mapper.FromSyncTodo(todo, userId);

                    // An older peer sends no ColumnId, so the status picks the column.
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
                await _todoService.DeleteAsync(deletedId, trackForSync: false);
                mergeDeleted++;
            }
            if (pullResponse.Todos.Deleted.Count > 0)
                _logger.LogInformation("Pull {EntityType} deletions applied: {Count}", "todos", pullResponse.Todos.Deleted.Count);
        }

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
                await _scheduledJobService.DeleteAsync(deletedId, trackForSync: false);
                mergeDeleted++;
            }
            if (pullResponse.ScheduledJobs.Deleted.Count > 0)
                _logger.LogInformation("Pull {EntityType} deletions applied: {Count}", "scheduledJobs", pullResponse.ScheduledJobs.Deleted.Count);
        }

        // Research sessions on the wire are ignored: research results are assistant chats.

        if (decryptionErrors > 0)
        {
            _logger.LogWarning("Pull completed with {Count} decryption error(s) — data may have been encrypted with a different key", decryptionErrors);
        }

        if (_pluginService is not null &&
            (pullResponse.Plugins.Upserted.Count > 0 || pullResponse.Plugins.Deleted.Count > 0))
        {
            await _pluginService.ApplyServerPluginsAsync(
                pullResponse.Plugins.Upserted, pullResponse.Plugins.Deleted);
            _logger.LogInformation("Applied {Upserted} plugin upserts, {Deleted} plugin deletions",
                pullResponse.Plugins.Upserted.Count, pullResponse.Plugins.Deleted.Count);
        }

        // Managed personas last, before the token save below, so a throw in the replace leaves both conditional
        // tokens unchanged. Only the first page can carry the catalog block.
        if (isFirstPage && _personaService is not null)
        {
            // Replace-all: a non-null snapshot is authoritative and null means the catalog fast-skip fired. A merge
            // would never remove an unassigned persona, which carries no tombstone.
            if (pullResponse.ManagedPersonas is { } managed)
            {
                // Managed rows are plaintext even under E2EE: a group-shared row cannot be wrapped with one user's key.
                await _personaService.ReplaceManagedPersonasAsync(
                    managed.Personas.Select(_mapper.FromSyncManagedPersona).ToList());

                // RecentlyRemoved is logged, not applied: absence from the snapshot is what removes a row. Counts only.
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

        // Stored only now that the whole page applied, so a throw above leaves the tokens for the retry. The
        // first-run latches close on the same rule, since an older server never sends a channel block to wait for.
        var latchCatalogChannels = forceFullCatalog;
        var storePullETag = newPullETag is not null && newPullETag != settings.LastPullETag;

        if (newCatalogVersion.HasValue || latchCatalogChannels || storePullETag)
        {
            await ThrowIfAccountChangedAsync();
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

        // Raised once per drained page, not once per pull.
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

        return (false, true, pulledCount, decryptionErrors, pullResponse.ServerTimestamp, pullResponse.HasMore == true, settingsUndecryptable);
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync();
        _logger.LogError("{Operation} failed ({Status})", operation, (int)response.StatusCode);
        _logger.SensitiveDebug("{Operation} failure body: {Body}", operation, body);
        if (ReadErrorCodeFromBody(body) == E2EEErrorCodes.DeviceRevoked)
        {
            await RaiseCurrentDeviceRevokedAsync(wasRevoked: true);
        }
        throw new HttpRequestException(
            $"{operation} failed ({(int)response.StatusCode}): {body}");
    }

    private static string? ReadErrorCodeFromBody(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task RaiseCurrentDeviceRevokedAsync(bool wasRevoked)
    {
        if (_revokedNoticeRaised) return;
        _revokedNoticeRaised = true;
        StopBackgroundSync();
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            settings.ForgetE2EEKey();
            await _settingsService.SaveSettingsAsync(settings);
        }
        finally
        {
            _e2ee?.ClearCachedUmk();
            PendingDeviceRevocation = new CurrentDeviceRevokedEventArgs(wasRevoked);
            NotifyE2EEOnboardingRequired();
            CurrentDeviceRevoked?.Invoke(this, PendingDeviceRevocation);
        }
    }

    private async Task ReplaceLostDeviceAsync()
    {
        if (_revokedNoticeRaised) return;
        _revokedNoticeRaised = true;
        StopBackgroundSync();
        _e2ee?.ClearCachedUmk();
        await _deviceMgmt!.ReplaceLostDeviceAsync();
    }

    private async Task CheckForPendingDevicesAsync()
    {
        try
        {
            var response = await _deviceMgmt!.GetDevicesAsync();
            if (_cycleScope is { } scope && await AccountChangedAsync(scope))
                return;

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
                    await RaiseCurrentDeviceRevokedAsync(wasRevoked: currentDevice is not null);
                    return;
                }

                // Re-registered under this id with other keys: approving "it" would hand the key to those keys.
                if (currentDevice.Status == DeviceStatus.Pending)
                {
                    _logger.LogWarning(
                        "Current device {DeviceId} is pending on the server; onboarding required", currentDeviceId);
                    NotifyE2EEOnboardingRequired();
                    return;
                }

                // The local copy of the key still opens, so sync alone would never notice the device keys are gone.
                if (_deviceMgmt.HoldsOtherKeysThan(currentDevice))
                {
                    _logger.LogWarning(
                        "Current device {DeviceId} no longer holds the keys the server lists; replacing it", currentDeviceId);
                    await ReplaceLostDeviceAsync();
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
