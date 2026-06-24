using System.Text.Json.Serialization;

namespace Pia.Shared.Models;

/// <summary>
/// Sync DTO for assistant personas — a reusable bundle of identity + voice + role + expertise
/// that shapes how the assistant answers (the Assistant-mode analogue of an optimization template).
/// Built-in personas are not synced (they're hardcoded in each client); only user-authored personas
/// travel over the wire. See docs/personas/TARGET/00-shared-contract.md for the canonical schema.
/// </summary>
public class SyncPersona
{
    public Guid Id { get; set; }

    // Textual content (encrypted into EncryptedPayload when E2EE is active; null on the wire then).
    public string? Name { get; set; }
    public string? Tagline { get; set; }
    public string? SystemPrompt { get; set; }
    public string? Guardrails { get; set; }
    public string? OutputFormat { get; set; }
    public List<string>? Expertise { get; set; }

    // Structural / config (always plaintext, even under E2EE).
    public string? Archetype { get; set; }            // "assistant" | "analyst" | "creative" | "visionary" | "explainer" | "custom" (default "custom")
    public string? Emoji { get; set; }
    public string? AccentColor { get; set; }          // "#RRGGBB"
    public int ToolScope { get; set; } = 2;           // 0 = none, 1 = read-only (reserved), 2 = full (default)
    public Guid? PreferredProviderId { get; set; }    // soft reference; null => use the mode default
    public int? ReasoningEffort { get; set; }         // null => provider default
    public int SchemaVersion { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }           // conflict key (last-write-wins)

    /// <summary>
    /// Base64: AES-GCM encrypted entity payload (nonce‖ciphertext‖tag).
    /// Non-null when E2EE is active; the plaintext textual fields will be null.
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
