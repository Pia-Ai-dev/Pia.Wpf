using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
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

    internal TimeSpan ExportTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public async Task ExportAsync(Stream destination, CancellationToken ct = default)
    {
        // HttpClient.Timeout stops counting at the headers, so the deadline also has to cover the body.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ExportTimeout);
        using var client = await CreateAuthorizedClientAsync(Timeout.InfiniteTimeSpan);
        using var response = await client.GetAsync(
            "auth/account/export", HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        await response.Content.CopyToAsync(destination, deadline.Token);
    }

    public async Task ExportToFileAsync(string path, CancellationToken ct = default)
    {
        var tempPath = AtomicBinaryWriter.CreateTempPath(path);
        try
        {
            await using (var file = File.Create(tempPath))
                await ExportAsync(file, ct);
            AtomicBinaryWriter.CommitTempFile(tempPath, path);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Account export failed");
            _logger.SensitiveDebug("Account export target: {Path}", path);
            throw;
        }
        finally
        {
            AtomicBinaryWriter.DiscardTempFile(tempPath);
        }
    }

    public async Task<AccountDeletionOutcome> DeleteAsync(string? password, CancellationToken ct = default)
    {
        using var client = await CreateAuthorizedClientAsync(TimeSpan.FromSeconds(30));
        using var response = await PostDeleteAsync(client, new { confirm = "DELETE", password }, ct);

        if (response.IsSuccessStatusCode)
            return AccountDeletionOutcome.Deleted;

        var code = await ReadErrorCodeAsync(response, ct);
        if (code == "user_not_found")
        {
            _logger.LogInformation("Account deletion: the server no longer has the account; treating it as deleted");
            return AccountDeletionOutcome.Deleted;
        }

        _logger.LogWarning("Account deletion refused: {Status} {Code}", (int)response.StatusCode, code);
        return code == "invalid_password" ? AccountDeletionOutcome.InvalidPassword : AccountDeletionOutcome.Failed;
    }

    private async Task<HttpResponseMessage> PostDeleteAsync(HttpClient client, object body, CancellationToken ct)
    {
        try
        {
            return await client.PostAsJsonAsync("auth/account/delete", body, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Deleting twice is harmless, and the second answer says whether the first went through.
            _logger.LogWarning(ex, "Account deletion got no answer; sending it once more");
            return await client.PostAsJsonAsync("auth/account/delete", body, ct);
        }
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
        var serverUrl = (await _settings.GetSettingsAsync()).ServerUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(serverUrl))
            throw new InvalidOperationException("Server URL not configured");

        var client = _httpFactory.CreateClient();
        // Without the trailing slash a relative request path would replace the URL's last segment.
        client.BaseAddress = new Uri(serverUrl + "/");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _auth.GetAccessTokenAsync());
        client.Timeout = timeout;
        return client;
    }
}
