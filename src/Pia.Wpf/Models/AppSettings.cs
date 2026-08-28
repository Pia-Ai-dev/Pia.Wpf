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
    public WindowMode DefaultWindowMode { get; set; } = WindowMode.Assistant;
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

    /// <summary>
    /// False hides add/edit/delete on AI providers; the configured ones stay usable. Belongs in the
    /// policy's <c>enforce</c> block — under <c>defaults</c> the user can simply switch it back on.
    /// </summary>
    public bool AllowProviderManagement { get; set; } = true;

    /// <summary>
    /// False hides add/edit/delete on personas, leaving built-in and managed ones. Same enforce-only
    /// caveat as <see cref="AllowProviderManagement"/>.
    /// </summary>
    public bool AllowPersonaManagement { get; set; } = true;

    /// <summary>
    /// Built-in personas to hide, by key (<c>PiaPersonal</c>, <c>ExperiencedCoder</c>, …) or Guid.
    /// Hides them from the picker only — the ids stay reserved, so a hidden built-in can never be
    /// re-created as a user persona under the same Guid.
    /// </summary>
    public List<string>? BlockedBuiltInPersonas { get; set; }

    // TTS settings
    public bool TtsEnabled { get; set; } = false;
    public string TtsVoiceModelKey { get; set; } = "en_US-lessac-medium";

    // Meeting attendee / live transcription settings

    /// <summary>False hides the Teams meeting-attendee toggle (the browser bot that joins a call).
    /// Independent of <see cref="DirectTranscriptionEnabled"/>. Enforce-only, like the other locks.</summary>
    public bool MeetingAttendeeEnabled { get; set; } = true;

    /// <summary>False hides the in-room microphone transcription toggle.</summary>
    public bool DirectTranscriptionEnabled { get; set; } = true;

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

    // The escape hatch for a meeting where attribution is visibly wrong: a confidently mislabelled
    // transcript is worse than an unlabelled one. Local-only (no SyncSettings mirror).
    public bool MeetingSuppressSpeakerLabels { get; set; } = false;
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

    // How many SCHEDULED meetings may run at once. The overlay's own meeting is separate and not counted.
    // Each one costs a browser, a VAD, an STT engine and a diarizer, so the ceiling is CPU and memory, not
    // audio — the in-browser tap is per-page, so hidden sessions never contend for a device. Values below 1
    // are read as 1. Machine-specific, so local-only. JSON-only (no settings UI), like the roster cadence.
    public int MaxConcurrentBackgroundMeetings { get; set; } = 2;

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

    /// <summary>Set by the confirm dialog's "don't ask again"; device-local (never in the sync projection).</summary>
    public bool AssistantBackgroundRunConfirmSuppressed { get; set; } = false;

    /// <summary>Set by the Obsidian vault-registration confirm dialog's "don't ask again"; device-local.</summary>
    public bool ObsidianVaultRegistrationConfirmSuppressed { get; set; } = false;

    // Agent-run budget envelope (§5/§13.8) — the generous terminal caps an interactive Planned run
    // stops at. Surfaced in Assistant settings so a user can tighten/loosen them; clamped when a
    // RunProfile is built (RunProfile.FromBudget). Defaults match RunProfile.Interactive.
    public int AgentMaxSteps { get; set; } = 24;
    public int AgentMaxReplans { get; set; } = 2;
    public int AgentWallClockMinutes { get; set; } = 20;

    // The model↔tool loop cap inside one step (AiClientService); shared by all run shapes, clamped at read.
    public int MaxToolRoundsPerStep { get; set; } = 24;

    // Reason-then-emit planning. When true, a plan turn on a provider whose handler DROPS the configured
    // reasoning effort as soon as tools are attached (AzureOpenAI / Ollama / Mistral — see
    // IAiProviderHandler.DropsReasoningEffortWithTools) is split into TWO provider turns: a tool-FREE
    // free-form reasoning turn at the configured effort, then the constrained emit_plan turn seeded with
    // that analysis. Default OFF: it doubles the plan-turn cost, and the plan turn already costs ≥2 rounds
    // (§16 R6). Global, not per-provider — the same answer applies to interactive, detached and scheduled runs.
    public bool AgentPlanReasoningTurnEnabled { get; set; } = false;

    // Batch 04 — per-run autonomy policy default. When true, the preset auto-approves Pia's OWN write tools by
    // CLASS — memory, todo, reminder, scheduling and files — so the caller does not stop at a card for every
    // write. Never covers a delete-like tool (04 D6), never Git (its destructive tools are not delete-like by
    // name, so no rule would stop them), never an external/MCP tool.
    //
    // FOUR consumers, and the fourth is the surprising one — the user-visible copy must name it (it does):
    //   1. an interactive Planned run (ChatSessionManager → LiveTurnExecutor),
    //   2. a "Run in background" detach and 3. a scheduled AgentTask (both HeadlessRunLauncher),
    //   4. VOICE MODE (AssistantViewModel.HandleVoiceModeToolCall, 04 D13) — where there is no run and no
    //      envelope, so the policy is read straight from these settings. Voice has no card surface, so a
    //      covered write there executes with no visual confirmation at all.
    // A RESUME is deliberately NOT a consumer: it reads the parked run's envelope, never this setting, so
    // flipping the toggle between park and Continue cannot widen a run that is already in flight (04 D10).
    //
    // Default OFF: with it on, an unattended run can overwrite files in the assistant folder with nobody
    // watching. Global, like every other Agent*/Scheduled* knob, and local-only (absent from SyncSettings).
    public bool AgentRunAutoApproveBuiltInWrites { get; set; } = false;

    /// <summary>
    /// Batch 07 D1/D7 — the "step specialists" roster: the personas a plan may assign individual steps to,
    /// per <see cref="UserOperatingMode"/>. <b>This dictionary IS the opt-in for per-step personas.</b> Empty
    /// (the default) means the planner is told about no personas, emits no persona key, and every step runs
    /// on the run persona — today's behaviour, prompt text included. There is deliberately no separate
    /// feature toggle: a second switch could disagree with this one.
    /// <para>
    /// Keyed by <see cref="UserOperatingMode"/> rather than <see cref="WindowMode"/> because every agent-run
    /// persona resolution already keys on the CONSTANT <c>WindowMode.Assistant</c>, so a WindowMode-keyed
    /// roster would have exactly one live key forever, whereas Personal vs. Business is a distinction users
    /// actually make (a work roster and a home roster). Shape and helper pair mirror
    /// <see cref="ModePersonaDefaults"/> / <see cref="GetPersonaForMode"/> / <see cref="SetPersonaForMode"/>.
    /// </para>
    /// <para>
    /// Local-only: absent from <c>SyncSettings</c>, like every other <c>Agent*</c> knob.
    /// </para>
    /// </summary>
    public Dictionary<UserOperatingMode, List<Guid>> AgentPersonaRoster { get; set; } = new();

    /// <summary>
    /// Hard cap on roster size, enforced on READ as well as on write. Read-side clamping is the point: a
    /// hand-edited (or one day synced) settings file must not be able to put forty persona lines into every
    /// plan prompt.
    /// </summary>
    public const int MaxAgentPersonaRoster = 6;

    /// <summary>
    /// The configured roster for <paramref name="mode"/>: never null, deduped, order preserved, clamped to
    /// <see cref="MaxAgentPersonaRoster"/>. Ids that no longer resolve to a persona are NOT filtered here —
    /// that needs <c>IPersonaService</c> and happens in <c>StepPersonaResolver.GetRosterAsync</c>.
    /// </summary>
    public IReadOnlyList<Guid> GetAgentPersonaRoster(UserOperatingMode mode) =>
        AgentPersonaRoster.TryGetValue(mode, out var ids) && ids is { Count: > 0 }
            ? ids.Distinct().Take(MaxAgentPersonaRoster).ToList()
            : [];

    /// <summary>
    /// Replaces the roster for <paramref name="mode"/>. An empty list REMOVES the key rather than storing an
    /// empty one, so a user who clears the roster leaves no residue — exactly what
    /// <see cref="SetPersonaForMode"/> does with a null id.
    /// </summary>
    public void SetAgentPersonaRoster(UserOperatingMode mode, IReadOnlyList<Guid> ids)
    {
        var clamped = ids.Distinct().Take(MaxAgentPersonaRoster).ToList();
        if (clamped.Count > 0)
            AgentPersonaRoster[mode] = clamped;
        else
            AgentPersonaRoster.Remove(mode);
    }

    // Scheduled/headless-run budget envelope (§17.5) — the caps an unattended run (a "Run in background"
    // detach or a scheduled AgentTask job) stops at. Separate from the interactive Agent* knobs because
    // an unattended run has no user watching and gets a longer envelope. Defaults match RunProfile.Scheduled.
    public int ScheduledMaxSteps { get; set; } = 24;
    public int ScheduledMaxReplans { get; set; } = 2;
    public int ScheduledWallClockMinutes { get; set; } = 45;

    /// <summary>
    /// T1-1 — how many unattended agent runs this device EXECUTES at once (<c>HeadlessRunLauncher</c>'s parent
    /// slot pool). A width, not a budget: it bounds execution, not dispatch, so N due jobs still create N run
    /// rows and N workspaces immediately and the surplus queues on a slot. Queue time is not charged against a
    /// run's wall clock (the <c>RunContext</c> stopwatch starts after the slot is acquired).
    /// <para>
    /// Live-resizable: <c>HeadlessRunLauncher</c> applies this on every settings save, so raising it starts a
    /// queued run without an app restart and lowering it takes effect as in-flight runs finish (it never
    /// preempts one). Read through <see cref="GetMaxParallelBackgroundRuns"/>, never raw.
    /// </para>
    /// <para>
    /// Local-only: absent from <c>SyncSettings</c>, like every other <c>Agent*</c>/<c>Scheduled*</c> knob —
    /// concurrency a device can sustain is a property OF THE DEVICE, so syncing it would push a workstation's
    /// width onto a laptop. Separate from the CHILD pool, which stays fixed at 2 per delegating parent and has
    /// deliberately no setting (see <c>HeadlessRunLauncher._childSlots</c> for why the two must not merge).
    /// </para>
    /// </summary>
    public int MaxParallelBackgroundRuns { get; set; } = DefaultParallelBackgroundRuns;

    public const int MinParallelBackgroundRuns = 1;

    /// <summary>
    /// Ceiling on <see cref="MaxParallelBackgroundRuns"/>. Not a guess about hardware — it is the number
    /// beyond which the honest bound is a per-provider request throttle rather than a run count. That throttle
    /// has since SHIPPED (T1-2, <see cref="MaxParallelRequestsPerProvider"/>, applied in
    /// <c>AiClientService</c>), so this cap is no longer the only thing standing between a wide run pool and a
    /// provider stampede; it stays where it is because a run count is still the number a person reasons about.
    /// </summary>
    public const int MaxParallelBackgroundRunsCap = 8;

    /// <summary>Two, i.e. the width the pool was hard-coded to before it became configurable.</summary>
    public const int DefaultParallelBackgroundRuns = 2;

    /// <summary>
    /// T1-2 — how many requests this device may have IN FLIGHT against ONE provider at once (plan §18.3
    /// item 3, the dependency §18.4 names for raising <see cref="MaxParallelBackgroundRuns"/> above 1).
    /// Enforced in <c>AiClientService</c> around each outbound round-trip via
    /// <c>IProviderRequestThrottle</c>, keyed on <c>AiProvider.Id</c>: requests to DIFFERENT providers never
    /// queue behind each other.
    /// <para>
    /// The default is deliberately chosen so an install that never touches a setting behaves as it did:
    /// <see cref="DefaultParallelRequestsPerProvider"/> is 4, and the default run pool
    /// (<see cref="DefaultParallelBackgroundRuns"/> = 2) plus the person typing in the chat window is 3
    /// concurrent requests at worst. It bites only where it is meant to — a widened run pool, or a fan-out
    /// adding <c>HeadlessRunLauncher</c>'s fixed 2 children per delegating parent, all on the same provider.
    /// </para>
    /// <para>
    /// Live-resizable with no restart: the throttle applies this value on every acquire. Read through
    /// <see cref="GetMaxParallelRequestsPerProvider"/>, never raw — a hand-edited <c>0</c> would otherwise
    /// build a pool with no permits and hang every request on that provider forever, the same failure
    /// <see cref="GetMaxParallelBackgroundRuns"/>'s clamp exists to prevent.
    /// </para>
    /// <para>
    /// Local-only and JSON-only: absent from <c>SyncSettings</c> like every other concurrency knob (what a
    /// device can sustain is a property OF THE DEVICE), and deliberately given no settings-page row in this
    /// pass — the number a user should be reasoning about is the run pool, and a second concurrency box beside
    /// it would invite tuning the wrong one.
    /// </para>
    /// </summary>
    public int MaxParallelRequestsPerProvider { get; set; } = DefaultParallelRequestsPerProvider;

    public const int MinParallelRequestsPerProvider = 1;

    /// <summary>
    /// Ceiling on <see cref="MaxParallelRequestsPerProvider"/>, and the hard cap of every pool the throttle
    /// builds. Sized to sit above the worst case the other knobs can produce — a full
    /// <see cref="MaxParallelBackgroundRunsCap"/> run pool where every run has delegated its fixed 2 children,
    /// plus an interactive turn — so raising it further is a decision about the PROVIDER's rate limit rather
    /// than about making Pia's own concurrency reachable.
    /// </summary>
    public const int MaxParallelRequestsPerProviderCap = 24;

    /// <summary>Four — see <see cref="MaxParallelRequestsPerProvider"/> for why that is a no-op by default.</summary>
    public const int DefaultParallelRequestsPerProvider = 4;

    /// <summary>The configured per-provider request width, clamped on READ (see the property for why).</summary>
    public int GetMaxParallelRequestsPerProvider() =>
        Math.Clamp(MaxParallelRequestsPerProvider, MinParallelRequestsPerProvider, MaxParallelRequestsPerProviderCap);

    /// <summary>
    /// The configured run-pool width, clamped on READ. The clamp is load-bearing, not decorative: a
    /// hand-edited (or defaulted-to-zero, on a document written by an older build) <c>0</c> would build a pool
    /// with no permits and nothing in the process that could ever release one — every background run would
    /// queue forever, with no error anywhere. Same principle as
    /// <see cref="GetAgentPersonaRoster"/> (clamp where the value is USED, so a bad file cannot break the
    /// invariant), applied to a scalar rather than a collection.
    /// </summary>
    public int GetMaxParallelBackgroundRuns() =>
        Math.Clamp(MaxParallelBackgroundRuns, MinParallelBackgroundRuns, MaxParallelBackgroundRunsCap);

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

    public bool AssistantChatHistoryToolsEnabled { get; set; } = true;

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
    /// <summary>
    /// True once this client has issued its one forced UNCONDITIONAL catalog pull (no
    /// <c>?catalogVersion=</c>, no <c>If-None-Match</c>) for the managed-persona channel.
    /// </summary>
    /// <remarks>
    /// A build that predates managed personas deserialized the pull into <c>SyncPullResponse</c>, silently
    /// ignored the then-unknown <c>managedPersonas</c> property (STJ default), and STILL stored the
    /// <c>catalogVersion</c> that came with it. Such a client echoes an already-current token, the server
    /// fast-skips the catalog, and the managed personas never arrive — until some unrelated admin catalog
    /// write happens, potentially weeks later. The same hole reopens whenever the local managed store is
    /// lost (profile reset, DB rebuild) while the stored token is genuinely current. One unconditional pull
    /// closes both; this flag is what stops it from becoming every pull.
    /// <para>
    /// DEVICE-LOCAL, never synced. The settings sync surface is an explicit allow-list
    /// (<c>SyncMapper.BuildSettingsPlainPayload</c> / <c>SyncSettings</c>), so adding a property here
    /// neither reaches the wire nor perturbs <c>ComputeSettingsHash</c>. It must stay off the wire: it
    /// describes THIS device's local store, and a synced value would tell a freshly-installed second
    /// device that it had already done a pull it never made.
    /// </para>
    /// </remarks>
    public bool ManagedPersonaStoreInitialized { get; set; }
    /// <summary>
    /// Gates one unconditional catalog pull for the group-policy channel, so an upgrading client cannot
    /// echo an already-current <c>catalogVersion</c> and never receive its policy. Device-local, never synced.
    /// </summary>
    public bool ClientPolicyInitialized { get; set; }
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

    // One-shot marker for the blanked-row repair. A build before the E2EE pull guard could write
    // empty rows over real ones and then advance the cursor past them; the repair resets the cursor
    // so they re-pull. Marked once so a repair that finds nothing left on the server (the rows were
    // pushed back blank) does not force a full resync on every launch.
    public DateTime? BlankedSyncRowRepairAt { get; set; }

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
