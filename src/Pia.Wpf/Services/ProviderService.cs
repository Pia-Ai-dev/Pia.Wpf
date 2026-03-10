using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services.Interfaces;

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

    public ProviderService(
        ILogger<ProviderService> logger,
        IAiClientService aiClientService,
        DpapiHelper dpapiHelper,
        ISettingsService settingsService,
        IAuthService authService)
    {
        _logger = logger;
        _aiClientService = aiClientService;
        _dpapiHelper = dpapiHelper;
        _settingsService = settingsService;
        _authService = authService;
    }

    public async Task<IReadOnlyList<AiProvider>> GetProvidersAsync()
    {
        var providers = await LoadAsync();
        return providers.AsReadOnly();
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
        var providerId = settings.GetProviderForMode(mode);

        if (providerId.HasValue)
        {
            var provider = await GetProviderAsync(providerId.Value);
            if (provider is not null)
                return provider;
        }

        // Fallback: return the first provider
        return await GetDefaultProviderAsync();
    }

    public async Task<AiProvider> AddProviderAsync(AiProvider provider, string? apiKey)
    {
        var providers = await LoadAsync();

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
            settings.SetProviderForMode(WindowMode.Research, provider.Id);
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
        var providers = await LoadAsync();
        var existing = providers.FirstOrDefault(p => p.Id == provider.Id);
        if (existing is null)
            throw new InvalidOperationException($"Provider with id {provider.Id} not found");

        var index = providers.IndexOf(existing);

        // Preserve encrypted key if no new key provided
        if (string.IsNullOrEmpty(newApiKey))
        {
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

        var providers = await LoadAsync();
        var provider = providers.FirstOrDefault(p => p.Id == id);
        if (provider is null)
            return;

        providers.Remove(provider);
        await SaveAsync(providers);
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
        var providers = await LoadAsync();
        if (providers.Any(p => p.Id == PiaCloudProviderId))
            return;

        var piaCloud = new AiProvider
        {
            Id = PiaCloudProviderId,
            Name = "Pia Cloud",
            ProviderType = AiProviderType.PiaCloud,
            Endpoint = "",
            SupportsToolCalling = false,
            CreatedAt = DateTime.UtcNow
        };

        providers.Insert(0, piaCloud);
        await SaveAsync(providers);

        // Set as default for all modes if no other default is configured
        var settings = await _settingsService.GetSettingsAsync();
        if (settings.ModeProviderDefaults.Count == 0)
        {
            settings.SetProviderForMode(WindowMode.Optimize, piaCloud.Id);
            settings.SetProviderForMode(WindowMode.Assistant, piaCloud.Id);
            settings.SetProviderForMode(WindowMode.Research, piaCloud.Id);
            settings.UseSameProviderForAllModes = true;
            await _settingsService.SaveSettingsAsync(settings);
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
        // PiaCloud: just hit /api/ai/status — no chat completions endpoint
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

            provider.SupportsToolCalling = false;
            provider.SupportsStreaming = false;
            if (persist) await UpdateProviderAsync(provider);
            return new TestConnectionResult(true, false, false);
        }

        try
        {
            var testPrompt = "Say 'Connection successful' if you can read this.";
            var response = await _aiClientService.SendRequestAsync(provider, testPrompt);
            if (string.IsNullOrWhiteSpace(response))
                throw new InvalidOperationException("Provider returned empty response");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Connection test failed: {ex.Message}", ex);
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

        _logger.LogInformation("Fetching models from {Url} for provider type {ProviderType}", requestUrl, providerType);

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
        _logger.LogInformation("Fetched {Count} models from {Url}", models.Count, requestUrl);
        return models;
    }
}
