using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
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
    private readonly IE2EEService? _e2ee;
    private readonly IDeviceManagementService? _deviceMgmt;
    private readonly IDeviceKeyService? _deviceKeys;
    private readonly SyncMapper _mapper;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SyncClientService> _logger;

    private Timer? _syncTimer;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(5);
    private bool _hasVerifiedServerE2EEStatus;

    public bool IsSyncActive => _syncTimer is not null;
    public event EventHandler? E2EEOnboardingRequired;
    public event EventHandler<PendingDeviceEventArgs>? PendingDeviceDetected;
    public event EventHandler? CurrentDeviceRevoked;

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
        ITodoService? todoService = null,
        IE2EEService? e2ee = null,
        IDeviceManagementService? deviceMgmt = null,
        IDeviceKeyService? deviceKeys = null)
    {
        _authService = authService;
        _settingsService = settingsService;
        _templateService = templateService;
        _providerService = providerService;
        _historyService = historyService;
        _memoryService = memoryService;
        _todoService = todoService;
        _e2ee = e2ee;
        _deviceMgmt = deviceMgmt;
        _deviceKeys = deviceKeys;
        _mapper = mapper;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
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
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            if (!settings.SyncEnabled || string.IsNullOrEmpty(settings.ServerUrl))
                return null;

            // E2EE initialization check: if E2EE is enabled but UMK not available,
            // this is a second device that needs onboarding before sync can proceed.
            if (_e2ee is not null && settings.IsE2EEEnabled && !_e2ee.IsReady())
            {
                _logger.LogWarning("E2EE enabled but UMK not available; onboarding required");
                E2EEOnboardingRequired?.Invoke(this, EventArgs.Empty);
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
                    E2EEOnboardingRequired?.Invoke(this, EventArgs.Empty);
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
            var (pulled, decryptErrors) = await PullChangesAsync(client, serverUrl, settings);

            // Update last sync timestamp
            settings.LastSyncTimestamp = DateTime.UtcNow;
            await _settingsService.SaveSettingsAsync(settings);

            // Check for pending devices (only if this device is active with E2EE)
            if (_deviceMgmt is not null && _e2ee?.IsReady() == true)
            {
                await CheckForPendingDevicesAsync();
            }

            result = new SyncResult(pushed, pulled, decryptErrors);
            _logger.LogInformation("Sync cycle completed successfully");
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
                Todos = new SyncEntityChanges<SyncTodo>
                {
                    Upserted = todos
                        .Select(t => _mapper.ToSyncTodo(t, userId))
                        .ToList()
                }
            };

            var response = await client.PostAsJsonAsync($"{serverUrl}/api/sync/push", request);
            await EnsureSuccessAsync(response, "First-sync push");

            settings.LastSyncTimestamp = DateTime.UtcNow;
            await _settingsService.SaveSettingsAsync(settings);

            _logger.LogInformation("First-sync migration completed (templates: {Templates}, providers: {Providers}, sessions: {Sessions}, memories: {Memories}, todos: {Todos})",
                request.Templates.Upserted.Count, request.Providers.Upserted.Count,
                request.Sessions.Added.Count, request.Memories.Upserted.Count,
                request.Todos.Upserted.Count);
        }
        finally
        {
            _syncLock.Release();
        }
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
        // For the initial implementation, push all data.
        // A future optimization would track changed entities since last push
        // and only send those (using a local change queue).
        var templates = await _templateService.GetTemplatesAsync();
        var providers = await _providerService.GetProvidersAsync();

        // Only push sessions created since last sync
        var lastSync = settings.LastSyncTimestamp ?? DateTime.MinValue;
        var sessions = await _historyService.SearchSessionsAsync(fromDate: lastSync);

        var memories = await _memoryService.GetAllObjectsAsync();
        var todos = _todoService is not null
            ? await _todoService.GetAllAsync()
            : [];

        var isE2EE = _e2ee?.IsReady() == true;
        var userId = isE2EE ? settings.SyncUserId : null;

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
            Todos = new SyncEntityChanges<SyncTodo>
            {
                Upserted = todos
                    .Select(t => _mapper.ToSyncTodo(t, userId))
                    .ToList()
            }
        };

        var pushedCount = request.Templates.Upserted.Count
            + request.Providers.Upserted.Count
            + request.Sessions.Added.Count
            + request.Memories.Upserted.Count
            + request.Todos.Upserted.Count;

        var response = await client.PostAsJsonAsync($"{serverUrl}/api/sync/push", request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Push failed with status {Status}: {Body}", response.StatusCode, body);
            return 0;
        }

        return pushedCount;
    }

    private async Task<(int Pulled, int DecryptionErrors)> PullChangesAsync(HttpClient client, string serverUrl, AppSettings settings)
    {
        var lastSync = settings.LastSyncTimestamp ?? DateTime.MinValue;
        var since = lastSync.ToString("O");

        var response = await client.GetAsync($"{serverUrl}/api/sync/pull?since={since}");
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Pull failed with status {Status}", response.StatusCode);
            return (0, 0);
        }

        var pullResponse = await response.Content.ReadFromJsonAsync<SyncPullResponse>();
        if (pullResponse is null) return (0, 0);

        var userId = settings.SyncUserId;

        var decryptionErrors = 0;

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
                    var remoteTime = (local.ModifiedAt ?? local.CreatedAt).ToUniversalTime();
                    var localTime = (existing.ModifiedAt ?? existing.CreatedAt).ToUniversalTime();

                    if (remoteTime >= localTime)
                    {
                        await _templateService.UpdateTemplateAsync(local);
                        _logger.LogInformation("Updated template {Id}: {Name}", template.Id, local.Name);
                    }
                    else
                    {
                        _logger.LogDebug("Skipped template {Id}: local is newer (local={Local}, remote={Remote})",
                            template.Id, localTime, remoteTime);
                    }
                }
                else
                {
                    await _templateService.AddTemplateAsync(local);
                    _logger.LogInformation("Imported template {Id}: {Name}", template.Id, local.Name);
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
            await _templateService.DeleteTemplateAsync(deletedId);
        }

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
                        _logger.LogInformation("Updated provider {Id}: {Name}", provider.Id, local.Name);
                    }
                    else
                    {
                        _logger.LogDebug("Skipped provider {Id}: local is newer (local={Local}, remote={Remote})",
                            provider.Id, existing.UpdatedAt, local.UpdatedAt);
                    }
                }
                else
                {
                    var apiKey = (provider.EncryptedPayload is not null) ? null : provider.ApiKey;
                    await _providerService.AddProviderAsync(local, apiKey);
                    _logger.LogInformation("Imported provider {Id}: {Name}", provider.Id, local.Name);
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
            await _providerService.DeleteProviderAsync(deletedId);
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
                    _logger.LogInformation("Imported session {Id}", session.Id);
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
            await _historyService.DeleteSessionAsync(deletedId);
        }

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
                        _logger.LogInformation("Updated memory {Id}: {Label}", memory.Id, local.Label);
                    }
                    else
                    {
                        _logger.LogDebug("Skipped memory {Id}: local is newer (local={Local}, remote={Remote})",
                            memory.Id, existing.UpdatedAt, local.UpdatedAt);
                    }
                }
                else
                {
                    await _memoryService.ImportObjectAsync(local);
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
            await _memoryService.DeleteObjectAsync(deletedId);
        }

        // Apply todos
        if (_todoService is not null)
        {
            foreach (var todo in pullResponse.Todos.Upserted)
            {
                try
                {
                    var local = _mapper.FromSyncTodo(todo, userId);
                    var existing = await _todoService.GetAsync(todo.Id);

                    if (existing is not null)
                    {
                        if (local.UpdatedAt.ToUniversalTime() >= existing.UpdatedAt.ToUniversalTime())
                        {
                            await _todoService.ImportAsync(local);
                            _logger.LogInformation("Updated todo {Id}: {Title}", todo.Id, local.Title);
                        }
                        else
                        {
                            _logger.LogDebug("Skipped todo {Id}: local is newer (local={Local}, remote={Remote})",
                                todo.Id, existing.UpdatedAt, local.UpdatedAt);
                        }
                    }
                    else
                    {
                        await _todoService.ImportAsync(local);
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
                await _todoService.DeleteAsync(deletedId);
            }
        }

        if (decryptionErrors > 0)
        {
            _logger.LogWarning("Pull completed with {Count} decryption error(s) — data may have been encrypted with a different key", decryptionErrors);
        }

        var pulledCount = pullResponse.Templates.Upserted.Count
            + pullResponse.Providers.Upserted.Count
            + pullResponse.Sessions.Added.Count
            + pullResponse.Memories.Upserted.Count
            + pullResponse.Todos.Upserted.Count;

        _logger.LogInformation("Pull applied: {Templates}T, {Providers}P, {Sessions}S, {Memories}M, {Todos}Todo",
            pullResponse.Templates.Upserted.Count,
            pullResponse.Providers.Upserted.Count,
            pullResponse.Sessions.Added.Count,
            pullResponse.Memories.Upserted.Count,
            pullResponse.Todos.Upserted.Count);

        return (pulledCount, decryptionErrors);
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync();
        _logger.LogError("{Operation} failed ({Status}): {Body}", operation, (int)response.StatusCode, body);
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
        _syncTimer?.Dispose();
        _syncLock.Dispose();
    }
}
