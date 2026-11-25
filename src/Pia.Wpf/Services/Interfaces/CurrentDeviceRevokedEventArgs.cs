namespace Pia.Services.Interfaces;

/// <summary>Carries how the server lost this device: a revoke leaves a Revoked row, a deletion leaves nothing.</summary>
public class CurrentDeviceRevokedEventArgs(bool wasRevoked) : EventArgs
{
    /// <summary>False when the device is simply absent from the list (deleted or expired).</summary>
    public bool WasRevoked { get; } = wasRevoked;
}
