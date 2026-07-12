using System.Text.Json.Serialization;

namespace Pia.Shared.Models;

/// <summary>
/// Sync-relevant subset of AppSettings. Machine-specific settings
/// (window position, draft text, hotkeys) are excluded.
/// </summary>
public class SyncSettings
{
    public int DefaultOutputAction { get; set; }
    public Guid? DefaultTemplateId { get; set; }
    public int WhisperModel { get; set; }
    public bool StartInAdvancedMode { get; set; }
    public int AutoTypeDelayMs { get; set; } = 10;
    public int Theme { get; set; }
    public bool StartMinimized { get; set; }
    public int? TargetLanguage { get; set; }
    public int TargetSpeechLanguage { get; set; }
    public int DefaultWindowMode { get; set; }
    public Dictionary<int, Guid> ModeProviderDefaults { get; set; } = new();

    /// <summary>
    /// Per-mode active-persona selection (WindowMode int =&gt; persona Guid), mirroring
    /// <see cref="ModeProviderDefaults"/>. May reference a built-in persona Guid (identical on
    /// every device). Absent entries fall back to the UserOperatingMode-mapped Pia built-in.
    /// </summary>
    public Dictionary<int, Guid> ModePersonaDefaults { get; set; } = new();
    public bool UseSameProviderForAllModes { get; set; } = true;

    /// <summary>
    /// Relative subpath (forward slashes) new assistant chats default their working directory to.
    /// A device-independent relative path (not a machine path), so it syncs. Null from a peer that
    /// predates this field — the apply side must not clobber the local value on null.
    /// </summary>
    public string? AssistantDefaultWorkingDirectory { get; set; }

    public DateTime ModifiedAt { get; set; }

    /// <summary>
    /// Base64: AES-GCM encrypted settings JSON (nonce‖ciphertext‖tag).
    /// Non-null when E2EE is active.
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
