namespace Pia.Shared.Models;

/// <summary>
/// Sync DTO for kanban columns.
/// </summary>
public class SyncKanbanColumn
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public int SortOrder { get; set; }
    public bool IsDefaultView { get; set; }
    public bool IsClosedColumn { get; set; }
    public DateTime CreatedAt { get; set; }
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
