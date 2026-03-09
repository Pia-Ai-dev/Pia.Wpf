namespace Pia.Shared.E2EE;

public class DeviceRegistrationRequest
{
    public required string DeviceId { get; set; }
    public required string DeviceName { get; set; }
    public required string AgreementPublicKey { get; set; }
    public required string SigningPublicKey { get; set; }
    public string? OsVersion { get; set; }
    public string? AppVersion { get; set; }
}
