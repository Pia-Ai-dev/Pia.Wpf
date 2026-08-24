using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.Providers;

namespace Pia.Services;

public class ProviderService : JsonPersistenceService<List<AiProvider>>, IProviderService
{
    /// <summary>
    /// Well-known ID for the built-in Pia Cloud provider.
    /// Fixed GUID so it's consistent across installs and identifiable after sync.
    /// </summary>
    public static readonly Guid PiaCloudProviderId = new("00000000-0000-0000-0000-000000000001");

    public event EventHandler? ProvidersChanged;

    protected override string FileName => "providers.json";

    protected override List<AiProvider> CreateDefault() => [];

    private readonly ILogger<ProviderService> _logger;
    private readonly IAiClientService _aiClientService;
    private readonly DpapiHelper _dpapiHelper;
    private readonly ISettingsService _settingsService;
    private readonly IAuthService _authService;
    private readonly SyncDeleteTrackerService _deleteTracker;

    public ProviderService(
        ILogger<ProviderService> logger,
        IAiClientService aiClientService,
        DpapiHelper dpapiHelper,
        ISettingsService settingsService,
        IAuthService authService,
        SyncDeleteTrackerService deleteTracker)
    {
        _logger = logger;
        _aiClientService = aiClientService;
        _dpapiHelper = dpapiHelper;
        _settingsService = settingsService;
        _authService = authService;
        _deleteTracker = deleteTracker;
    }

    /// <summary>
    /// Every read of the provider list, with an assumed context window filled in for any provider that has
    /// none. Only OpenRouter reports one, and the field is otherwise hand-typed, so without this compaction
    /// never runs and an over-window chat fails at the provider instead.
    /// <para>
    /// Stamped into the loaded object rather than written back: the editor binds what it is given, so the
    /// user SEES the assumed window and can change it, and a value nobody edits stays out of providers.json.
    /// </para>
    /// </summary>
    private async Task<List<AiProvider>> LoadProvidersAsync()
    {
        var providers = await LoadAsync();

        foreach (var provider in providers)
            provider.MaxContextWindowTokens ??= ContextWindowDefaults.For(provider.ProviderType, provider.ModelName);

        return providers;
    }

