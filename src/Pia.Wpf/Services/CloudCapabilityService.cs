using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services;

public sealed class CloudCapabilityService : ICloudCapabilityService
{
    // Client's compiled-in chats schema version. Kept in sync with
    // SyncAssistantChat.SchemaVersion's default — bump both together.
    private const int ClientChatsSchemaVersion = 1;

    private readonly ISettingsService _settingsService;
    private readonly IAuthService _authService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CloudCapabilityService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool? _chatsSupported;
    private int? _chatsSchemaVersion;

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
        await EnsureProbedAsync(ct);
        return _chatsSupported ?? false;
    }

    public async Task<int?> ChatsSchemaVersionAsync(CancellationToken ct = default)
    {
        await EnsureProbedAsync(ct);
        return _chatsSchemaVersion;
    }

    private async Task EnsureProbedAsync(CancellationToken ct)
    {
        if (_chatsSupported.HasValue) return;

        await _gate.WaitAsync(ct);
        try
        {
            if (_chatsSupported.HasValue) return;

            var (supported, schemaVersion) = await ProbeAsync(ct);
            _chatsSupported = supported;
            _chatsSchemaVersion = schemaVersion;

            _logger.LogInformation(
                "Cloud capability probe: chats supported = {Supported}, schemaVersion = {SchemaVersion}",
                supported, schemaVersion);

            if (supported && schemaVersion is int v && v > ClientChatsSchemaVersion)
            {
                _logger.LogWarning(
                    "Server advertises chats schemaVersion {ServerVersion}; client knows {ClientVersion}. " +
                    "Some chat fields may be hidden until the client is updated.",
                    v, ClientChatsSchemaVersion);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(bool Supported, int? SchemaVersion)> ProbeAsync(CancellationToken ct)
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            var serverUrl = settings.ServerUrl?.TrimEnd('/');
            if (string.IsNullOrEmpty(serverUrl)) return (false, null);

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
                return (false, null);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (false, null);
            if (!doc.RootElement.TryGetProperty("chats", out var chatsProp)) return (false, null);
            var supported = chatsProp.ValueKind == JsonValueKind.True;

            int? schemaVersion = null;
            if (doc.RootElement.TryGetProperty("chatsSchemaVersion", out var versionProp) &&
                versionProp.ValueKind == JsonValueKind.Number &&
                versionProp.TryGetInt32(out var v))
            {
                schemaVersion = v;
            }

            return (supported, schemaVersion);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Capability probe failed: {Type}", ex.GetType().Name);
            return (false, null);
        }
    }
}
