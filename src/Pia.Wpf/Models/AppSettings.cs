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
    // its taskbar button suppressed) AND the meeting captured silently via the in-browser audio tap (the
    // page mutes its own media elements so nothing reaches the speakers). When true, the window opens
    // normally and the meeting is audible via endpoint loopback. The audio source is derived from this
    // flag (hidden ⇒ silent) — there is no separate audio-source toggle.
    // Machine-specific, so local-only (no SyncSettings mirror).
    public bool MeetingAttendeeShowBrowserWindow { get; set; } = false;

    // Per-speaker diarization for the meeting attendee. On by default; degrades to single-bubble
    // behavior if the speaker-embedding model is unavailable. Local-only (no SyncSettings mirror).
    public bool EnableMeetingDiarization { get; set; } = true;

    // Smart auto-detect: continuously re-cluster all voice embeddings during the meeting and
    // retro-correct earlier speaker assignments. ON by default; when on, the manual tuning knobs
    // below (threshold / max speakers / min speech) are ignored and hidden in the settings UI.
    // Local-only (no SyncSettings mirror).
    public bool MeetingSmartSpeakerDetection { get; set; } = true;
    public float SpeakerEmbeddingThreshold { get; set; } = 0.50f;
    // Caps how many distinct speakers diarization may create in one meeting; 0 = no limit. Local-only.
    public int MeetingMaxSpeakers { get; set; } = 0;
    // Minimum uninterrupted speech length (seconds) before diarization attempts to identify a speaker.
    // Local-only (no SyncSettings mirror).
    public float MeetingMinSpeechSeconds { get; set; } = 1.5f;

    // How often (minutes) the meeting attendee snapshots the Teams participant roster while attending.
    // The accumulated union of names is handed to the "Summarize with assistant" prompt as metadata so
    // the model can attribute the diarized "Speaker N" labels to real people. 0 (or negative) disables
    // roster snapshots entirely. Best-effort: a roster miss never affects the meeting. Local-only.
    public int MeetingAttendeeRosterSnapshotMinutes { get; set; } = 2;

    // Automatically ingest documents in the vault's sources/ folder (watcher + startup reconcile).
    // Each ingest costs two LLM calls to the default provider and writes synced memory pages, so this
    // is the consent gate. Gates only the automatic triggers — the chat ingest tool always works.
    // JSON-only (no settings UI), like MeetingAttendeeRosterSnapshotMinutes.
    public bool AutoIngestSources { get; set; } = true;

    // Schema version for the ingest pipeline. Bumped when the on-disk topic-page format changes OR when
    // topic content must be rebuilt, so a one-time startup migration can wipe stale pages + ingest state
    // and force a fresh re-synthesis. JSON-only (no settings UI), like AutoIngestSources.
    // 0 = pre-synthesis pipeline. 1 = synthesis pipeline. 2 = scope tightening (charter no longer feeds
    // profile.md + ingest restricted to sources/), rebuilds topics free of leaked personal content.
    public int IngestSchemaVersion { get; set; } = 0;

    // Assistant suggestions (follow-up chips)
    public bool AssistantSuggestionsEnabled { get; set; } = false;

    /// <summary>Global last-used Chat/Agent lever default (R15). Not per-chat, not per-mode. false = Chat.</summary>
    public bool AssistantAgentModeDefault { get; set; } = false;

    // Agent-run budget envelope (§5/§13.8) — the generous terminal caps an interactive Planned run
    // stops at. Surfaced in Assistant settings so a user can tighten/loosen them; clamped when a
    // RunProfile is built (RunProfile.FromBudget). Defaults match RunProfile.Interactive.
    public int AgentMaxSteps { get; set; } = 24;
    public int AgentMaxReplans { get; set; } = 2;
    public int AgentWallClockMinutes { get; set; } = 20;

    // Scheduled/headless-run budget envelope (§17.5) — the caps an unattended run (a "Run in background"
    // detach or a scheduled AgentTask job) stops at. Separate from the interactive Agent* knobs because
    // an unattended run has no user watching and gets a longer envelope. Defaults match RunProfile.Scheduled.
    public int ScheduledMaxSteps { get; set; } = 24;
    public int ScheduledMaxReplans { get; set; } = 2;
    public int ScheduledWallClockMinutes { get; set; } = 45;

    // Sandboxed folder the assistant's file tool may read/write/delete in. The memory vault lives
    // under it (<folder>\Vault), so it is always set after first run; file-tool enablement is the
    // separate AssistantFileToolsEnabled flag (clearing the folder no longer disables the tools).
    public string? AssistantFilesFolder { get; set; }

    // Relative subpath (forward slashes) under AssistantFilesFolder that new assistant chats
    // adopt as their working directory. Device-independent (a relative path, not the machine
    // path), so it is synced. Auto-created under the files folder when applied. Empty = sandbox
    // root. Default "Playground".
    public string AssistantDefaultWorkingDirectory { get; set; } = "Playground";

    // True when the assistant's file tools (read/write/delete/list/search) are exposed over
    // AssistantFilesFolder. The folder is always set (the vault lives under it), so file-tool
    // enablement is a distinct flag rather than "clear the folder to disable".
    public bool AssistantFileToolsEnabled { get; set; } = true;

    // True when the assistant's git tools (status/log/diff/branch/show/init/add/commit/switch/restore/stash)
    // are exposed over repositories inside AssistantFilesFolder. Inert when git is not installed (the
    // handler's IsAvailable also requires GitLocator.IsAvailable); the settings toggle is greyed out then.
    public bool AssistantGitToolsEnabled { get; set; } = true;

    // Layout-migration marker, distinct from VaultVersion (SQLite->vault). 0 = pre-nesting
    // (legacy vault at %LOCALAPPDATA%\Pia\Vault, sibling of workdir); 1 = vault nested under
    // AssistantFilesFolder. Set once the in-place nesting migration completes on this device.
    public int AssistantFolderLayoutVersion { get; set; } = 0;

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
    // SHA-256 (base64) of the plaintext settings projection last successfully pushed. Lets the
    // delta/first-sync push omit Settings entirely when the local settings are byte-identical to
    // the last push (E2EE re-encrypts with a fresh DEK/nonce each run, so the ciphertext always
    // differs — the gate must be over plaintext). See SyncMapper.ComputeSettingsHash.
    public string? LastPushedSettingsHash { get; set; }
    // Last plugin-catalog version the server echoed on a pull (SyncPullResponse.CatalogVersion).
    // Sent back as ?catalogVersion= on the next pull so the server can skip re-sending an unchanged
    // plugin catalog (Sec 3.5). Null on first run / pre-upgrade servers => the param is omitted and
    // the server returns the full catalog.
    public long? LastCatalogVersion { get; set; }
    // ETag from the last successful assistant-chat startup pull (GET /api/v1/chats). Echoed as
    // If-None-Match so an unchanged chat set answers 304 with no body. Mirrors LastPullETag.
    public string? LastChatPullETag { get; set; }
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

    /// <summary>
    /// Persistent marker for the memory-vault migration. <c>0</c> = the legacy <c>Memories</c> table has
    /// not yet been migrated into the on-disk vault; <c>1</c> = migration completed on this device.
    /// <see cref="Services.Migration.VaultMigrationRunner"/> sets this to <c>1</c> after a successful run
    /// so the migration is idempotent. Synced across devices via settings so a device that pulls an
    /// already-migrated vault does not re-migrate.
    /// </summary>
    public int VaultVersion { get; set; } = 0;

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
