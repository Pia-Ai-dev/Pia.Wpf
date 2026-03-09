namespace Pia.Shared.E2EE;

public class DeviceListResponse
{
    public List<DeviceInfo> Devices { get; set; } = [];
    public bool HasRecoveryKey { get; set; }
    public int UmkVersion { get; set; }
}
