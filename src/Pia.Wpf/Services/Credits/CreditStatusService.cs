using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services.Credits;

public sealed class CreditStatusService : ICreditStatusService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ISettingsService _settings;
    private readonly IAuthService _auth;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CreditStatusService> _logger;

    public CreditStatusService(
        ISettingsService settings, IAuthService auth, IHttpClientFactory httpClientFactory,
        ILogger<CreditStatusService> logger)
    {
        _settings = settings;
        _auth = auth;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // Null for every "nothing to show" answer, 403 and 404 included: a server without the AI proxy, or one
    // older than the endpoint, simply has no credit view.
    public async Task<CreditStatusResponse?> GetAsync(CancellationToken ct = default)
    {
        var serverUrl = (await _settings.GetSettingsAsync()).ServerUrl?.TrimEnd('/');
        if (string.IsNullOrEmpty(serverUrl)) return null;

        var token = await _auth.GetAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return null;

        using var http = _httpClientFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using var response = await http.GetAsync($"{serverUrl}/api/ai/credits", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Credit status from {Url} returned {Status}.", SafeUrl.Format(serverUrl), (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<CreditStatusResponse>(JsonOptions, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Credit status from {Url} could not be read.", SafeUrl.Format(serverUrl));
            return null;
        }
    }
}
