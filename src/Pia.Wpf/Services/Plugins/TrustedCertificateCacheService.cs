using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;
using Pia.Shared.Models;

namespace Pia.Services.Plugins;

public class TrustedCertificateCacheService
{
    private readonly ILogger<TrustedCertificateCacheService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAuthService _authService;
    private readonly ISettingsService _settingsService;
    private IReadOnlyList<SyncTrustedCertificate> _cached = [];
    private DateTime _lastFetched = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public TrustedCertificateCacheService(
        ILogger<TrustedCertificateCacheService> logger,
        IHttpClientFactory httpClientFactory,
        IAuthService authService,
        ISettingsService settingsService)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _authService = authService;
        _settingsService = settingsService;
    }

    public async Task<IReadOnlyList<SyncTrustedCertificate>> GetCertificatesAsync(CancellationToken ct = default)
    {
        if (DateTime.UtcNow - _lastFetched < CacheTtl && _cached.Count > 0)
            return _cached;

        try
        {
            var accessToken = await _authService.GetAccessTokenAsync();
            if (string.IsNullOrEmpty(accessToken))
            {
                _logger.LogWarning("No access token available, using cached certificates");
                return _cached;
            }

            var settings = await _settingsService.GetSettingsAsync();
            if (string.IsNullOrEmpty(settings.ServerUrl))
            {
                _logger.LogWarning("No server URL configured, using cached certificates");
                return _cached;
            }

            var serverUrl = settings.ServerUrl.TrimEnd('/');
            using var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);
            client.Timeout = TimeSpan.FromSeconds(30);

            var certs = await client.GetFromJsonAsync<List<SyncTrustedCertificate>>(
                $"{serverUrl}/api/certificates/trusted", ct);

            if (certs is not null)
            {
                _cached = certs;
                _lastFetched = DateTime.UtcNow;
                _logger.LogDebug("Fetched {Count} trusted certificates", certs.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch trusted certificates, using cache");
        }

        return _cached;
    }
}
