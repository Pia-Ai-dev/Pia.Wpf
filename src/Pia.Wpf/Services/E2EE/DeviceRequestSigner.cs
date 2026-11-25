using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;
using Pia.Shared.E2EE;

namespace Pia.Services.E2EE;

public sealed class DeviceRequestSigner : IDeviceRequestSigner
{
    private readonly IDeviceKeyService _deviceKeys;
    private readonly ISettingsService _settings;
    private readonly ILogger<DeviceRequestSigner> _logger;

    // Servers that predate device signatures, so their challenge route is asked only once per session.
    private readonly ConcurrentDictionary<string, byte> _unsigningServers = new();

    public DeviceRequestSigner(
        IDeviceKeyService deviceKeys, ISettingsService settings, ILogger<DeviceRequestSigner> logger)
    {
        _deviceKeys = deviceKeys;
        _settings = settings;
        _logger = logger;
    }

    public async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string relativePath, byte[]? jsonBody, CancellationToken ct = default)
    {
        var signer = await SignerAsync();
        if (signer is not { } identity)
            return await client.SendAsync(Request(method, relativePath, jsonBody), ct);

        var (userId, deviceId) = identity;
        var server = client.BaseAddress?.ToString() ?? "";
        if (_unsigningServers.ContainsKey(server))
        {
            var unsigned = await client.SendAsync(Request(method, relativePath, jsonBody), ct);
            if (!await IsRefusedWithAsync(unsigned, E2EEErrorCodes.SignatureRequired, ct))
                return unsigned;

            unsigned.Dispose();
            _unsigningServers.TryRemove(server, out _);
            _logger.LogInformation("The server now asks for device signatures");
        }

        for (var attempt = 1; ; attempt++)
        {
            var challengeResponse = await client.PostAsync($"api/e2ee/devices/{deviceId}/challenge", null, ct);
            if (challengeResponse.StatusCode == HttpStatusCode.NotFound
                && await E2EEErrorBody.ReadCodeAsync(challengeResponse, ct) is null)
            {
                challengeResponse.Dispose();
                _unsigningServers.TryAdd(server, 0);
                _logger.LogInformation("The server issues no device challenges; sending unsigned");
                return await client.SendAsync(Request(method, relativePath, jsonBody), ct);
            }
            if (!challengeResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "The server refused a device challenge with status {Status}", (int)challengeResponse.StatusCode);
                return challengeResponse;
            }

            var challenge = await challengeResponse.Content.ReadFromJsonAsync<DeviceChallengeResponse>(ct)
                ?? throw new InvalidOperationException("Empty device challenge");
            challengeResponse.Dispose();

            var request = Request(method, relativePath, jsonBody);
            var payload = DeviceRequestSignature.Payload(
                method.Method, SignedPath(relativePath), userId, deviceId, challenge.ChallengeId, jsonBody ?? []);
            request.Headers.Add(DeviceRequestSignature.DeviceIdHeader, deviceId);
            request.Headers.Add(DeviceRequestSignature.ChallengeIdHeader, challenge.ChallengeId);
            request.Headers.Add(DeviceRequestSignature.SignatureHeader, _deviceKeys.Sign(payload));

            var response = await client.SendAsync(request, ct);
            if (attempt > 1 || !await IsRefusedWithAsync(response, E2EEErrorCodes.InvalidSignature, ct))
                return response;

            response.Dispose();
            _logger.LogInformation("The server refused the device signature; retrying with a fresh challenge");
        }
    }

    private async Task<(Guid UserId, string DeviceId)?> SignerAsync()
    {
        if (!_deviceKeys.CanSign())
            return null;

        if (!Guid.TryParse((await _settings.GetSettingsAsync()).SyncUserId, out var userId))
        {
            _logger.LogWarning("No account id to sign a device request with; sending unsigned");
            return null;
        }
        return (userId, _deviceKeys.GetDeviceId());
    }

    // The route path from /api/ on, so a deployment prefix in the server URL does not enter the signature.
    private static string SignedPath(string relativePath) => "/" + relativePath.Split('?')[0];

    private static HttpRequestMessage Request(HttpMethod method, string relativePath, byte[]? jsonBody)
    {
        var request = new HttpRequestMessage(method, relativePath);
        if (jsonBody is not null)
        {
            request.Content = new ByteArrayContent(jsonBody);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }
        return request;
    }

    private static async Task<bool> IsRefusedWithAsync(HttpResponseMessage response, string code, CancellationToken ct) =>
        response.StatusCode == HttpStatusCode.Forbidden && await E2EEErrorBody.ReadCodeAsync(response, ct) == code;
}
