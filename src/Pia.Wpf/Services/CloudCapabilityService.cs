using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services;

public sealed class CloudCapabilityService : ICloudCapabilityService
{
    private readonly ISettingsService _settingsService;
    private readonly IAuthService _authService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CloudCapabilityService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool? _chatsSupported;

    public CloudCapabilityService(
        ISettingsService settingsService,
        IAuthService authService,
        IHttpClientFactory httpClientFactory,
        ILogger<CloudCapabilityService> logger)
    {
        _settingsService = settingsService;
        _authService = authService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<bool> ChatsSupportedAsync(CancellationToken ct = default)
    {
        if (_chatsSupported.HasValue) return _chatsSupported.Value;

        await _gate.WaitAsync(ct);
        try
        {
            if (_chatsSupported.HasValue) return _chatsSupported.Value;

            var supported = await ProbeAsync(ct);
            _chatsSupported = supported;
            _logger.LogInformation("Cloud capability probe: chats supported = {Supported}", supported);
            return supported;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> ProbeAsync(CancellationToken ct)
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            var serverUrl = settings.ServerUrl?.TrimEnd('/');
            if (string.IsNullOrEmpty(serverUrl)) return false;

            var url = $"{serverUrl}/api/capabilities";
            var client = _httpClientFactory.CreateClient();

            var token = await _authService.GetAccessTokenAsync();
            if (!string.IsNullOrEmpty(token))
            {
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Capability probe non-success status {Status}", (int)response.StatusCode);
                return false;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("chats", out var chatsProp)) return false;
            return chatsProp.ValueKind == JsonValueKind.True;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Capability probe failed: {Type}", ex.GetType().Name);
            return false;
        }
    }
}
