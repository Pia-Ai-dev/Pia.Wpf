namespace Pia.Models;

public enum OutputAction
{
    CopyToClipboard,
    AutoType,
    PasteToPreviousWindow
}

public enum WhisperModelSize
{
    Tiny,
    Base,
    Small,
    Medium,
    Large
}

public enum AppTheme
{
    System,
    Dark,
    Light
}

public enum TargetLanguage
{
    EN,
    DE,
    FR
}

public enum TargetSpeechLanguage
{
    Auto,
    EN,
    DE,
    FR
}

public class AppSettings
{
    public OutputAction DefaultOutputAction { get; set; } = OutputAction.CopyToClipboard;
    public Guid? DefaultTemplateId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? DefaultProviderId { get; set; }
    public WhisperModelSize WhisperModel { get; set; } = WhisperModelSize.Base;
public int AutoTypeDelayMs { get; set; } = 10;
    public string? DraftText { get; set; }
    public string? LastActiveView { get; set; }
    public double WindowWidth { get; set; } = 1000;
    public double WindowHeight { get; set; } = 700;
    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.System;
    public bool StartMinimized { get; set; } = false;
    public bool LaunchAtStartup { get; set; } = true;
    public bool ShowTodoPanelButton { get; set; } = true;
    public bool HasCompletedFirstRunWizard { get; set; } = false;
    public UserOperatingMode? UserOperatingMode { get; set; }
    public KeyboardShortcut OptimizeHotkey { get; set; } = KeyboardShortcut.DefaultCtrlAltO();
    public KeyboardShortcut? AssistantHotkey { get; set; } = KeyboardShortcut.DefaultCtrlAltP();
    public KeyboardShortcut? ResearchHotkey { get; set; } = KeyboardShortcut.DefaultCtrlAltR();
    public TargetLanguage? TargetLanguage { get; set; }
    public TargetSpeechLanguage TargetSpeechLanguage { get; set; } = TargetSpeechLanguage.Auto;
    public WindowMode DefaultWindowMode { get; set; } = WindowMode.Optimize;
    public TargetLanguage UiLanguage { get; set; } = Models.TargetLanguage.EN;
    public Dictionary<WindowMode, Guid> ModeProviderDefaults { get; set; } = new();
    public bool UseSameProviderForAllModes { get; set; } = true;

    // TTS settings
    public bool TtsEnabled { get; set; } = false;
    public string TtsVoiceModelKey { get; set; } = "en_US-lessac-medium";

    // Auto-update
    public bool AutoUpdateEnabled { get; set; } = true;

    // Sync settings
    public bool SyncEnabled { get; set; } = false;
    public bool TrustSelfSignedCertificates { get; set; } = false;
    public string? ServerUrl { get; set; }
    public string? EncryptedAccessToken { get; set; }
    public string? EncryptedRefreshToken { get; set; }
    public string? SyncUserId { get; set; }
    public string? SyncUserEmail { get; set; }
    public string? SyncUserDisplayName { get; set; }
    public string? SyncProvider { get; set; }
    public DateTime? LastSyncTimestamp { get; set; }
    public string? SyncDeviceId { get; set; }

    // E2EE settings
    public bool IsE2EEEnabled { get; set; }
    public string? E2EEEncryptedUmk { get; set; }
    public string? E2EEDeviceId { get; set; }
    public int E2EEUmkVersion { get; set; }
    public bool E2EERecoveryConfigured { get; set; }

    // Privacy settings
    public PrivacySettings Privacy { get; set; } = new();

    public Guid? GetProviderForMode(WindowMode mode)
    {
        if (UseSameProviderForAllModes)
        {
            return ModeProviderDefaults.TryGetValue(WindowMode.Optimize, out var id) ? id : DefaultProviderId;
        }
        return ModeProviderDefaults.TryGetValue(mode, out var modeId) ? modeId : DefaultProviderId;
    }

    public void SetProviderForMode(WindowMode mode, Guid? providerId)
    {
        if (providerId.HasValue)
            ModeProviderDefaults[mode] = providerId.Value;
        else
            ModeProviderDefaults.Remove(mode);
    }

    public void MigrateFromLegacyDefault()
    {
        if (DefaultProviderId.HasValue && ModeProviderDefaults.Count == 0)
        {
            ModeProviderDefaults[WindowMode.Optimize] = DefaultProviderId.Value;
            ModeProviderDefaults[WindowMode.Assistant] = DefaultProviderId.Value;
            ModeProviderDefaults[WindowMode.Research] = DefaultProviderId.Value;
            DefaultProviderId = null;
        }
    }
}
