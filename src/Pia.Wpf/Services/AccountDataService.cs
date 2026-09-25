using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services;

public sealed class AccountDataService : IAccountDataService
{
    private readonly ISettingsService _settings;
    private readonly IAuthService _auth;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AccountDataService> _logger;

    public AccountDataService(
        ISettingsService settings,
        IAuthService auth,
        IHttpClientFactory httpFactory,
        ILogger<AccountDataService> logger)
    {
        _settings = settings;
        _auth = auth;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task ExportAsync(Stream destination, CancellationToken ct = default)
    {
        // The archive is built while it streams, so its size gives no upper bound worth a timeout.
        using var client = await CreateAuthorizedClientAsync(Timeout.InfiniteTimeSpan);
        using var response = await client.GetAsync("auth/account/export", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await response.Content.CopyToAsync(destination, ct);
    }

    public async Task ExportToFileAsync(string path, CancellationToken ct = default)
    {
        try
        {
            await using var file = File.Create(path);
            await ExportAsync(file, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Account export failed");
            _logger.SensitiveDebug("Account export target: {Path}", path);
            File.Delete(path);
            throw;
        }
    }

    public async Task<AccountDeletionOutcome> DeleteAsync(string? password, CancellationToken ct = default)
    {
        using var client = await CreateAuthorizedClientAsync(TimeSpan.FromSeconds(30));
        using var response = await client.PostAsJsonAsync(
            "auth/account/delete", new { confirm = "DELETE", password }, ct);

        if (response.IsSuccessStatusCode)
            return AccountDeletionOutcome.Deleted;

        var code = await ReadErrorCodeAsync(response, ct);
        _logger.LogWarning("Account deletion refused: {Status} {Code}", (int)response.StatusCode, code);
        return code == "invalid_password" ? AccountDeletionOutcome.InvalidPassword : AccountDeletionOutcome.Failed;
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return body.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<HttpClient> CreateAuthorizedClientAsync(TimeSpan timeout)
    {
        var settings = await _settings.GetSettingsAsync();
        var client = _httpFactory.CreateClient();
        client.BaseAddress = new Uri(settings.ServerUrl ?? throw new InvalidOperationException("Server URL not configured"));
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _auth.GetAccessTokenAsync());
        client.Timeout = timeout;
        return client;
    }
}