    /// <summary>
    /// Re-reads what OpenRouter's default route serves for this model and stamps it on the provider. Called
    /// on every save, because the value moves: an alias id floats to whatever the author ships as current,
    /// and OpenRouter re-routes models between hosts.
    /// <para>
    /// <c>top_provider.context_length</c>, never the advertised <c>context_length</c> — they differ for
    /// dozens of models and the advertised one is the larger, so using it would size requests the route
    /// refuses. A failed lookup leaves whatever the snapshot resolved rather than failing the save; the
    /// endpoint is public, so this works before an API key is entered.
    /// </para>
    /// </summary>
    private async Task ApplyOpenRouterContextWindowAsync(AiProvider provider)
    {
        if (provider.ProviderType != AiProviderType.OpenRouter || string.IsNullOrWhiteSpace(provider.ModelName))
            return;

        provider.MaxContextWindowTokens ??= ContextWindowDefaults.For(provider.ProviderType, provider.ModelName);

        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var json = await httpClient.GetStringAsync("https://openrouter.ai/api/v1/models");

            if (OpenRouterModelCatalog.TryReadContextLength(json, provider.ModelName) is not { } served)
            {
                _logger.LogInformation("OpenRouter reported no context length for the configured model; keeping {Window}",
                    provider.MaxContextWindowTokens);
                return;
            }

            if (provider.MaxContextWindowTokens != served)
                _logger.LogInformation("OpenRouter context window moved {Old} -> {New}",
                    provider.MaxContextWindowTokens, served);

            provider.MaxContextWindowTokens = served;
        }
        catch (Exception ex)
        {
            // Never fail a save on this: the snapshot already put a usable number on the provider.
            _logger.LogWarning(ex, "Could not read the OpenRouter model list; keeping context window {Window}",
                provider.MaxContextWindowTokens);
        }
    }

    public async Task<IReadOnlyList<AiProvider>> GetProvidersAsync()
    {
        var providers = await LoadProvidersAsync();
        await MigrateEmptyProviderIdsAsync(providers);
        return providers.AsReadOnly();
    }

    /// <summary>
    /// One-time migration: providers created before the ProviderEditModel fix
    /// all got Id = Guid.Empty, causing badge/selection collisions.
    /// Assigns unique IDs and clears stale settings references.
    /// </summary>
    private async Task MigrateEmptyProviderIdsAsync(List<AiProvider> providers)
    {
        var emptyIdProviders = providers.Where(p => p.Id == Guid.Empty).ToList();
        if (emptyIdProviders.Count == 0)
            return;

        _logger.LogWarning("Migrating {Count} provider(s) with empty Guid IDs", emptyIdProviders.Count);

        foreach (var provider in emptyIdProviders)
            provider.Id = Guid.NewGuid();

        await SaveAsync(providers);

        // Settings referenced Guid.Empty — clear stale mode defaults
        var settings = await _settingsService.GetSettingsAsync();
        var staleKeys = settings.ModeProviderDefaults
            .Where(kv => kv.Value == Guid.Empty)
            .Select(kv => kv.Key)
            .ToList();

        if (staleKeys.Count > 0)
        {
            foreach (var key in staleKeys)
                settings.ModeProviderDefaults.Remove(key);

            await _settingsService.SaveSettingsAsync(settings);
            _logger.LogWarning("Cleared {Count} stale mode-provider default(s) referencing Guid.Empty", staleKeys.Count);
        }
    }

    public async Task<AiProvider?> GetProviderAsync(Guid id)
    {
        var providers = await GetProvidersAsync();
        return providers.FirstOrDefault(p => p.Id == id);
    }

    public async Task<AiProvider?> GetDefaultProviderAsync()
    {
        var providers = await GetProvidersAsync();
        return providers.FirstOrDefault();
    }

    public async Task<AiProvider?> GetDefaultProviderForModeAsync(WindowMode mode)
    {
        var settings = await _settingsService.GetSettingsAsync();
        var configuredId = settings.GetProviderForMode(mode);

        if (configuredId.HasValue)
        {
            var provider = await GetProviderAsync(configuredId.Value);
            if (provider is not null)
            {
                _logger.LogInformation(
                    "Resolved provider for mode {Mode}: configured={ConfiguredId} (UsedFallback=False)",
                    mode, configuredId.Value);
                _logger.SensitiveDebug(
                    "Resolved provider for mode {Mode}: {ProviderName}", mode, provider.Name);
                return provider;
            }
        }

        // Fallback: return the first provider (typically PiaCloud, which always exists).
        var fallback = await GetDefaultProviderAsync();
        _logger.LogInformation(
            "Resolved provider for mode {Mode}: configured={ConfiguredId} resolved={ResolvedId} (UsedFallback=True)",
            mode, configuredId, fallback?.Id);
        if (fallback is not null)
            _logger.SensitiveDebug("Fallback provider for mode {Mode}: {ProviderName}", mode, fallback.Name);
        return fallback;
    }

    public async Task<AiProvider> AddProviderAsync(AiProvider provider, string? apiKey)
    {
        var providers = await LoadProvidersAsync();

        await ApplyOpenRouterContextWindowAsync(provider);

        if (!string.IsNullOrEmpty(apiKey))
        {
            provider.EncryptedApiKey = _dpapiHelper.Encrypt(apiKey);
        }

        // If no real/configured provider exists yet, treat this as the first provider
        if (!providers.Any(p => p.ProviderType != AiProviderType.PiaCloud
            && !string.IsNullOrWhiteSpace(p.Endpoint)))
        {
            var settings = await _settingsService.GetSettingsAsync();
            settings.SetProviderForMode(WindowMode.Optimize, provider.Id);
            settings.SetProviderForMode(WindowMode.Assistant, provider.Id);
            settings.UseSameProviderForAllModes = true;
            await _settingsService.SaveSettingsAsync(settings);
        }

        providers.Add(provider);
        await SaveAsync(providers);
        ProvidersChanged?.Invoke(this, EventArgs.Empty);
        return provider;
    }

    public async Task UpdateProviderAsync(AiProvider provider, string? newApiKey = null)
    {
        var providers = await LoadProvidersAsync();
        var existing = providers.FirstOrDefault(p => p.Id == provider.Id);
        if (existing is null)
            throw new InvalidOperationException($"Provider with id {provider.Id} not found");

        var index = providers.IndexOf(existing);

        await ApplyOpenRouterContextWindowAsync(provider);

        // Preserve encrypted key if no new key provided — unless the incoming provider
        // already carries one (the E2EE pull path maps the synced key onto
        // EncryptedApiKey directly; clobbering it here would keep rotated keys from
        // ever propagating to other devices).
        if (string.IsNullOrEmpty(newApiKey))
        {
            if (string.IsNullOrEmpty(provider.EncryptedApiKey))
                provider.EncryptedApiKey = existing.EncryptedApiKey;
        }
        else
        {
            provider.EncryptedApiKey = _dpapiHelper.Encrypt(newApiKey);
        }

        providers[index] = provider;
        await SaveAsync(providers);
        ProvidersChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteProviderAsync(Guid id)
    {
        if (id == PiaCloudProviderId)
            throw new InvalidOperationException("The built-in Pia Cloud provider cannot be deleted.");

        var providers = await LoadProvidersAsync();
        var provider = providers.FirstOrDefault(p => p.Id == id);
        if (provider is null)
            return;

        providers.Remove(provider);
        await SaveAsync(providers);
        _deleteTracker.TrackDeletion("providers", id);
        ProvidersChanged?.Invoke(this, EventArgs.Empty);

        // Clean up any mode defaults pointing to deleted provider
        var settings = await _settingsService.GetSettingsAsync();
        var modified = false;
        foreach (var mode in Enum.GetValues<WindowMode>())
        {
            if (settings.ModeProviderDefaults.TryGetValue(mode, out var modeProviderId) && modeProviderId == id)
            {
                var replacement = providers.FirstOrDefault();
                if (replacement is not null)
                    settings.ModeProviderDefaults[mode] = replacement.Id;
                else
                    settings.ModeProviderDefaults.Remove(mode);
                modified = true;
            }
        }
        if (modified)
            await _settingsService.SaveSettingsAsync(settings);
    }

    public string? GetDecryptedApiKey(AiProvider provider)
    {
        if (string.IsNullOrEmpty(provider.EncryptedApiKey))
            return null;

        return _dpapiHelper.Decrypt(provider.EncryptedApiKey);
    }

    public async Task EnsureBuiltInProviderAsync()
    {
        var providers = await LoadProvidersAsync();
        var existing = providers.FirstOrDefault(p => p.Id == PiaCloudProviderId);
        if (existing is not null)
        {
            // Migrate: PiaCloud capabilities are server-determined, ensure they're enabled
            var updated = false;
            if (!existing.SupportsToolCalling)
            {
                existing.SupportsToolCalling = true;
                updated = true;
            }
            if (!existing.SupportsStreaming)
            {
                existing.SupportsStreaming = true;
                updated = true;
            }
            if (updated)
                await SaveAsync(providers);
            _logger.LogInformation("Built-in PiaCloud provider already present (CapsMigrated={Migrated})", updated);
            return;
        }

        var piaCloud = new AiProvider
        {
            Id = PiaCloudProviderId,
            Name = "Pia Cloud",
            ProviderType = AiProviderType.PiaCloud,
            Endpoint = "",
            SupportsToolCalling = true,
            SupportsStreaming = true,
            CreatedAt = DateTime.UtcNow
        };

        providers.Insert(0, piaCloud);
        await SaveAsync(providers);
        _logger.LogInformation("Built-in PiaCloud provider created with Id={Id}", piaCloud.Id);

        // Set as default for all modes if no other default is configured
        var settings = await _settingsService.GetSettingsAsync();
        if (settings.ModeProviderDefaults.Count == 0)
        {
            settings.SetProviderForMode(WindowMode.Optimize, piaCloud.Id);
            settings.SetProviderForMode(WindowMode.Assistant, piaCloud.Id);
            settings.UseSameProviderForAllModes = true;
            await _settingsService.SaveSettingsAsync(settings);
            _logger.LogInformation(
                "Seeded mode defaults to PiaCloud for Optimize/Assistant/Research (Id={Id})", piaCloud.Id);
        }
    }

    public async Task<TestConnectionResult> TestConnectionAsync(AiProvider provider, string? plainApiKey)
    {
        if (!string.IsNullOrEmpty(plainApiKey))
            provider.EncryptedApiKey = _dpapiHelper.Encrypt(plainApiKey);

        return await TestConnectionCoreAsync(provider, persist: false);
    }

    public Task<TestConnectionResult> TestConnectionAsync(AiProvider provider)
        => TestConnectionCoreAsync(provider, persist: true);

    private async Task<TestConnectionResult> TestConnectionCoreAsync(AiProvider provider, bool persist)
    {
        // PiaCloud: verify server reachability first, then run standard probes
        if (provider.ProviderType == AiProviderType.PiaCloud)
        {
            try
            {
                await _aiClientService.TestPiaCloudConnectionAsync();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Connection test failed: {ex.Message}", ex);
            }

            provider.SupportsToolCalling = true;
            provider.SupportsStreaming = true;
            if (persist) await UpdateProviderAsync(provider);
            return new TestConnectionResult(true, true, true);
        }
        else
        {
            try
            {
                var testPrompt = "Say 'Connection successful' if you can read this.";
                var response = await _aiClientService.SendRequestAsync(provider, testPrompt);
                if (string.IsNullOrWhiteSpace(response.Text))
                    throw new InvalidOperationException("Provider returned empty response");
            }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Connection test failed: {ex.Message}", ex);
            }
        }

        // Probe tool calling support
        bool supportsToolCalling;
        try
        {
            supportsToolCalling = await _aiClientService.TestToolCallingAsync(provider);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tool calling probe failed for provider {ProviderName}, assuming not supported", provider.Name);
            supportsToolCalling = false;
        }

        // Probe streaming support
        bool supportsStreaming;
        try
        {
            supportsStreaming = await _aiClientService.TestStreamingAsync(provider);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Streaming probe failed for provider {ProviderName}, assuming not supported", provider.Name);
            supportsStreaming = false;
        }

        // Auto-persist the result only for existing providers
        provider.SupportsToolCalling = supportsToolCalling;
        provider.SupportsStreaming = supportsStreaming;
        if (persist) await UpdateProviderAsync(provider);

        return new TestConnectionResult(true, supportsToolCalling, supportsStreaming);
    }

    public Task<bool> IsProviderActiveAsync(AiProvider provider)
    {
        if (provider.ProviderType == AiProviderType.PiaCloud)
            return Task.FromResult(_authService.IsLoggedIn);

        // Ollama doesn't require an API key
        if (provider.ProviderType == AiProviderType.Ollama)
            return Task.FromResult(!string.IsNullOrWhiteSpace(provider.Endpoint));

        return Task.FromResult(
            !string.IsNullOrWhiteSpace(provider.Endpoint)
            && !string.IsNullOrEmpty(provider.EncryptedApiKey));
    }

    public async Task<List<string>> FetchModelsAsync(string endpoint, string? apiKey, AiProviderType providerType)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("Endpoint is required to fetch models.");

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        if (!string.IsNullOrEmpty(apiKey))
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        string requestUrl;
        if (providerType == AiProviderType.Ollama)
        {
            // Ollama's model list is at /api/tags, outside the /v1 compat path
            var baseUrl = endpoint.TrimEnd('/');
            if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                baseUrl = baseUrl[..^3];
            requestUrl = $"{baseUrl}/api/tags";
        }
        else
        {
            // OpenAI and OpenAI-compatible endpoints
            requestUrl = $"{endpoint.TrimEnd('/')}/models";
        }

        _logger.LogInformation("Fetching models from {Url} for provider type {ProviderType}",
            SafeUrl.Format(requestUrl), providerType);

        var response = await httpClient.GetAsync(requestUrl);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var models = new List<string>();

        if (providerType == AiProviderType.Ollama)
        {
            // Ollama response: { "models": [{ "name": "llama3:latest", ... }] }
            if (doc.RootElement.TryGetProperty("models", out var modelsArray))
            {
                foreach (var model in modelsArray.EnumerateArray())
                {
                    if (model.TryGetProperty("name", out var name))
                        models.Add(name.GetString()!);
                }
            }
        }
        else
        {
            // OpenAI response: { "data": [{ "id": "gpt-4o", ... }] }
            if (doc.RootElement.TryGetProperty("data", out var dataArray))
            {
                foreach (var model in dataArray.EnumerateArray())
                {
                    if (model.TryGetProperty("id", out var id))
                        models.Add(id.GetString()!);
                }
            }
        }

        models.Sort(StringComparer.OrdinalIgnoreCase);
        _logger.LogInformation("Fetched {Count} models from {Url}", models.Count, SafeUrl.Format(requestUrl));
        return models;
    }

    public async Task ReassignProviderIdAsync(Guid oldId, Guid newId, AiProvider merged)
    {
        if (oldId == PiaCloudProviderId || newId == PiaCloudProviderId)
            throw new InvalidOperationException("PiaCloud provider Id is fixed and cannot be reassigned.");
        if (oldId == newId)
            return;

        var providers = await LoadProvidersAsync();
        var index = providers.FindIndex(p => p.Id == oldId);
        if (index < 0)
        {
            _logger.LogWarning(
                "ReassignProviderIdAsync: local row {OldId} not found; treating incoming {NewId} as new",
                oldId, newId);
            return;
        }

        var localApiKey = providers[index].EncryptedApiKey;
        merged.Id = newId;
        // Preserve the locally-stored encrypted API key (DPAPI-bound to this machine);
        // the wire payload doesn't carry a decryptable key for other devices.
        if (string.IsNullOrEmpty(merged.EncryptedApiKey) && !string.IsNullOrEmpty(localApiKey))
            merged.EncryptedApiKey = localApiKey;

        providers[index] = merged;
        await SaveAsync(providers);

        _logger.LogInformation("Reassigned provider Id {OldId} -> {NewId}", oldId, newId);
        _logger.SensitiveDebug("Reassigned provider name: {Name}", merged.Name);

        // Rewrite any mode defaults pointing at oldId.
        var settings = await _settingsService.GetSettingsAsync();
        var rewroteModes = false;
        foreach (var mode in Enum.GetValues<WindowMode>())
        {
            if (settings.ModeProviderDefaults.TryGetValue(mode, out var current) && current == oldId)
            {
                settings.ModeProviderDefaults[mode] = newId;
                rewroteModes = true;
                _logger.LogInformation("Rewrote mode default {Mode}: {OldId} -> {NewId}", mode, oldId, newId);
            }
        }
        if (rewroteModes)
            await _settingsService.SaveSettingsAsync(settings);

        ProvidersChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RepairModeDefaultsAsync()
    {
        var providers = await LoadProvidersAsync();
        var settings = await _settingsService.GetSettingsAsync();

        var providerIds = new HashSet<Guid>(providers.Select(p => p.Id));
        var hasPiaCloud = providerIds.Contains(PiaCloudProviderId);
        var fallback = hasPiaCloud ? PiaCloudProviderId : providers.FirstOrDefault()?.Id;

        var repaired = 0;
        var removed = 0;
        var modified = false;

        foreach (var mode in Enum.GetValues<WindowMode>())
        {
            if (!settings.ModeProviderDefaults.TryGetValue(mode, out var current))
                continue;

            if (providerIds.Contains(current))
                continue;

            // Stale reference.
            if (fallback.HasValue)
            {
                settings.ModeProviderDefaults[mode] = fallback.Value;
                repaired++;
                _logger.LogWarning(
                    "Mode-default for {Mode} pointed to missing provider {OldId}, replaced with {NewId}",
                    mode, current, fallback.Value);
            }
            else
            {
                settings.ModeProviderDefaults.Remove(mode);
                removed++;
                _logger.LogWarning(
                    "Mode-default for {Mode} pointed to missing provider {OldId}; removed (no providers available)",
                    mode, current);
            }
            modified = true;
        }

        if (modified)
            await _settingsService.SaveSettingsAsync(settings);

        settings.ModeProviderDefaults.TryGetValue(WindowMode.Optimize, out var optId);
        settings.ModeProviderDefaults.TryGetValue(WindowMode.Assistant, out var asstId);
        _logger.LogInformation(
            "Mode-default repair: {Repaired} repaired, {Removed} removed (Optimize={OptId} Assistant={AsstId})",
            repaired, removed, optId, asstId);
    }

    public async Task ConsolidateLocalDuplicatesAsync()
    {
        var providers = await LoadProvidersAsync();
        if (providers.Count <= 1)
            return;

        // Build fingerprint groups, keeping the row with the most recent UpdatedAt as the survivor.
        var groups = providers
            .Where(p => p.Id != PiaCloudProviderId && p.ProviderType != AiProviderType.PiaCloud)
            .GroupBy(ProviderFingerprint.Compute)
            .Where(g => g.Count() > 1)
            .ToList();

        if (groups.Count == 0)
            return;

        var settings = await _settingsService.GetSettingsAsync();
        var modifiedSettings = false;
        var collapsed = 0;

        foreach (var group in groups)
        {
            var ordered = group.OrderByDescending(p => p.UpdatedAt.ToUniversalTime()).ToList();
            var survivor = ordered[0];
            var duplicates = ordered.Skip(1).ToList();

            foreach (var dup in duplicates)
            {
                // Preserve the duplicate's locally-stored API key (DPAPI-bound to this
                // machine) if the survivor has none. Pulled rows arrive without a
                // decryptable key for this device, so a fresh-from-sync survivor would
                // otherwise wipe a user-configured key when consolidating.
                if (string.IsNullOrEmpty(survivor.EncryptedApiKey)
                    && !string.IsNullOrEmpty(dup.EncryptedApiKey))
                {
                    survivor.EncryptedApiKey = dup.EncryptedApiKey;
                }

                providers.Remove(dup);
                _logger.LogInformation(
                    "Consolidated duplicate provider {DupId} into survivor {SurvivorId} (Fingerprint match)",
                    dup.Id, survivor.Id);

                foreach (var mode in Enum.GetValues<WindowMode>())
                {
                    if (settings.ModeProviderDefaults.TryGetValue(mode, out var current) && current == dup.Id)
                    {
                        settings.ModeProviderDefaults[mode] = survivor.Id;
                        modifiedSettings = true;
                        _logger.LogInformation(
                            "Rewrote mode default {Mode}: {DupId} -> {SurvivorId}", mode, dup.Id, survivor.Id);
                    }
                }
                collapsed++;
            }
        }

        if (collapsed == 0)
            return;

        await SaveAsync(providers);
        if (modifiedSettings)
            await _settingsService.SaveSettingsAsync(settings);
        _logger.LogInformation("Local consolidation collapsed {Count} duplicate provider row(s)", collapsed);
        ProvidersChanged?.Invoke(this, EventArgs.Empty);
    }
}
