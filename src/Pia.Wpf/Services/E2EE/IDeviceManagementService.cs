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

    /// <summary>
    /// Activate this device using the recovery code.
    /// </summary>
    Task ActivateViaRecoveryAsync(string recoveryCode, string onboardingSessionId);

    /// <summary>
    /// Fetch this device's wrapped UMK from the server and unwrap it.
    /// Called after another device approves this one.
    /// </summary>
    Task FetchAndUnwrapUmkAsync();

    /// <summary>
    /// Revoke a device by its deviceId.
    /// </summary>
    Task RevokeDeviceAsync(string deviceId);

    /// <summary>
    /// Get the list of all devices for the current user.
    /// </summary>
    Task<DeviceListResponse> GetDevicesAsync();

    /// <summary>
    /// Re-key E2EE with a new UMK. Used when re-enabling E2EE on a device that
    /// was previously bootstrapped. Generates a new UMK, self-wraps, and uploads
    /// a new recovery-wrapped UMK. Returns the new recovery code.
    /// </summary>
    Task<string> ReKeyAsync();

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
