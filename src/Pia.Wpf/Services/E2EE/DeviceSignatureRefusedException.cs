namespace Pia.Services.E2EE;

/// <summary>The server wants a signature from an active device that this device cannot give; recovery restores one.</summary>
public sealed class DeviceSignatureRefusedException(string code)
    : InvalidOperationException("The server requires a signature from an active device.")
{
    public string Code { get; } = code;
}
