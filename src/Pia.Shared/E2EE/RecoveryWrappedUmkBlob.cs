namespace Pia.Shared.E2EE;

/// <summary>
/// UMK wrapped with a key derived from the user's recovery code via Argon2id.
/// </summary>
public class RecoveryWrappedUmkBlob
{
    /// <summary>Base64: AES-GCM encrypted UMK (nonce‖ciphertext‖tag).</summary>
    public required string Ciphertext { get; set; }
    /// <summary>Base64: Argon2id salt.</summary>
    public required string KdfSalt { get; set; }
    /// <summary>Argon2id memory cost in KB.</summary>
    public int KdfMemoryCostKb { get; set; }
    /// <summary>Argon2id time cost (iterations).</summary>
    public int KdfTimeCost { get; set; }
    /// <summary>Argon2id parallelism.</summary>
    public int KdfParallelism { get; set; }
    public int WrapVersion { get; set; } = 1;
    public int UmkVersion { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
}
