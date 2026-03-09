namespace Pia.Shared.Models;

/// <summary>
/// Sync DTO for optimization history sessions.
/// Sessions are append-only and immutable once created. Deduplicated by Id.
/// </summary>
public class SyncSession
{
    public Guid Id { get; set; }
    public string? OriginalText { get; set; }
    public string? OptimizedText { get; set; }
    public Guid TemplateId { get; set; }
    public string? TemplateName { get; set; }
    public Guid ProviderId { get; set; }
    public string? ProviderName { get; set; }
    public bool WasTranscribed { get; set; }
    public DateTime CreatedAt { get; set; }
    public int TokensUsed { get; set; }

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
