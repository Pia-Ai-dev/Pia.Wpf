using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;
using Pia.Shared.E2EE;

namespace Pia.Services.E2EE;

public class DeviceManagementService : IDeviceManagementService
{
    private readonly IE2EEService _e2ee;
    private readonly IDeviceKeyService _deviceKeys;
    private readonly IRecoveryCodeService _recovery;
    private readonly ICryptoService _crypto;
    private readonly ISettingsService _settings;
    private readonly IAuthService _auth;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<DeviceManagementService> _logger;

    public DeviceManagementService(
        IE2EEService e2ee,
        IDeviceKeyService deviceKeys,
        IRecoveryCodeService recovery,
        ICryptoService crypto,
        ISettingsService settings,
        IAuthService auth,
        IHttpClientFactory httpFactory,
        ILogger<DeviceManagementService> logger)
    {
        _e2ee = e2ee;
        _deviceKeys = deviceKeys;
        _recovery = recovery;
        _crypto = crypto;
        _settings = settings;
        _auth = auth;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<string> BootstrapFirstDeviceAsync()
    {
        _logger.LogInformation("Bootstrapping E2EE for first device");

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
        // Wrap UMK for target device
        var (wrappedUmk, hkdfSalt) = _e2ee.WrapUmkForDevice(
            targetDevice.AgreementPublicKey, targetDevice.DeviceId);

        var approval = new DeviceApprovalRequest
        {
            OnboardingSessionId = onboardingSessionId,
            TargetDeviceId = targetDevice.DeviceId,
            WrappedUmk = wrappedUmk,
            HkdfSalt = hkdfSalt,
            ApproverDeviceId = _deviceKeys.GetDeviceId(),
        };

        // Sign the approval
        var signData = Encoding.UTF8.GetBytes(
            $"{onboardingSessionId}:{targetDevice.DeviceId}:{targetDevice.AgreementPublicKey}");
        approval.ApproverSignature = _deviceKeys.Sign(signData);

        using var client = await CreateAuthorizedClientAsync();
        var response = await client.PostAsJsonAsync("/api/e2ee/devices/approve", approval);
        response.EnsureSuccessStatusCode();

        _logger.LogInformation("Approved device {DeviceId}", targetDevice.DeviceId);
    }

    public async Task ActivateViaRecoveryAsync(string recoveryCode, string onboardingSessionId)
    {
        var deviceId = _deviceKeys.GetDeviceId();

        // 1. Fetch recovery-wrapped UMK from server
        using var client = await CreateAuthorizedClientAsync();
        var recoveryBlob = await client.GetFromJsonAsync<RecoveryWrappedUmkBlob>(
            "/api/e2ee/recovery/wrapped-umk");

        if (recoveryBlob is null)
            throw new InvalidOperationException("No recovery key found on server");

        // 2. Unwrap UMK using recovery code
        var umk = _recovery.UnwrapUmkFromRecovery(recoveryBlob, recoveryCode);
        await _e2ee.StoreUmkAsync(umk);

        // 3. Self-wrap UMK for this device
        var (selfWrapped, hkdfSalt) = _e2ee.WrapUmkForSelf();

        // 4. Compute proof-of-possession
        var proofKey = _crypto.DeriveKey(umk, Encoding.UTF8.GetBytes("activation"), "pia-activation-proof-v1");
        var proof = Convert.ToBase64String(
            HMACSHA256.HashData(proofKey, Encoding.UTF8.GetBytes(onboardingSessionId)));

        // 5. Send recovery activation request
        var activationRequest = new RecoveryActivationRequest
        {
            DeviceId = deviceId,
            SelfWrappedUmk = selfWrapped,
            HkdfSalt = hkdfSalt,
            ProofOfPossession = proof,
            OnboardingSessionId = onboardingSessionId
        };

        var response = await client.PostAsJsonAsync("/api/e2ee/recovery/activate", activationRequest);
        response.EnsureSuccessStatusCode();

        // 6. Update local settings
        var settings = await _settings.GetSettingsAsync();
        settings.IsE2EEEnabled = true;
        settings.E2EEUmkVersion = recoveryBlob.UmkVersion;
        await _settings.SaveSettingsAsync(settings);

        Array.Clear(umk);
        Array.Clear(proofKey);

        _logger.LogInformation("Activated device via recovery code");
    }

    public async Task FetchAndUnwrapUmkAsync()
    {
        var deviceId = _deviceKeys.GetDeviceId();
        using var client = await CreateAuthorizedClientAsync();

        var wrappedBlob = await client.GetFromJsonAsync<WrappedUmkBlob>(
            $"/api/e2ee/devices/{deviceId}/wrapped-umk");

        if (wrappedBlob is null)
            throw new InvalidOperationException("No wrapped UMK found for this device");

        // Need the sender's (approver's) public key to derive shared secret
        var devices = await GetDevicesAsync();
        var approverDevice = devices.Devices
            .FirstOrDefault(d => d.DeviceId == wrappedBlob.CreatedByDeviceId);

        if (approverDevice is null)
            throw new InvalidOperationException("Approver device not found");

        var umk = _e2ee.UnwrapUmkForDevice(
            wrappedBlob.Ciphertext,
            wrappedBlob.HkdfSalt,
            approverDevice.AgreementPublicKey,
            deviceId);

        await _e2ee.StoreUmkAsync(umk);
        Array.Clear(umk);

        var settings = await _settings.GetSettingsAsync();
        settings.IsE2EEEnabled = true;
        settings.E2EEUmkVersion = devices.UmkVersion;
        await _settings.SaveSettingsAsync(settings);

        _logger.LogInformation("Fetched and unwrapped UMK from device {Approver}", wrappedBlob.CreatedByDeviceId);
    }

    public async Task RevokeDeviceAsync(string deviceId)
    {
        using var client = await CreateAuthorizedClientAsync();
        var response = await client.PostAsync($"/api/e2ee/devices/{deviceId}/revoke", null);
        response.EnsureSuccessStatusCode();
        _logger.LogInformation("Revoked device {DeviceId}", deviceId);
    }

    public async Task<DeviceListResponse> GetDevicesAsync()
    {
        using var client = await CreateAuthorizedClientAsync();
        return await client.GetFromJsonAsync<DeviceListResponse>("/api/e2ee/devices")
            ?? new DeviceListResponse();
    }

    public async Task<string> ReKeyAsync()
    {
        _logger.LogInformation("Re-keying E2EE with new UMK");

        var deviceId = _deviceKeys.GetDeviceId();

        // Generate new UMK (overwrites the old one)
        var umk = await _e2ee.GenerateAndStoreUmkAsync();

        // Self-wrap new UMK and upload
        var (selfWrapped, hkdfSalt) = _e2ee.WrapUmkForSelf();
        await UploadWrappedUmkAsync(deviceId, selfWrapped, hkdfSalt, deviceId);

        // Generate new recovery code and upload recovery-wrapped UMK
        var recoveryCode = _recovery.GenerateRecoveryCode();
        var recoveryBlob = _recovery.WrapUmkForRecovery(umk, recoveryCode);
        await UploadRecoveryWrappedUmkAsync(recoveryBlob);

        // Update settings
        var settings = await _settings.GetSettingsAsync();
        settings.IsE2EEEnabled = true;
        settings.E2EEUmkVersion++;
        settings.E2EERecoveryConfigured = true;
        await _settings.SaveSettingsAsync(settings);

        _logger.LogInformation("E2EE re-keyed for device {DeviceId}", deviceId);

        return recoveryCode;
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
            AppVersion = typeof(DeviceManagementService).Assembly
                .GetName().Version?.ToString() ?? "1.0.0"
        };

        using var client = await CreateAuthorizedClientAsync();
        var response = await client.PostAsJsonAsync("/api/e2ee/devices/register", request);
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
        var response = await client.PostAsJsonAsync($"/api/e2ee/devices/{deviceId}/wrapped-umk", blob);
        response.EnsureSuccessStatusCode();
    }

    private async Task UploadRecoveryWrappedUmkAsync(RecoveryWrappedUmkBlob blob)
    {
        using var client = await CreateAuthorizedClientAsync();
        var response = await client.PostAsJsonAsync("/api/e2ee/recovery/wrapped-umk", blob);
        response.EnsureSuccessStatusCode();
    }

    public async Task<E2EEStatusResponse?> CheckE2EEStatusAsync()
    {
        try
        {
            using var client = await CreateAuthorizedClientAsync();
            var response = await client.GetAsync("/api/e2ee/status");
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
            var response = await client.GetAsync($"/api/e2ee/devices/{deviceId}/status");
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
        var client = _httpFactory.CreateClient();
        var settings = await _settings.GetSettingsAsync();
        client.BaseAddress = new Uri(settings.ServerUrl ?? throw new InvalidOperationException("Server URL not configured"));
        var token = await _auth.GetAccessTokenAsync();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }
}
