namespace Pia.Shared.Models;

/// <summary>
/// DTO for trusted signing certificates used to verify plugin cab files.
/// Served from GET /api/certificates/trusted.
/// </summary>
public class SyncTrustedCertificate
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string Thumbprint { get; set; } = null!;
    public byte[] PublicKeyData { get; set; } = null!;
    public DateTime UpdatedAt { get; set; }
}
