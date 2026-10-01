using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Shared.E2EE;

namespace Pia.Services.E2EE;

public class DeviceManagementService : IDeviceManagementService
{
    private readonly IE2EEService _e2ee;
    private readonly IDeviceKeyService _deviceKeys;
    private readonly IRecoveryCodeService _recovery;
    private readonly ISettingsService _settings;
    private readonly IAuthService _auth;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<DeviceManagementService> _logger;

    // Server URL and account the proof key is settled for, so a sign-in to another account checks again.
    private string? _proofKeySettledFor;

    public DeviceManagementService(
        IE2EEService e2ee,
        IDeviceKeyService deviceKeys,
        IRecoveryCodeService recovery,
        ISettingsService settings,
        IAuthService auth,
        IHttpClientFactory httpFactory,
        ILogger<DeviceManagementService> logger)
    {
        _e2ee = e2ee;
        _deviceKeys = deviceKeys;
        _recovery = recovery;
        _settings = settings;
        _auth = auth;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<string> BootstrapFirstDeviceAsync()
    {
        _logger.LogInformation("Bootstrapping E2EE for first device");

        // A retry after a partial success would wrap a new key while the locked recovery copy keeps the old one,
        // and the code shown would open nothing.
        if (await CheckE2EEStatusAsync() is { IsEnabled: true })
        {
            _logger.LogWarning("Not bootstrapping E2EE: the server reports it enabled for this account");
            throw new E2EEAlreadyEnabledException("End-to-end encryption is already set up for this account.");
        }

        // 1. Generate device keys (happens lazily in DeviceKeyService)
        var deviceId = _deviceKeys.GetDeviceId();
        var agreementPubKey = _deviceKeys.GetAgreementPublicKey();
        var signingPubKey = _deviceKeys.GetSigningPublicKey();

        // 2. Register device on server
        var registration = await RegisterDeviceOnServerAsync(deviceId, agreementPubKey, signingPubKey);

        if (!registration.IsFirstDevice)
            throw new InvalidOperationException("Cannot bootstrap: other devices already exist for this user");

        // 3. Generate UMK
        var umk = await _e2ee.GenerateAndStoreUmkAsync();

        // 4. Self-wrap UMK and upload to server
        var (selfWrapped, hkdfSalt) = _e2ee.WrapUmkForSelf();
        await UploadWrappedUmkAsync(deviceId, selfWrapped, hkdfSalt, deviceId);

        // 5. Generate recovery code and upload recovery-wrapped UMK
        var recoveryCode = _recovery.GenerateRecoveryCode();
        var recoveryBlob = _recovery.WrapUmkForRecovery(umk, recoveryCode);
        await UploadRecoveryWrappedUmkAsync(recoveryBlob);

        // Only after the recovery copy: the server refuses a proof key without one. A failure here is caught up
        // later by EnsureRecoveryProofKeyAsync.
        await TryPutRecoveryProofKeyAsync(umk);
        Array.Clear(umk);

        // 6. Update local settings
        var settings = await _settings.GetSettingsAsync();
        settings.IsE2EEEnabled = true;
        settings.E2EEUmkVersion = 1;
        settings.E2EERecoveryConfigured = true;
        await _settings.SaveSettingsAsync(settings);

        _logger.LogInformation("E2EE bootstrap complete for device {DeviceId}", deviceId);

        return recoveryCode;
    }

    public async Task<DeviceRegistrationResponse> RegisterPendingDeviceAsync()
    {
        var deviceId = _deviceKeys.GetDeviceId();
        var agreementPubKey = _deviceKeys.GetAgreementPublicKey();
        var signingPubKey = _deviceKeys.GetSigningPublicKey();

        return await RegisterDeviceOnServerAsync(deviceId, agreementPubKey, signingPubKey);
    }

    public async Task ApproveDeviceAsync(string onboardingSessionId, DeviceInfo targetDevice)
    {
        var deviceId = _deviceKeys.GetDeviceId();

        // Verify this device is registered and active before attempting approval
        var ownStatus = await GetDeviceStatusAsync(deviceId);
        if (ownStatus is null || ownStatus.Status != DeviceStatus.Active)
        {
            _logger.LogWarning(
                "Cannot approve device: this device ({DeviceId}) is not registered as Active on the server (status={Status}). Re-registering.",
                deviceId, ownStatus?.Status.ToString() ?? "not found");

            // Re-register to ensure the device exists on the server
            await RegisterDeviceOnServerAsync(
                deviceId,
                _deviceKeys.GetAgreementPublicKey(),
                _deviceKeys.GetSigningPublicKey());

            // Re-check status — if still not Active, we can't approve
            ownStatus = await GetDeviceStatusAsync(deviceId);
            if (ownStatus is null || ownStatus.Status != DeviceStatus.Active)
            {
                throw new InvalidOperationException(
                    $"This device is not active on the server (status: {ownStatus?.Status.ToString() ?? "unknown"}). " +
                    "It may need to be approved by another device first.");
            }
        }

        // Wrap UMK for target device
        var (wrappedUmk, hkdfSalt) = _e2ee.WrapUmkForDevice(
            targetDevice.AgreementPublicKey, targetDevice.DeviceId);

        var signed = DeviceApprovalSignature.Payload(
            DeviceApprovalSignature.Current, onboardingSessionId, targetDevice.DeviceId,
            targetDevice.AgreementPublicKey, wrappedUmk, hkdfSalt)!;
        var approval = new DeviceApprovalRequest
        {
            OnboardingSessionId = onboardingSessionId,
            TargetDeviceId = targetDevice.DeviceId,
            WrappedUmk = wrappedUmk,
            HkdfSalt = hkdfSalt,
            ApproverDeviceId = deviceId,
            ApproverSignature = _deviceKeys.Sign(signed),
            SignatureVersion = DeviceApprovalSignature.Current,
        };

        using var client = await CreateAuthorizedClientAsync();
        var response = await client.PostAsJsonAsync("api/e2ee/devices/approve", approval);
        response.EnsureSuccessStatusCode();

        _logger.LogInformation("Approved device {DeviceId}", targetDevice.DeviceId);
    }

    public async Task ActivateViaRecoveryAsync(string recoveryCode, string onboardingSessionId)
    {
        var deviceId = _deviceKeys.GetDeviceId();

        using var client = await CreateAuthorizedClientAsync();
        var recoveryBlob = await client.GetFromJsonAsync<RecoveryWrappedUmkBlob>(
            "api/e2ee/recovery/wrapped-umk");

        if (recoveryBlob is null)
            throw new InvalidOperationException("No recovery key found on server");

        var umk = _recovery.UnwrapUmkFromRecovery(recoveryBlob, recoveryCode);
        await _e2ee.StoreUmkAsync(umk);
        var (selfWrapped, hkdfSalt) = _e2ee.WrapUmkForSelf();

        // The server activates only a pending device; an active one replaces its unopenable copy with its own wrap.
        var status = await GetDeviceStatusAsync(deviceId);
        if (status is { Status: DeviceStatus.Active })
        {
            await UploadWrappedUmkAsync(deviceId, selfWrapped, hkdfSalt, deviceId);
            _logger.LogInformation("Replaced this active device's copy of the key via recovery code");
        }
        else
        {
            await ActivatePendingDeviceAsync(client, deviceId, umk, selfWrapped, hkdfSalt, onboardingSessionId);
            _logger.LogInformation("Activated device via recovery code");
        }

        var settings = await _settings.GetSettingsAsync();
        settings.IsE2EEEnabled = true;
        settings.E2EEUmkVersion = recoveryBlob.UmkVersion;
        await _settings.SaveSettingsAsync(settings);

        Array.Clear(umk);
    }

    private async Task ActivatePendingDeviceAsync(
        HttpClient client, string deviceId, byte[] umk, string selfWrapped, string hkdfSalt,
        string onboardingSessionId)
    {
        var proofKey = RecoveryActivationProof.DeriveProofKey(umk);
        var proof = RecoveryActivationProof.Compute(proofKey, onboardingSessionId);
        Array.Clear(proofKey);

        var activationRequest = new RecoveryActivationRequest
        {
            DeviceId = deviceId,
            SelfWrappedUmk = selfWrapped,
            HkdfSalt = hkdfSalt,
            ProofOfPossession = proof,
            OnboardingSessionId = onboardingSessionId
        };

        using var response = await client.PostAsJsonAsync("api/e2ee/recovery/activate", activationRequest);
        if (response.StatusCode == HttpStatusCode.BadRequest && await IsSpentOnboardingSessionAsync(response))
        {
            _logger.LogInformation("The server no longer accepts this device's onboarding session");
            throw new OnboardingSessionExpiredException("The onboarding session is no longer valid.");
        }
        response.EnsureSuccessStatusCode();
    }

    // The server answers a spent session with a bare JSON string and a failed proof with an { error } envelope.
    private static async Task<bool> IsSpentOnboardingSessionAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.String => MentionsOnboardingSession(doc.RootElement.GetString()),
                JsonValueKind.Object => doc.RootElement.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.String && error.GetString() == "invalid_onboarding_session",
                _ => false,
            };
        }
        catch (JsonException)
        {
            return MentionsOnboardingSession(body);
        }
    }

    private static bool MentionsOnboardingSession(string? text) =>
        text?.Contains("onboarding session", StringComparison.OrdinalIgnoreCase) == true;

    public async Task FetchAndUnwrapUmkAsync() => await AcceptKeyHandoverAsync(await FetchKeyHandoverAsync());

    public async Task<KeyHandover> FetchKeyHandoverAsync()
    {
        var deviceId = _deviceKeys.GetDeviceId();
        using var client = await CreateAuthorizedClientAsync();

        var wrappedBlob = await client.GetFromJsonAsync<WrappedUmkBlob>(
            $"api/e2ee/devices/{deviceId}/wrapped-umk");

        if (wrappedBlob is null)
            throw new InvalidOperationException("No wrapped UMK found for this device");

        var devices = await GetDevicesAsync();
        if (wrappedBlob.CreatedByDeviceId == deviceId)
            return new KeyHandover(wrappedBlob, _deviceKeys.GetAgreementPublicKey(), devices.UmkVersion, null, null);

        var approver = VerifiedApprover(wrappedBlob, devices, deviceId);
        return new KeyHandover(
            wrappedBlob, approver.AgreementPublicKey, devices.UmkVersion,
            approver.DeviceName, _deviceKeys.ComputeFingerprint(approver.AgreementPublicKey));
    }

    public async Task AcceptKeyHandoverAsync(KeyHandover handover)
    {
        var deviceId = _deviceKeys.GetDeviceId();
        var umk = _e2ee.UnwrapUmkForDevice(
            handover.Blob.Ciphertext,
            handover.Blob.HkdfSalt,
            handover.SenderAgreementPublicKey,
            deviceId);

        await _e2ee.StoreUmkAsync(umk);
        Array.Clear(umk);

        var settings = await _settings.GetSettingsAsync();
        settings.IsE2EEEnabled = true;
        settings.E2EEUmkVersion = handover.UmkVersion;
        await _settings.SaveSettingsAsync(settings);

        _logger.LogInformation("Fetched and unwrapped UMK from device {Approver}", handover.Blob.CreatedByDeviceId);
    }

    // A copy this device wrapped for itself needs no signature and is opened with the local agreement key.
    private DeviceInfo VerifiedApprover(WrappedUmkBlob blob, DeviceListResponse devices, string deviceId)
    {
        var approver = devices.Devices.FirstOrDefault(d =>
            d.DeviceId == blob.CreatedByDeviceId && d.Status == DeviceStatus.Active);
        var payload = string.IsNullOrEmpty(blob.OnboardingSessionId)
            ? null
            : DeviceApprovalSignature.Payload(
                blob.SignatureVersion, blob.OnboardingSessionId, deviceId, _deviceKeys.GetAgreementPublicKey(),
                blob.Ciphertext, blob.HkdfSalt);

        if (approver is null || payload is null || string.IsNullOrEmpty(blob.ApproverSignature)
            || !_deviceKeys.Verify(payload, blob.ApproverSignature, approver.SigningPublicKey))
        {
            _logger.LogWarning(
                "Refusing the key handed over by device {Approver}: no valid signature of an active device",
                blob.CreatedByDeviceId);
            throw new UnverifiedApprovalException("The approval could not be verified.");
        }
        return approver;
    }

    public async Task<bool> TryRestoreKeyAsync()
    {
        if (IsInitialized()) return true;
        if (!_deviceKeys.HasDeviceKeys()) return false;

        var status = await GetDeviceStatusAsync(_deviceKeys.GetDeviceId());
        if (status is not { Status: DeviceStatus.Active }) return false;

        try
        {
            await FetchAndUnwrapUmkAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not take back this device's copy of the key");
            return false;
        }
        return IsInitialized();
    }

    public async Task EnsureRecoveryProofKeyAsync()
    {
        try
        {
            var account = await CurrentAccountAsync();
            if (account is null || account == _proofKeySettledFor || !IsInitialized())
                return;

            var status = await CheckE2EEStatusAsync();
            if (status is not { IsEnabled: true, HasRecoveryKey: true })
                return;
            if (status.HasRecoveryProofKey)
            {
                _proofKeySettledFor = account;
                return;
            }

            if (await GetDeviceStatusAsync(_deviceKeys.GetDeviceId()) is not { Status: DeviceStatus.Active })
                return;

            // The cached key itself: never cleared here.
            var umk = _e2ee.LoadUmk();
            if (umk is not null && await TryPutRecoveryProofKeyAsync(umk))
                _proofKeySettledFor = account;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check the recovery proof key");
        }
    }

    /// <summary>False only for a failure worth retrying; a refusal the server would repeat counts as settled.</summary>
    private async Task<bool> TryPutRecoveryProofKeyAsync(byte[] umk)
    {
        var proofKey = RecoveryActivationProof.DeriveProofKey(umk);
        try
        {
            using var client = await CreateAuthorizedClientAsync();
            using var response = await client.PutAsJsonAsync(
                "api/e2ee/recovery/proof-key",
                new RecoveryProofKeyRequest { ProofKey = Convert.ToBase64String(proofKey) });

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Recovery proof key stored on the server");
                return true;
            }

            switch (response.StatusCode)
            {
                case HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed:
                    _logger.LogInformation("The server does not accept a recovery proof key yet");
                    return true;
                case HttpStatusCode.Conflict:
                    var code = await ReadErrorCodeAsync(response);
                    if (code == E2EEErrorCodes.ProofKeyConflict)
                    {
                        _logger.LogWarning("The server holds a different recovery proof key for this account");
                        return true;
                    }
                    // A missing precondition, e.g. this device just turned pending: worth another try.
                    _logger.LogInformation("The server is not ready for the recovery proof key ({Code})", code ?? "no code");
                    return false;
                case HttpStatusCode.BadRequest:
                    _logger.LogWarning("The server rejected the recovery proof key as malformed");
                    return true;
                default:
                    _logger.LogWarning(
                        "Storing the recovery proof key failed with status {Status}", (int)response.StatusCode);
                    return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not store the recovery proof key");
            return false;
        }
        finally
        {
            Array.Clear(proofKey);
        }
    }

    private async Task<string?> CurrentAccountAsync()
    {
        var serverUrl = (await _settings.GetSettingsAsync()).ServerUrl?.TrimEnd('/');
        var email = _auth.UserEmail;
        return string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(email) ? null : $"{serverUrl}|{email}";
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task RevokeDeviceAsync(string deviceId)
    {
        using var client = await CreateAuthorizedClientAsync();
        var response = await client.PostAsync($"api/e2ee/devices/{deviceId}/revoke", null);
        response.EnsureSuccessStatusCode();
        _logger.LogInformation("Revoked device {DeviceId}", deviceId);
    }

    public async Task<DeviceListResponse> GetDevicesAsync()
    {
        using var client = await CreateAuthorizedClientAsync();
        return await client.GetFromJsonAsync<DeviceListResponse>("api/e2ee/devices")
            ?? new DeviceListResponse();
    }

    public bool IsInitialized() => _e2ee.IsReady() && _deviceKeys.HasDeviceKeys();

    private async Task<DeviceRegistrationResponse> RegisterDeviceOnServerAsync(
        string deviceId, string agreementPubKey, string signingPubKey)
    {
        var request = new DeviceRegistrationRequest
        {
            DeviceId = deviceId,
            DeviceName = Environment.MachineName,
            AgreementPublicKey = agreementPubKey,
            SigningPublicKey = signingPubKey,
            OsVersion = Environment.OSVersion.ToString(),
            AppVersion = AppVersionInfo.FileVersion
        };

        using var client = await CreateAuthorizedClientAsync();
        var response = await client.PostAsJsonAsync("api/e2ee/devices/register", request);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<DeviceRegistrationResponse>()
            ?? throw new InvalidOperationException("Invalid registration response");
    }

    private async Task UploadWrappedUmkAsync(
        string deviceId, string ciphertext, string hkdfSalt, string createdByDeviceId)
    {
        var blob = new WrappedUmkBlob
        {
            DeviceId = deviceId,
            Ciphertext = ciphertext,
            HkdfSalt = hkdfSalt,
            CreatedByDeviceId = createdByDeviceId,
            CreatedAt = DateTime.UtcNow
        };

        using var client = await CreateAuthorizedClientAsync();
        var response = await client.PostAsJsonAsync($"api/e2ee/devices/{deviceId}/wrapped-umk", blob);
        response.EnsureSuccessStatusCode();
    }

    private async Task UploadRecoveryWrappedUmkAsync(RecoveryWrappedUmkBlob blob)
    {
        using var client = await CreateAuthorizedClientAsync();
        using var response = await client.PostAsJsonAsync("api/e2ee/recovery/wrapped-umk", blob);
        if (response.StatusCode == HttpStatusCode.Conflict
            && await ReadErrorCodeAsync(response) == E2EEErrorCodes.RecoveryKeyLocked)
        {
            _logger.LogWarning("The server refused to replace the recovery copy of the key: it is locked");
            throw new InvalidOperationException("The recovery key of this account is locked and cannot be replaced.");
        }
        response.EnsureSuccessStatusCode();
    }

    public async Task<E2EEStatusResponse?> CheckE2EEStatusAsync()
    {
        try
        {
            using var client = await CreateAuthorizedClientAsync();
            var response = await client.GetAsync("api/e2ee/status");
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<E2EEStatusResponse>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check E2EE status");
            return null;
        }
    }

    public async Task<DeviceStatusResponse?> GetDeviceStatusAsync(string deviceId)
    {
        try
        {
            using var client = await CreateAuthorizedClientAsync();
            var response = await client.GetAsync($"api/e2ee/devices/{deviceId}/status");
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<DeviceStatusResponse>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check device status for {DeviceId}", deviceId);
            return null;
        }
    }

    private async Task<HttpClient> CreateAuthorizedClientAsync()
    {
        var serverUrl = (await _settings.GetSettingsAsync()).ServerUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(serverUrl))
            throw new InvalidOperationException("Server URL not configured");

        var client = _httpFactory.CreateClient();
        // Without the trailing slash a relative request path would replace the URL's last segment.
        client.BaseAddress = new Uri(serverUrl + "/");
        var token = await _auth.GetAccessTokenAsync();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }
}
