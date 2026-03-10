using Pia.Shared.E2EE;

namespace Pia.Services.Interfaces;

public class PendingDeviceEventArgs : EventArgs
{
    public required List<DeviceInfo> PendingDevices { get; init; }
}
