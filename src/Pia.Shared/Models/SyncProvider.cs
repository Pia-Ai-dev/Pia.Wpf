namespace Pia.Shared.Models;

/// <summary>
/// Sync DTO for AI provider configurations.
/// ApiKey is sent as plaintext over TLS during sync (encrypted at rest on both ends).
/// </summary>
public class SyncProvider
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public int ProviderType { get; set; }
    public string? Endpoint { get; set; }
    public string? ModelName { get; set; }
    public string? ApiKey { get; set; }
    public string? AzureDeploymentName { get; set; }
    public bool SupportsToolCalling { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Provider-specific options
    public int ReasoningEffort { get; set; }
    public bool WebSearchEnabled { get; set; }
    public bool ExtendedThinkingEnabled { get; set; }
    public int? ThinkingBudgetTokens { get; set; }
    public bool PromptCachingEnabled { get; set; }

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
