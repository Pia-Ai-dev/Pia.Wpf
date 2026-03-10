namespace Pia.Shared.E2EE;

/// <summary>
/// Lightweight response for polling a specific device's approval status.
/// Used during onboarding to detect when a pending device becomes active.
/// </summary>
public class DeviceStatusResponse
{
    public required string DeviceId { get; set; }
    public DeviceStatus Status { get; set; }
    public bool IsApproved => Status == DeviceStatus.Active;
}
