namespace Pia.Shared.Models;

/// <summary>
/// Minimal pending-device info returned inline on a sync pull so the client can
/// render a meaningful device-approval prompt without a follow-up GetDevicesAsync call.
/// This DTO is for prompt rendering only; it does not carry enough information to
/// drive the approval flow itself.
/// </summary>
public class SyncPendingDevice
{
    /// <summary>
    /// The server row primary key (ServerDevice.Id), NOT the E2EE string DeviceId used
    /// elsewhere in the device surface (DeviceInfo.DeviceId, the approval flow). Do not
    /// confuse the two identifiers. Resolving/approving a device still requires a
    /// GetDevicesAsync call to obtain the full DeviceInfo (string DeviceId, keys, fingerprint).
    /// </summary>
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public DateTime CreatedAt { get; set; }
}
