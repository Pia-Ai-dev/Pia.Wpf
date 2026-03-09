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
    public bool UseSameProviderForAllModes { get; set; } = true;
    public DateTime ModifiedAt { get; set; }

    /// <summary>
    /// Base64: AES-GCM encrypted settings JSON (nonce‖ciphertext‖tag).
    /// Non-null when E2EE is active.
    /// </summary>
    public string? EncryptedPayload { get; set; }

    /// <summary>
    /// Base64: DEK wrapped with UMK via AES-GCM (nonce‖wrapped-DEK‖tag).
    /// Non-null when E2EE is active.
    /// </summary>
    public string? WrappedDek { get; set; }
}
