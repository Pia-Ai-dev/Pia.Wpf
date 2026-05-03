using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
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
    private readonly IProviderService _providerService;
    private readonly IHistoryService _historyService;
    private readonly IMemoryService _memoryService;
    private readonly ITodoService? _todoService;
    private readonly IKanbanColumnService? _columnService;
    private readonly IScheduledJobService? _scheduledJobService;
    private readonly IResearchHistoryService? _researchHistoryService;
    private readonly IE2EEService? _e2ee;
    private readonly IDeviceManagementService? _deviceMgmt;
    private readonly IDeviceKeyService? _deviceKeys;
    private readonly IPluginService? _pluginService;
    private readonly SyncMapper _mapper;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SyncClientService> _logger;
    private readonly SyncDeleteTrackerService _deleteTracker;

    private Timer? _syncTimer;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(5);
    private bool _hasVerifiedServerE2EEStatus;

    public bool IsSyncActive => _syncTimer is not null;
    public bool IsE2EEOnboardingRequired { get; private set; }
    public event EventHandler? E2EEOnboardingRequired;
    public event EventHandler? E2EEOnboardingCleared;
    public event EventHandler<PendingDeviceEventArgs>? PendingDeviceDetected;
    public event EventHandler? CurrentDeviceRevoked;

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
        IResearchHistoryService? researchHistoryService = null,
        IE2EEService? e2ee = null,
        IDeviceManagementService? deviceMgmt = null,
        IDeviceKeyService? deviceKeys = null,
        IPluginService? pluginService = null)
    {
        _authService = authService;
        _settingsService = settingsService;
        _templateService = templateService;
        _providerService = providerService;
        _historyService = historyService;
        _memoryService = memoryService;
        _todoService = todoService;
        _columnService = columnService;
        _scheduledJobService = scheduledJobService;
        _researchHistoryService = researchHistoryService;
        _e2ee = e2ee;
        _deviceMgmt = deviceMgmt;
        _deviceKeys = deviceKeys;
        _pluginService = pluginService;
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

        _syncTimer = new Timer(async _ =>
        {
            try { await SyncNowAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Background sync cycle failed"); }
        }, null, TimeSpan.FromSeconds(10), SyncInterval); // First run after 10 seconds

        _logger.LogInformation("Background sync started (interval: {Interval})", SyncInterval);
    }

    public void StopBackgroundSync()
    {
        _syncTimer?.Dispose();
        _syncTimer = null;
        _hasVerifiedServerE2EEStatus = false;
        _logger.LogInformation("Background sync stopped");
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

            // One-time server E2EE check: if local E2EE is off, verify against server
            // to catch cases where E2EE was enabled on another device (e.g., first-run
            // wizard login, app restart). Without this, sync would push IsE2EEEncrypted=false.
            if (!_hasVerifiedServerE2EEStatus && _deviceMgmt is not null && !settings.IsE2EEEnabled)
            {
                _hasVerifiedServerE2EEStatus = true;
                var serverStatus = await _deviceMgmt.CheckE2EEStatusAsync();
                if (serverStatus is { IsEnabled: true })
                {
                    _logger.LogWarning("E2EE enabled on server but not locally; onboarding required");
                    NotifyE2EEOnboardingRequired();
                    return null;
                }
            }

            var accessToken = await _authService.GetAccessTokenAsync();
            if (string.IsNullOrEmpty(accessToken))
                return null;

            var serverUrl = settings.ServerUrl.TrimEnd('/');
            using var client = CreateAuthenticatedClient(accessToken);

            // Push local changes
            var pushed = await PushChangesAsync(client, serverUrl, settings);

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

            // Check for pending devices (only if this device is active with E2EE)
            if (_deviceMgmt is not null && _e2ee?.IsReady() == true)
            {
                await CheckForPendingDevicesAsync();
            }

            result = new SyncResult(pushed, pulled, decryptErrors);
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

            var accessToken = await _authService.GetAccessTokenAsync();
            if (string.IsNullOrEmpty(accessToken))
                return;

            var serverUrl = settings.ServerUrl.TrimEnd('/');
            using var client = CreateAuthenticatedClient(accessToken);

            // Build full push request with all local data
            var templates = await _templateService.GetTemplatesAsync();
            var providers = await _providerService.GetProvidersAsync();
            var sessions = await _historyService.GetSessionsAsync(0, 10_000);
            var memories = await _memoryService.GetAllObjectsAsync();
            var kanbanColumns = _columnService is not null
                ? await _columnService.GetAllAsync()
                : Array.Empty<KanbanColumn>();
            var todos = _todoService is not null
                ? await _todoService.GetAllAsync()
                : [];

            var isE2EE = _e2ee?.IsReady() == true;
            var userId = isE2EE ? settings.SyncUserId : null;

            var request = new SyncPushRequest
            {
                ClientTimestamp = DateTime.UtcNow,
                LastSyncTimestamp = DateTime.MinValue,
                DeviceId = settings.SyncDeviceId,
                IsE2EEEncrypted = isE2EE,
                Settings = _mapper.ToSyncSettings(settings, userId),
                Templates = new SyncEntityChanges<SyncTemplate>
                {
                    Upserted = templates
                        .Where(t => !t.IsBuiltIn)
                        .Select(t => _mapper.ToSyncTemplate(t, userId))
                        .ToList()
                },
                Providers = new SyncEntityChanges<SyncProvider>
                {
                    Upserted = providers
                        .Where(p => p.ProviderType != AiProviderType.PiaCloud)
                        .Select(p => _mapper.ToSyncProvider(p, userId))
                        .ToList()
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
                        .Select(m => _mapper.ToSyncMemory(m, userId))
                        .ToList()
                },
                KanbanColumns = new SyncEntityChanges<SyncKanbanColumn>
                {
                    Upserted = kanbanColumns
                        .Select(c => _mapper.ToSyncKanbanColumn(c, userId))
                        .ToList()
                },
                Todos = new SyncEntityChanges<SyncTodo>
                {
                    Upserted = todos
                        .Select(t => _mapper.ToSyncTodo(t, userId))
                        .ToList()
                }
            };

            var response = await client.PostAsJsonAsync($"{serverUrl}/api/sync/push", request);
            await EnsureSuccessAsync(response, "First-sync push");

            _logger.LogInformation("First-sync push completed (templates: {Templates}, providers: {Providers}, sessions: {Sessions}, memories: {Memories}, kanbanColumns: {KanbanColumns}, todos: {Todos})",
                request.Templates.Upserted.Count, request.Providers.Upserted.Count,
                request.Sessions.Added.Count, request.Memories.Upserted.Count,
                request.KanbanColumns.Upserted.Count, request.Todos.Upserted.Count);

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

    public async Task ForceFullResyncAsync()
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

        await SyncNowAsync();
    }

    public async Task StopBackgroundSyncAndWaitAsync()
    {
        StopBackgroundSync();
        // Acquire and release the lock to ensure any in-progress sync is done
        await _syncLock.WaitAsync();
        _syncLock.Release();
    }

    private async Task<int> PushChangesAsync(HttpClient client, string serverUrl, AppSettings settings)
    {
        var lastSync = settings.LastSyncTimestamp ?? DateTime.MinValue;

        var templates = await _templateService.GetTemplatesAsync();
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
        var researchSessions = _researchHistoryService is not null
            ? await _researchHistoryService.GetModifiedSinceAsync(lastSync)
            : [];

        var dirtyTemplates = templates.Where(t => !t.IsBuiltIn).Where(t => (t.ModifiedAt ?? t.CreatedAt).ToUniversalTime() >= lastSync).Count();
        var dirtyProviders = providers.Where(p => p.ProviderType != AiProviderType.PiaCloud).Where(p => p.UpdatedAt.ToUniversalTime() >= lastSync).Count();
        var dirtySessions = sessions.Count;
        var dirtyMemories = memories.Where(m => m.UpdatedAt.ToUniversalTime() >= lastSync).Count();
        var dirtyKanbanCols = kanbanColumns.Where(c => c.UpdatedAt.ToUniversalTime() >= lastSync).Count();
        var dirtyTodos = todos.Where(t => t.UpdatedAt.ToUniversalTime() >= lastSync).Count();
        _logger.LogInformation("Push dirty tracking: {Templates}T, {Providers}P, {Sessions}S, {Memories}M, {KanbanCols}K, {Todos}Todo, {Jobs}Job, {Research}R changed since {LastSync}",
            dirtyTemplates, dirtyProviders, dirtySessions, dirtyMemories, dirtyKanbanCols, dirtyTodos,
            scheduledJobs.Count, researchSessions.Count, lastSync);

        var isE2EE = _e2ee?.IsReady() == true;
        var userId = isE2EE ? settings.SyncUserId : null;

        var pendingDeletes = _deleteTracker.GetPendingDeletes();

        var request = new SyncPushRequest
        {
            ClientTimestamp = DateTime.UtcNow,
            LastSyncTimestamp = lastSync,
            DeviceId = settings.SyncDeviceId,
            IsE2EEEncrypted = isE2EE,
            Settings = _mapper.ToSyncSettings(settings, userId),
            Templates = new SyncEntityChanges<SyncTemplate>
            {
                Upserted = templates
                    .Where(t => !t.IsBuiltIn)
                    .Where(t => (t.ModifiedAt ?? t.CreatedAt).ToUniversalTime() >= lastSync)
                    .Select(t => _mapper.ToSyncTemplate(t, userId))
                    .ToList(),
                Deleted = pendingDeletes.GetValueOrDefault("templates", [])
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
            ResearchSessions = new SyncEntityChanges<SyncResearchSession>
            {
                Upserted = researchSessions
                    .Select(r => _mapper.ToSyncResearchSession(r, userId))
                    .ToList(),
                Deleted = pendingDeletes.GetValueOrDefault("researchSessions", [])
            },
            PluginPreferences = _pluginService?.GetPendingPreferenceChanges() ?? []
        };

        _logger.LogInformation("Push pending deletes: {Templates}T, {Providers}P, {Memories}M, {Todos}Todo, {KanbanCols}K, {Jobs}Job, {Research}R",
            request.Templates.Deleted.Count, request.Providers.Deleted.Count,
            request.Memories.Deleted.Count, request.Todos.Deleted.Count,
            request.KanbanColumns.Deleted.Count,
            request.ScheduledJobs.Deleted.Count, request.ResearchSessions.Deleted.Count);

        var pushedCount = request.Templates.Upserted.Count
            + request.Providers.Upserted.Count
            + request.Sessions.Added.Count
            + request.Memories.Upserted.Count
            + request.KanbanColumns.Upserted.Count
            + request.Todos.Upserted.Count
            + request.ScheduledJobs.Upserted.Count
            + request.ResearchSessions.Upserted.Count;

        _logger.LogInformation(
            "Push request — Templates: {Templates}, Providers: {Providers}, Sessions: {Sessions}, Memories: {Memories}, KanbanColumns: {KanbanColumns}, Todos: {Todos}, ScheduledJobs: {Jobs}, ResearchSessions: {Research}, LastSync: {LastSync}, DeviceId: {DeviceId}, IsE2EE: {IsE2EE}",
            request.Templates.Upserted.Count, request.Providers.Upserted.Count,
            request.Sessions.Added.Count, request.Memories.Upserted.Count,
            request.KanbanColumns.Upserted.Count, request.Todos.Upserted.Count,
            request.ScheduledJobs.Upserted.Count, request.ResearchSessions.Upserted.Count,
            request.LastSyncTimestamp, request.DeviceId, request.IsE2EEEncrypted);

        // Short-circuit: skip HTTP POST when there are no changes to push
        if (pushedCount == 0 && pendingDeletes.Values.All(v => v.Count == 0))
        {
            _logger.LogInformation("Push short-circuited: no changes to push");
            return 0;
        }

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(request);
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
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Push failed with status {Status}", response.StatusCode);
            _logger.SensitiveDebug("Push failure body: {Body}", body);
            return 0;
        }

        var pushResponse = await response.Content.ReadFromJsonAsync<SyncPushResponse>();
        if (pushResponse is not null && pushResponse.Conflicts.Count > 0)
        {
            _logger.LogWarning("Push returned {ConflictCount} conflict(s)", pushResponse.Conflicts.Count);
            foreach (var c in pushResponse.Conflicts)
                _logger.LogWarning("Push conflict: {Entity} {Id}", c.Entity, c.Id);
        }

        _deleteTracker.ClearAfterSuccessfulPush();

        return pushedCount;
    }

    private async Task<(int Pulled, int DecryptionErrors, bool PullSucceeded, DateTime? ServerTimestamp)> PullChangesAsync(HttpClient client, string serverUrl, AppSettings settings)
    {
        var lastSync = settings.LastSyncTimestamp ?? DateTime.MinValue;
        // Ensure UTC Kind so ToString("O") includes the Z suffix — prevents Npgsql
        // timestamptz comparison failures when the server uses PostgreSQL.
        if (lastSync.Kind != DateTimeKind.Utc)
            lastSync = DateTime.SpecifyKind(lastSync, DateTimeKind.Utc);
        var since = lastSync.ToString("O");

        var pullUrl = $"{serverUrl}/api/sync/pull?since={since}";
        _logger.LogInformation("Pull requesting: {Url}", SafeUrl.Format(pullUrl));

        var pullRequest = new HttpRequestMessage(HttpMethod.Get, pullUrl);
        if (!string.IsNullOrEmpty(settings.LastPullETag))
            pullRequest.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(settings.LastPullETag));

        var pullSw = Stopwatch.StartNew();
        var response = await client.SendAsync(pullRequest);
        pullSw.Stop();
        _logger.LogInformation("Pull HTTP: {StatusCode} in {ElapsedMs}ms", (int)response.StatusCode, pullSw.ElapsedMilliseconds);

        if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
        {
            _logger.LogDebug("Pull returned 304 Not Modified — no changes since last sync");
            return (0, 0, true, null);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Pull failed with status {Status}", response.StatusCode);
            return (0, 0, false, null);
        }

        if (response.Headers.ETag is not null)
        {
            settings.LastPullETag = response.Headers.ETag.ToString();
            await _settingsService.SaveSettingsAsync(settings);
            _logger.LogDebug("Pull ETag stored: {ETag}", settings.LastPullETag);
        }

        var pullResponse = await response.Content.ReadFromJsonAsync<SyncPullResponse>();
        if (pullResponse is null) return (0, 0, false, null);

        _logger.LogInformation(
            "Pull response — ServerTimestamp: {ServerTs}, Templates: {TU}u/{TD}d, Providers: {PU}u/{PD}d, Sessions: {SA}a/{SD}d, Memories: {MU}u/{MD}d, KanbanColumns: {KCU}u/{KCD}d, Todos: {ToU}u/{ToD}d, Plugins: {PlU}u/{PlD}d",
            pullResponse.ServerTimestamp,
            pullResponse.Templates.Upserted.Count, pullResponse.Templates.Deleted.Count,
            pullResponse.Providers.Upserted.Count, pullResponse.Providers.Deleted.Count,
            pullResponse.Sessions.Added.Count, pullResponse.Sessions.Deleted.Count,
            pullResponse.Memories.Upserted.Count, pullResponse.Memories.Deleted.Count,
            pullResponse.KanbanColumns.Upserted.Count, pullResponse.KanbanColumns.Deleted.Count,
            pullResponse.Todos.Upserted.Count, pullResponse.Todos.Deleted.Count,
            pullResponse.Plugins.Upserted.Count, pullResponse.Plugins.Deleted.Count);

        var userId = settings.SyncUserId;

        var decryptionErrors = 0;
        var mergeInserted = 0;
        var mergeUpdated = 0;
        var mergeSkipped = 0;
        var mergeDeleted = 0;

        // Apply settings
        if (pullResponse.Settings is not null)
        {
            try
            {
                var currentSettings = await _settingsService.GetSettingsAsync();
                _mapper.ApplySyncSettings(pullResponse.Settings, currentSettings, userId);
                await _settingsService.SaveSettingsAsync(currentSettings);
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

        // Apply providers
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
                        var apiKey = (provider.EncryptedPayload is not null) ? null : provider.ApiKey;
                        await _providerService.UpdateProviderAsync(local, apiKey);
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
                }
                else
                {
                    var apiKey = (provider.EncryptedPayload is not null) ? null : provider.ApiKey;
                    await _providerService.AddProviderAsync(local, apiKey);
                    mergeInserted++;
                    _logger.LogInformation("Imported provider {Id}", provider.Id);
                    _logger.SensitiveDebug("Imported provider {Id} name: {Name}", provider.Id, local.Name);
                }
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

        // Apply research sessions
        if (_researchHistoryService is not null)
        {
            foreach (var syncEntry in pullResponse.ResearchSessions.Upserted)
            {
                try
                {
                    var local = _mapper.FromSyncResearchSession(syncEntry, userId);
                    var existing = await _researchHistoryService.GetEntryAsync(syncEntry.Id);

                    if (existing is null)
                    {
                        await _researchHistoryService.UpsertFromSyncAsync(local);
                        mergeInserted++;
                    }
                    else if (local.UpdatedAt.ToUniversalTime() >= existing.UpdatedAt.ToUniversalTime())
                    {
                        await _researchHistoryService.UpsertFromSyncAsync(local);
                        mergeUpdated++;
                        _logger.LogInformation("Updated research session {Id}", syncEntry.Id);
                    }
                    else
                    {
                        mergeSkipped++;
                    }
                }
                catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException
                                            or System.Security.Cryptography.AuthenticationTagMismatchException)
                {
                    decryptionErrors++;
                    _logger.LogWarning(ex, "Failed to decrypt synced research session {Id}; skipping", syncEntry.Id);
                }
            }

            foreach (var deletedId in pullResponse.ResearchSessions.Deleted)
            {
                _logger.LogDebug("Pull deleted: {EntityType} {Id}", "researchSessions", deletedId);
                await _researchHistoryService.DeleteEntryAsync(deletedId);
                mergeDeleted++;
            }
            if (pullResponse.ResearchSessions.Deleted.Count > 0)
                _logger.LogInformation("Pull {EntityType} deletions applied: {Count}", "researchSessions", pullResponse.ResearchSessions.Deleted.Count);
        }

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

        var pulledCount = pullResponse.Templates.Upserted.Count
            + pullResponse.Providers.Upserted.Count
            + pullResponse.Sessions.Added.Count
            + pullResponse.Memories.Upserted.Count
            + pullResponse.KanbanColumns.Upserted.Count
            + pullResponse.Todos.Upserted.Count
            + pullResponse.ScheduledJobs.Upserted.Count
            + pullResponse.ResearchSessions.Upserted.Count
            + pullResponse.Plugins.Upserted.Count;

        _logger.LogInformation("Pull merge: {Inserted} inserted, {Updated} updated, {Skipped} skipped, {Deleted} deleted, {DecryptErrors} decrypt errors",
            mergeInserted, mergeUpdated, mergeSkipped, mergeDeleted, decryptionErrors);

        return (pulledCount, decryptionErrors, true, pullResponse.ServerTimestamp);
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
