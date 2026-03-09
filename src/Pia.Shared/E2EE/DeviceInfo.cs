namespace Pia.Shared.E2EE;

/// <summary>
/// Device metadata visible to other devices and the server.
/// Public keys are base64-encoded raw EC point bytes (X + Y, 64 bytes for P-256).
/// </summary>
public class DeviceInfo
{
    public required string DeviceId { get; set; }
    public required string DeviceName { get; set; }
    public DeviceStatus Status { get; set; }
    public required string AgreementPublicKey { get; set; }
    public required string SigningPublicKey { get; set; }
    /// <summary>
    /// SHA-256 fingerprint of AgreementPublicKey, displayed as "XXXX-XXXX-XXXX-XXXX"
    /// for human verification during pairing.
    /// </summary>
    public string? Fingerprint { get; set; }
    public string? OsVersion { get; set; }
    public string? AppVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ActivatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
}
