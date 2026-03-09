namespace Pia.Shared.E2EE;

/// <summary>
/// UMK wrapped for a specific device via ECDH key agreement.
/// Format: base64(nonce[12] + ciphertext[32] + tag[16]).
/// </summary>
public class WrappedUmkBlob
{
    public required string DeviceId { get; set; }
    /// <summary>Base64: AES-GCM encrypted UMK (nonce‖ciphertext‖tag).</summary>
    public required string Ciphertext { get; set; }
    /// <summary>Base64: HKDF salt used to derive the wrapping key.</summary>
    public required string HkdfSalt { get; set; }
    public int WrapVersion { get; set; } = 1;
    public string? CreatedByDeviceId { get; set; }
    public DateTime CreatedAt { get; set; }
}
