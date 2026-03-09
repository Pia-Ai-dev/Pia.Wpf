namespace Pia.Shared.Models;

/// <summary>
/// Sync DTO for memory objects.
/// Embeddings are NOT synced (machine-specific, regenerated locally).
/// </summary>
public class SyncMemory
{
    public Guid Id { get; set; }
    public string? Type { get; set; }
    public string? Label { get; set; }
    public string? Data { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime LastAccessedAt { get; set; }

    /// <summary>
    /// Base64: AES-GCM encrypted entity payload (nonce‖ciphertext‖tag).
    /// Non-null when E2EE is active; plaintext fields will be null.
    /// </summary>
    public string? EncryptedPayload { get; set; }

    /// <summary>
    /// Base64: DEK wrapped with UMK via AES-GCM (nonce‖wrapped-DEK‖tag).
    /// Non-null when E2EE is active.
    /// </summary>
    public string? WrappedDek { get; set; }
}
