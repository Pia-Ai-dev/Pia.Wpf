using System.Text.Json.Serialization;

namespace Pia.Shared.Models;

/// <summary>
/// Sync DTO for custom optimization templates.
/// Built-in templates are not synced (they're hardcoded in the client).
/// </summary>
public class SyncTemplate
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public string? Prompt { get; set; }
    public string? Description { get; set; }

    [JsonPropertyName("ExampleText")]
    public string? StyleDescription { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? ModifiedAt { get; set; }

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
