namespace Pia.Shared.E2EE;

public class DeviceStatusResponse
{
    public required string DeviceId { get; set; }
    public DeviceStatus Status { get; set; }
}
