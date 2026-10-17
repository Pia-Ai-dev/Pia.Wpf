using Pia.Shared.E2EE;

namespace Pia.Services.E2EE;

public interface IDeviceManagementService
{
    /// <summary>
    /// Bootstrap E2EE for the very first device.
    /// Generates UMK, device keys, self-wraps UMK, and uploads recovery-wrapped UMK.
    /// Returns the recovery code that must be shown to the user.
    /// </summary>
    Task<string> BootstrapFirstDeviceAsync();

    /// <summary>
    /// Register this device as pending on the server.
    /// </summary>
    Task<DeviceRegistrationResponse> RegisterPendingDeviceAsync();

    /// <summary>
    /// Approve a pending device (called from an already-active device).
    /// Wraps UMK for the target device and sends approval to server.
    /// </summary>
    Task ApproveDeviceAsync(string onboardingSessionId, DeviceInfo targetDevice);

    /// <summary>Takes the key back with the recovery code: activates a pending device, and replaces an active
    /// device's server copy with its own wrap, which needs no onboarding session.</summary>
    Task ActivateViaRecoveryAsync(string recoveryCode, string onboardingSessionId);

    /// <summary>Fetches this device's server copy of the key without storing it; another device's wrap needs that
    /// device's valid signature, or <see cref="UnverifiedApprovalException"/> is thrown.</summary>
    Task<KeyHandover> FetchKeyHandoverAsync();

    /// <summary>Stores a key a person accepted, remembering another device's fingerprint as confirmed.</summary>
    Task AcceptKeyHandoverAsync(KeyHandover handover);

    /// <summary>True when this device holds the key, taking back its server copy if the device is still active and
    /// the copy is its own or comes from an approver confirmed here before.</summary>
    Task<bool> TryRestoreKeyAsync();

    /// <summary>Uploads the recovery proof key an existing account lacks, once per account and app run; never throws.</summary>
    Task EnsureRecoveryProofKeyAsync();

    /// <summary>
    /// Revoke a device by its deviceId.
    /// </summary>
    Task RevokeDeviceAsync(string deviceId);

    /// <summary>
    /// Get the list of all devices for the current user.
    /// </summary>
    Task<DeviceListResponse> GetDevicesAsync();

    /// <summary>
    /// Check if E2EE has been initialized for this device.
    /// </summary>
    bool IsInitialized();

    /// <summary>
    /// Check account-level E2EE status from the server.
    /// Used after login to detect if onboarding is needed.
    /// </summary>
    Task<E2EEStatusResponse?> CheckE2EEStatusAsync();

    /// <summary>
    /// Poll a specific device's approval status from the server.
    /// Used during onboarding to detect when a pending device becomes active.
    /// </summary>
    Task<DeviceStatusResponse?> GetDeviceStatusAsync(string deviceId);
}
