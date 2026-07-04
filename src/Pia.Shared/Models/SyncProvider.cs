using System.Text.Json.Serialization;

namespace Pia.Shared.Models;

/// <summary>
/// Sync DTO for AI provider configurations.
/// The API key syncs only inside the E2EE EncryptedPayload; without E2EE, keys are
/// device-local and the plaintext <see cref="ApiKey"/> field stays null in both directions.
/// </summary>
public class SyncProvider
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public int ProviderType { get; set; }
    public string? Endpoint { get; set; }
    public string? ModelName { get; set; }

    /// <summary>Legacy wire field — no longer populated (see class remarks). Kept for schema stability.</summary>
    public string? ApiKey { get; set; }
    public string? AzureDeploymentName { get; set; }
    public bool SupportsToolCalling { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 300;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Base64: AES-GCM encrypted entity payload (nonce‖ciphertext‖tag).
    /// Non-null when E2EE is active; plaintext fields will be null.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EncryptedPayload { get; set; }

    /// <summary>
    /// Base64: DEK wrapped with UMK via AES-GCM (nonce‖wrapped-DEK‖tag).
    /// Non-null when E2EE is active.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WrappedDek { get; set; }
}
