using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Services.Interfaces;
using Pia.Shared.Operators;

namespace Pia.Services.Operators;

/// <summary>
/// The <c>/api/assignments</c> half of the Pia server. This is the ONE plane where content leaves the device
/// unencrypted, so nothing here is called speculatively — every method is reached from the consent flow or
/// from the resume pass for a run that already has consent behind it.
/// </summary>
public interface IAssignmentApiClient
{
    /// <summary>What the picker renders from, and the availability gate. Anything other than a populated
    /// list — no server configured, no token, 401/403/404, or an empty array — hides the surface.</summary>
    Task<AssignmentSurface> GetSurfaceAsync(CancellationToken ct = default);

    /// <summary>Null when the server refused or could not be reached; the reason is logged.</summary>
    Task<Guid?> CreateAsync(string skillName, AssignmentInput input, CancellationToken ct = default);

    Task<AssignmentDto?> GetAsync(Guid assignmentId, CancellationToken ct = default);

    /// <summary>The irreversible acknowledgement. True for a drop AND for a repeat, which is what makes the
    /// resume pass safe to run twice.</summary>
    Task<bool> CollectAsync(Guid assignmentId, CancellationToken ct = default);
}

/// <inheritdoc cref="IAssignmentApiClient"/>
public sealed class AssignmentApiClient : IAssignmentApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ISettingsService _settings;
    private readonly IAuthService _auth;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AssignmentApiClient> _logger;

    public AssignmentApiClient(
        ISettingsService settings,
        IAuthService auth,
        IHttpClientFactory httpClientFactory,
        ILogger<AssignmentApiClient> logger)
    {
        _settings = settings;
        _auth = auth;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<AssignmentSurface> GetSurfaceAsync(CancellationToken ct = default)
    {
        var client = await CreateClientAsync(ct);
        if (client is null) return AssignmentSurface.Hidden;

        try
        {
            using var response = await client.Http.GetAsync($"{client.BaseUrl}/api/assignments/skills", ct);
            if (!response.IsSuccessStatusCode)
            {
                // 401/403/404 are the ordinary answers here — no token, no licence feature, older server —
                // and none of them is worth surfacing to the user as an error.
                _logger.LogInformation(
                    "Assignment skills probe returned {Status}; hiding the background-assignment surface.",
                    (int)response.StatusCode);
                return AssignmentSurface.Hidden;
            }

            var skills = await response.Content.ReadFromJsonAsync<List<AssignmentSkill>>(JsonOptions, ct);
            if (skills is null || skills.Count == 0) return AssignmentSurface.Hidden;

            return new AssignmentSurface(true, skills);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Assignment skills probe failed; hiding the surface.");
            return AssignmentSurface.Hidden;
        }
    }

    public async Task<Guid?> CreateAsync(string skillName, AssignmentInput input, CancellationToken ct = default)
    {
        var client = await CreateClientAsync(ct);
        if (client is null) return null;

        var body = new CreateAssignmentRequest(skillName, JsonSerializer.Serialize(input, JsonOptions));

        try
        {
            using var response = await client.Http.PostAsJsonAsync(
                $"{client.BaseUrl}/api/assignments", body, JsonOptions, ct);

            if (!response.IsSuccessStatusCode)
            {
                await LogRefusalAsync(response, ct);
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("id", out var id) && id.TryGetGuid(out var assignmentId)
                ? assignmentId
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start a background assignment.");
            return null;
        }
    }

    public async Task<AssignmentDto?> GetAsync(Guid assignmentId, CancellationToken ct = default)
    {
        var client = await CreateClientAsync(ct);
        if (client is null) return null;

        try
        {
            using var response = await client.Http.GetAsync(
                $"{client.BaseUrl}/api/assignments/{assignmentId}", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Assignment {AssignmentId} read returned {Status}.", assignmentId, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<AssignmentDto>(JsonOptions, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read assignment {AssignmentId}.", assignmentId);
            return null;
        }
    }

    public async Task<bool> CollectAsync(Guid assignmentId, CancellationToken ct = default)
    {
        var client = await CreateClientAsync(ct);
        if (client is null) return false;

        try
        {
            using var response = await client.Http.PostAsync(
                $"{client.BaseUrl}/api/assignments/{assignmentId}/collect", content: null, ct);

            // 404 counts as done: the row is gone, so there is no server-side plaintext left to drop and
            // nothing a retry could achieve. 409 does not — the run is still going and must be polled again.
            if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound) return true;

            _logger.LogInformation(
                "Collect for assignment {AssignmentId} returned {Status}.", assignmentId, (int)response.StatusCode);
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not acknowledge assignment {AssignmentId}.", assignmentId);
            return false;
        }
    }

    /// <summary>Logs WHY a create was refused. The server's code is machine-readable and safe to log; its
    /// message can quote an entity type but never the prompt or an item's text, and is kept to the sensitive
    /// channel regardless.</summary>
    private async Task LogRefusalAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var payload = await response.Content.ReadAsStringAsync(ct);
        var code = "unknown";
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("error", out var error))
                code = error.GetString() ?? code;
        }
        catch (JsonException)
        {
            // A non-JSON body (a proxy error page) is not worth a second failure path.
        }

        _logger.LogWarning(
            "The server refused the assignment: {Status} {Code}.", (int)response.StatusCode, code);
        _logger.SensitiveDebug("Assignment refusal body: {Body}", payload);
    }

    private sealed record ApiTarget(HttpClient Http, string BaseUrl);

    /// <summary>Null when there is nothing to talk to — no server URL or no token. Both are ordinary states
    /// for a local-only install, not errors.</summary>
    private async Task<ApiTarget?> CreateClientAsync(CancellationToken ct)
    {
        var settings = await _settings.GetSettingsAsync();
        var serverUrl = settings.ServerUrl?.TrimEnd('/');
        if (string.IsNullOrEmpty(serverUrl)) return null;

        var token = await _auth.GetAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return null;

        var http = _httpClientFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _logger.LogDebug("Assignment API target {Url}", SafeUrl.Format(serverUrl));
        _ = ct;
        return new ApiTarget(http, serverUrl);
    }
}
