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

public enum SttBackend
{
    Whisper,
    Parakeet
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
    public SttBackend SttBackend { get; set; } = SttBackend.Parakeet;
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
    /// <summary>Whether the Flow rail is pinned as a docked column (design §4). Persisted across restarts.</summary>
    public bool FlowPinned { get; set; } = false;
    public Dictionary<Guid, double> TodoColumnWidths { get; set; } = new();
    public bool HasCompletedFirstRunWizard { get; set; } = false;
    public UserOperatingMode? UserOperatingMode { get; set; }
    public KeyboardShortcut OptimizeHotkey { get; set; } = KeyboardShortcut.DefaultCtrlAltO();
    public KeyboardShortcut? AssistantHotkey { get; set; } = KeyboardShortcut.DefaultCtrlAltP();
    public KeyboardShortcut? FastPathHotkey { get; set; }
    public bool AutoCaptureSelectedText { get; set; } = true;
    public TargetLanguage? TargetLanguage { get; set; }
    public TargetSpeechLanguage TargetSpeechLanguage { get; set; } = TargetSpeechLanguage.Auto;
    public WindowMode DefaultWindowMode { get; set; } = WindowMode.Optimize;
    public TargetLanguage UiLanguage { get; set; } = Models.TargetLanguage.EN;
    public Dictionary<WindowMode, Guid> ModeProviderDefaults { get; set; } = new();
    public bool UseSameProviderForAllModes { get; set; } = true;

    /// <summary>
    /// Per-mode active-persona selection. May reference a built-in persona Guid (identical on every
    /// device). Absent entries fall back to the UserOperatingMode-mapped Pia built-in (contract §7).
    /// Synced via <c>SyncSettings.ModePersonaDefaults</c>, mirroring <see cref="ModeProviderDefaults"/>.
    /// </summary>
    public Dictionary<WindowMode, Guid> ModePersonaDefaults { get; set; } = new();

    /// <summary>
    /// Allow-list of sync login providers. Null/empty = all providers allowed.
    /// Recognized values: "local", "google", "microsoft", "entraid" (case-insensitive).
    /// Intended to be set via enterprise policy.
    /// </summary>
    public List<string>? AllowedSyncProviders { get; set; }

    /// <summary>
    /// Standing per-tool "always allow" grants. Keyed by (PluginId, ToolName).
    /// Persisted globally as camelCase JSON, mirroring <see cref="AllowedSyncProviders"/>.
    /// </summary>
    public List<ToolGrant> AlwaysAllowedTools { get; set; } = new();

    // TTS settings
    public bool TtsEnabled { get; set; } = false;
    public string TtsVoiceModelKey { get; set; } = "en_US-lessac-medium";

    // Meeting attendee / live transcription settings
    public string? LastCounterpartName { get; set; }
    // The display name the assistant joins meetings under (editable in the join dialog). Blank/null
    // falls back to the auto-built "{user}'s assistant" (see MeetingAttendeeService.BuildDisplayName).
    public string? MeetingAttendeeDisplayName { get; set; }
    public string? MeetingTranscriptFolder { get; set; }

    // Which browser the meeting attendee drives. Bundled Chromium is the only Playwright-guaranteed
    // build (reliable default); System Chrome/Edge are opt-in convenience (may be affected by browser
    // updates / enterprise policy); SystemDefault detects the OS default and falls back to bundled when
    // it is not a Chromium-family browser. Machine-specific, so local-only (no SyncSettings mirror).
    public MeetingBrowserSelection MeetingBrowserSelection { get; set; } = MeetingBrowserSelection.BundledChromium;

    // Show the attendee's browser window on-screen. Default false = hidden (window parked off-screen and
    // its taskbar button suppressed) AND the meeting captured silently via per-process loopback. When
    // true, the window opens normally and the meeting is audible via endpoint loopback. The audio source
    // is derived from this flag (hidden ⇒ silent) — there is no separate audio-source toggle.
    // Machine-specific, so local-only (no SyncSettings mirror).
    public bool MeetingAttendeeShowBrowserWindow { get; set; } = false;

    // Per-speaker diarization for the meeting attendee. On by default; degrades to single-bubble
    // behavior if the speaker-embedding model is unavailable. Local-only (no SyncSettings mirror).
    public bool EnableMeetingDiarization { get; set; } = true;
    public float SpeakerEmbeddingThreshold { get; set; } = 0.50f;
    // Caps how many distinct speakers diarization may create in one meeting; 0 = no limit. Local-only.
    public int MeetingMaxSpeakers { get; set; } = 0;
    // Minimum uninterrupted speech length (seconds) before diarization attempts to identify a speaker.
    // Local-only (no SyncSettings mirror).
    public float MeetingMinSpeechSeconds { get; set; } = 1.5f;

    // Assistant suggestions (follow-up chips)
    public bool AssistantSuggestionsEnabled { get; set; } = false;

    // Sandboxed folder the assistant's file tool may read/write/delete in.
    // Null/empty disables the tool entirely.
    public string? AssistantFilesFolder { get; set; }

    // Assistant chat history
    public bool ChatHistoryEnabled { get; set; } = true;
    public int ChatHistoryRetentionDays { get; set; } = 30;
    public bool ChatAutoTitleEnabled { get; set; } = false;

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
    public string? LastPullETag { get; set; }
    public string? SyncDeviceId { get; set; }

    // One-time gate for the assistant-chat startup backfill. Chats predating
    // cloud sign-in never raised ChatsChanged, so without a backfill they'd
    // never reach the cloud. Set once the full push completes; cleared on logout
    // so a different account re-backfills. See AssistantChatSyncService.
    public DateTime? AssistantChatsBackfilledAt { get; set; }

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

    public Guid? GetPersonaForMode(WindowMode mode) =>
        ModePersonaDefaults.TryGetValue(mode, out var id) ? id : null;

    public void SetPersonaForMode(WindowMode mode, Guid? personaId)
    {
        if (personaId.HasValue)
            ModePersonaDefaults[mode] = personaId.Value;
        else
            ModePersonaDefaults.Remove(mode);
    }

    public void MigrateFromLegacyDefault()
    {
        if (DefaultProviderId.HasValue && ModeProviderDefaults.Count == 0)
        {
            ModeProviderDefaults[WindowMode.Optimize] = DefaultProviderId.Value;
            ModeProviderDefaults[WindowMode.Assistant] = DefaultProviderId.Value;
            DefaultProviderId = null;
        }
    }
}
