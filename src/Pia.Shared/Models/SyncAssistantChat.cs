using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pia.Shared.Models;

/// <summary>
/// Sync DTO for a stored assistant conversation. Doubles as the persistence
/// shape used by the local SQLite service — there is no separate local model.
/// See docs/server/assistant-chat-history.md for the wire contract.
/// </summary>
public class SyncAssistantChat
{
    public Guid Id { get; set; }

    /// <summary>Schema version of this chat document. Currently 1.</summary>
    public int SchemaVersion { get; set; } = 1;

    public string? Title { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime LastAccessedAt { get; set; }

    public string WindowMode { get; set; } = "Assistant";
    public Guid? ProviderId { get; set; }

    public List<SyncAssistantChatMessage> Messages { get; set; } = [];

    /// <summary>
    /// Base64: AES-GCM encrypted entity payload (nonce‖ciphertext‖tag).
    /// Non-null when E2EE is active; plaintext content fields will be null.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EncryptedPayload { get; set; }

    /// <summary>
    /// Base64: DEK wrapped with UMK via AES-GCM (nonce‖wrapped-DEK‖tag).
    /// Non-null when E2EE is active.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WrappedDek { get; set; }

    /// <summary>
    /// Round-trips unknown fields the server may add in future schema versions
    /// (see docs/server/assistant-chat-history.md §1 forward-compatibility).
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class SyncAssistantChatMessage
{
    public Guid Id { get; set; }

    /// <summary>"user" or "assistant".</summary>
    public string Role { get; set; } = "user";

    public string Content { get; set; } = string.Empty;
    public string? ThinkingContent { get; set; }
    public DateTime Timestamp { get; set; }

    public int? Tokens { get; set; }
    public string? ModelName { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
