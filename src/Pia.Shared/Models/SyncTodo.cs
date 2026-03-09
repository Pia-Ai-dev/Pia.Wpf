namespace Pia.Shared.Models;

/// <summary>
/// Sync DTO for todo items.
/// LinkedReminderId is synced as-is (reminder may only exist locally).
/// </summary>
public class SyncTodo
{
    public Guid Id { get; set; }
    public string? Title { get; set; }
    public string? Notes { get; set; }
    public int Priority { get; set; }
    public int Status { get; set; }
    public DateTime? DueDate { get; set; }
    public Guid? LinkedReminderId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

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
