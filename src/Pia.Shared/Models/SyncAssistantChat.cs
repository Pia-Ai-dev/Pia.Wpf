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

    /// <summary>
    /// Per-chat working directory, RELATIVE to the assistant-files sandbox root
    /// (forward slashes). Null/empty = sandbox root.
    /// </summary>
    public string? WorkingDirectory { get; set; }

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

    // Content / ThinkingContent are UTF-8 text only — never inline base64
    // binaries (images, PDFs, audio, files) here. Per the server contract
    // (docs/server/assistant-chat-history.md §5 "Text-only payloads"),
    // attachments must flow through a separate transport and stay out of
    // chat-history sync.
    public string Content { get; set; } = string.Empty;
    public string? ThinkingContent { get; set; }
    public DateTime Timestamp { get; set; }

    public int? Tokens { get; set; }
    public string? ModelName { get; set; }

    /// <summary>Provider of <see cref="ModelName"/> (e.g. "OpenAI"); null for Pia Cloud and for messages saved before it was recorded.</summary>
    public string? ProviderName { get; set; }

    /// <summary>The server routed this answer to the protected model (guardrail hit).</summary>
    public bool IsProtectedRoute { get; set; }

    /// <summary>
    /// Persona that produced this (assistant) message; null for user messages and for
    /// messages saved before persona attribution existed. Old clients round-trip this
    /// via <see cref="ExtensionData"/>.
    /// </summary>
    public SyncMessagePersona? Persona { get; set; }

    /// <summary>
    /// Files the user attached to this message. Metadata only: the display name plus, when the file was
    /// copied into the assistant-files sandbox, its path RELATIVE to that root. Never file content and
    /// never the file's original absolute path, which would leak the sender's local folder layout.
    /// </summary>
    public List<SyncMessageAttachedFile>? AttachedFiles { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>One file attached to a user message (see AttachedFileRef client-side).</summary>
public sealed class SyncMessageAttachedFile
{
    public string FileName { get; set; } = string.Empty;

    /// <summary>Relative to the assistant-files sandbox root; null when the file was never saved there.</summary>
    public string? RelativePath { get; set; }
}

/// <summary>Snapshot of the persona that produced an assistant message (see PersonaAttribution client-side).</summary>
public sealed class SyncMessagePersona
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Emoji { get; set; }
}
