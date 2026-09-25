using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.Tts;

namespace Pia.Services.Help;

/// <summary>Reports this install's actual settings, each with the localized path to change it.</summary>
public sealed class HelpSettingsResolver
{
    public const string AllAreas = "all";

    /// <summary>Ordered so the summary reads top-down like the app does.</summary>
    public static IReadOnlyList<string> Areas { get; } =
    [
        "language", "application", "hotkeys", "speech", "privacy", "providers", "optimize",
        "assistant", "personas", "tools", "meetings", "agent", "sync", "about",
    ];

    private readonly ISettingsService _settingsService;
    private readonly ILocalizationService _localizationService;
    private readonly IPersonaService _personaService;
    private readonly IProviderService _providerService;
    private readonly ITemplateService _templateService;

    public HelpSettingsResolver(
        ISettingsService settingsService,
        ILocalizationService localizationService,
        IPersonaService personaService,
        IProviderService providerService,
        ITemplateService templateService)
    {
        _settingsService = settingsService;
        _localizationService = localizationService;
        _personaService = personaService;
        _providerService = providerService;
        _templateService = templateService;
    }

    public async Task<IReadOnlyList<HelpSettingRow>> DescribeAsync(string? area, CancellationToken ct = default)
    {
        var wanted = Normalize(area);
        var settings = await _settingsService.GetSettingsAsync();
        var rows = new List<HelpSettingRow>();

        if (Wants(wanted, "language")) AddLanguage(rows, settings);
        if (Wants(wanted, "application")) AddApplication(rows, settings);
        if (Wants(wanted, "hotkeys")) AddHotkeys(rows, settings);
        if (Wants(wanted, "speech")) AddSpeech(rows, settings);
        if (Wants(wanted, "privacy")) AddPrivacy(rows, settings);
        if (Wants(wanted, "providers")) await AddProvidersAsync(rows, settings);
        if (Wants(wanted, "optimize")) await AddOptimizeAsync(rows, settings);
        if (Wants(wanted, "assistant")) AddAssistant(rows, settings);
        if (Wants(wanted, "personas")) await AddPersonasAsync(rows, settings);
        if (Wants(wanted, "tools")) AddTools(rows, settings);
        if (Wants(wanted, "meetings")) AddMeetings(rows, settings);
        if (Wants(wanted, "agent")) AddAgent(rows, settings);
        if (Wants(wanted, "sync")) AddSync(rows, settings);
        if (Wants(wanted, "about")) AddAbout(rows);

        ct.ThrowIfCancellationRequested();
        return rows;
    }

    /// <summary>Returns the requested area, or <see cref="AllAreas"/> when it is absent or unrecognised.</summary>
    public static string Normalize(string? area)
    {
        if (string.IsNullOrWhiteSpace(area)) return AllAreas;
        var trimmed = area.Trim().ToLowerInvariant();
        return Areas.Contains(trimmed) ? trimmed : AllAreas;
    }

    private static bool Wants(string wanted, string area) => wanted == AllAreas || wanted == area;

    private void AddLanguage(List<HelpSettingRow> rows, AppSettings settings)
    {
        rows.Add(new HelpSettingRow(
            "language",
            "Interface language — also the language Pia answers in",
            LanguageName(settings.UiLanguage),
            Path("Settings_Tab_General", "Settings_InnerTab_Application", "Settings_UiLanguage")));

        rows.Add(new HelpSettingRow(
            "language",
            "Answer language",
            "follows the interface language; there is no separate setting. Ask Pia to switch for one conversation.",
            Path("Settings_Tab_General", "Settings_InnerTab_Application", "Settings_UiLanguage")));

        rows.Add(new HelpSettingRow(
            "language",
            "Speech recognition language (dictation input, not output)",
            settings.TargetSpeechLanguage.ToString(),
            Path("Settings_Tab_General", "Settings_InnerTab_Speech", "Settings_SpeechToTextLanguage")));
    }

    private void AddApplication(List<HelpSettingRow> rows, AppSettings settings)
    {
        string At(string labelKey) => Path("Settings_Tab_General", "Settings_InnerTab_Application", labelKey);

        rows.Add(new HelpSettingRow("application", "Window the main button opens",
            settings.DefaultWindowMode.ToString(), At("Settings_DefaultWindowMode")));
        rows.Add(new HelpSettingRow("application", "Launch at Windows startup",
            OnOff(settings.LaunchAtStartup), At("Settings_LaunchAtStartup")));
        rows.Add(new HelpSettingRow("application", "Start minimized to the system tray",
            OnOff(settings.StartMinimized), At("Settings_StartMinimized")));
        rows.Add(new HelpSettingRow("application", "Paste the selected text into Pia when a hotkey opens it",
            OnOff(settings.AutoCaptureSelectedText), At("Settings_AutoCaptureSelectedText")));
        rows.Add(new HelpSettingRow("application", "Check for updates automatically",
            OnOff(settings.AutoUpdateEnabled), At("Settings_AutoUpdateEnabled")));
        rows.Add(new HelpSettingRow("application", "Export diagnostics (a redacted log zip for support)",
            "a button; nothing is sent anywhere", At("Settings_ExportDiagnostics")));
        rows.Add(new HelpSettingRow("application", "Reset the application",
            "a button that deletes all local data and restarts Pia; it cannot be undone", At("Settings_ResetAppData")));
    }

    private void AddHotkeys(List<HelpSettingRow> rows, AppSettings settings)
    {
        string At(string labelKey) => Path("Settings_Tab_General", "Settings_InnerTab_Hotkeys", labelKey);

        rows.Add(new HelpSettingRow("hotkeys", "Open the Assistant",
            HotkeyText(settings.AssistantHotkey), At("Settings_Hotkey_Assistant")));
        rows.Add(new HelpSettingRow("hotkeys", "Open Optimize",
            HotkeyText(settings.OptimizeHotkey), At("Settings_Hotkey_Optimize")));
        rows.Add(new HelpSettingRow("hotkeys", "Fast path (capture, optimize and apply in one keystroke)",
            HotkeyText(settings.FastPathHotkey), At("Settings_Hotkey_FastPath")));
        rows.Add(new HelpSettingRow("hotkeys", "Capture the screen into the Assistant",
            HotkeyText(settings.ScreenCaptureHotkey), At("Settings_Hotkey_ScreenCapture")));
    }

    private void AddSpeech(List<HelpSettingRow> rows, AppSettings settings)
    {
        var voice = TtsVoiceCatalog.Curated.FirstOrDefault(v => v.Key == settings.TtsVoiceModelKey);
        var voiceValue = voice is null
            ? settings.TtsVoiceModelKey
            : $"{voice.DisplayName} ({voice.Language})";

        rows.Add(new HelpSettingRow(
            "speech",
            "Read answers aloud (text-to-speech)",
            settings.TtsEnabled ? "on" : "off",
            Path("Settings_Tab_General", "Settings_InnerTab_Speech", "Tts_Title")));

        rows.Add(new HelpSettingRow(
            "speech",
            "Active voice",
            voiceValue,
            Path("Settings_Tab_General", "Settings_InnerTab_Speech", "Tts_Title", "Tts_VoiceSelection")));

        // The single most-asked question Pia used to get wrong: users look for a language dropdown.
        rows.Add(new HelpSettingRow(
            "speech",
            "Spoken output language",
            "a property of the voice, not a separate setting — download and Select a voice in the language you want. "
            + "Available: " + string.Join(", ", TtsVoiceCatalog.Curated.Select(v => $"{v.DisplayName} ({v.Language})")),
            Path("Settings_Tab_General", "Settings_InnerTab_Speech", "Tts_Title", "Tts_VoiceSelection")));

        rows.Add(new HelpSettingRow(
            "speech",
            "Speech-to-text engine",
            settings.SttBackend == SttBackend.Whisper
                ? $"Whisper ({settings.WhisperModel})"
                : settings.SttBackend.ToString(),
            Path("Settings_Tab_General", "Settings_InnerTab_Speech", "Stt_Title")));
    }

    private void AddPrivacy(List<HelpSettingRow> rows, AppSettings settings)
    {
        string At(string labelKey) => Path("Settings_Tab_General", "Settings_InnerTab_Privacy", labelKey);
        var keywords = settings.Privacy.PiiKeywords.Count;

        rows.Add(new HelpSettingRow("privacy", "Replace personal data with tokens before it reaches the AI provider",
            OnOff(settings.Privacy.TokenizationEnabled), At("Settings_Privacy_Tokenization")));
        // The keywords are the user's secrets; a count confirms they saved without sending them to the provider.
        rows.Add(new HelpSettingRow("privacy", "Private keywords (always treated as personal data)",
            keywords == 0 ? "none" : keywords + " configured", At("Settings_Privacy_Keywords")));
    }

    private async Task AddOptimizeAsync(List<HelpSettingRow> rows, AppSettings settings)
    {
        string At(string labelKey) => Path("Settings_Tab_Optimize", "Settings_InnerTab_General", labelKey);
        var templatesPath = Path("Settings_Tab_Optimize", "Settings_Tab_Templates");
        var templates = await _templateService.GetTemplatesAsync();
        var defaultTemplate = templates.FirstOrDefault(t => t.Id == settings.DefaultTemplateId);

        rows.Add(new HelpSettingRow("optimize", "What happens with the optimized text",
            OutputActionText(settings.DefaultOutputAction), At("Settings_OutputAction")));
        rows.Add(new HelpSettingRow("optimize", "Delay between keystrokes when auto-typing",
            settings.AutoTypeDelayMs + " ms", At("Settings_AutoTypeDelay")));
        rows.Add(new HelpSettingRow("optimize", "Default template",
            defaultTemplate?.Name ?? "none", templatesPath));
        rows.Add(new HelpSettingRow("optimize", "Available templates",
            templates.Count == 0 ? "none" : string.Join(", ", templates.Select(t => t.Name)), templatesPath));
    }

    private void AddAssistant(List<HelpSettingRow> rows, AppSettings settings)
    {
        var general = Path("Settings_Tab_Assistant", "Settings_InnerTab_General");

        rows.Add(new HelpSettingRow("assistant", "Assistant files folder",
            string.IsNullOrWhiteSpace(settings.AssistantFilesFolder) ? "not set" : settings.AssistantFilesFolder!, general));
        rows.Add(new HelpSettingRow("assistant", "Default working folder for new chats",
            settings.AssistantDefaultWorkingDirectory, general));
        rows.Add(new HelpSettingRow("assistant", "File tools (read, search, edit files)",
            OnOff(settings.AssistantFileToolsEnabled), general));
        rows.Add(new HelpSettingRow("assistant", "Git tools", OnOff(settings.AssistantGitToolsEnabled), general));
        rows.Add(new HelpSettingRow("assistant", "Chat-history tools (search past conversations)",
            OnOff(settings.AssistantChatHistoryToolsEnabled), general));
        rows.Add(new HelpSettingRow("assistant", "Follow-up suggestions",
            OnOff(settings.AssistantSuggestionsEnabled), general));
        rows.Add(new HelpSettingRow("assistant", "Chat history retention",
            settings.GetChatHistoryRetentionDays() + " days", general));
        rows.Add(new HelpSettingRow("assistant", "Automatic chat titles",
            OnOff(settings.ChatAutoTitleEnabled), general));
    }

    private void AddAgent(List<HelpSettingRow> rows, AppSettings settings)
    {
        var agent = Path("Settings_Tab_Assistant", "Settings_Agent_Tab");

        rows.Add(new HelpSettingRow("agent", "Agent mode (multi-step runs with a plan you approve)",
            "available in every chat — the Chat/Agent lever sits in the message bar", agent));
        rows.Add(new HelpSettingRow("agent", "New chats start in Agent mode",
            settings.AssistantNewChatAgentMode ? "yes" : "no, they start in Chat mode", agent));
        rows.Add(new HelpSettingRow("agent", "Maximum steps per run", settings.AgentMaxSteps.ToString(), agent));
        rows.Add(new HelpSettingRow("agent", "Time limit per run", settings.AgentWallClockMinutes + " minutes", agent));
        rows.Add(new HelpSettingRow("agent", "Maximum replans", settings.AgentMaxReplans.ToString(), agent));
        rows.Add(new HelpSettingRow("agent", "Tool rounds per step", settings.MaxToolRoundsPerStep.ToString(), agent));
        rows.Add(new HelpSettingRow("agent", "Approve built-in writes automatically during a run",
            OnOff(settings.AgentRunAutoApproveBuiltInWrites),
            Path("Settings_Tab_Assistant", "Settings_Tab_ToolPermissions")));
    }

    private async Task AddProvidersAsync(List<HelpSettingRow> rows, AppSettings settings)
    {
        var path = Path("Settings_Tab_Providers");
        var providers = await _providerService.GetProvidersAsync();

        foreach (var mode in new[] { WindowMode.Assistant, WindowMode.Optimize })
        {
            var id = settings.GetProviderForMode(mode);
            var provider = id is null ? null : providers.FirstOrDefault(p => p.Id == id.Value);
            var value = provider is null
                ? "not configured"
                : $"{provider.Name}{(string.IsNullOrWhiteSpace(provider.ModelName) ? string.Empty : $" ({provider.ModelName})")}"
                  + (provider.SupportsToolCalling ? string.Empty : " — this provider cannot call tools");
            rows.Add(new HelpSettingRow("providers", $"AI provider for {mode} mode", value, path));
        }

        rows.Add(new HelpSettingRow("providers", "Configured providers",
            providers.Count == 0 ? "none" : string.Join(", ", providers.Select(p => p.Name)), path));
    }

    private async Task AddPersonasAsync(List<HelpSettingRow> rows, AppSettings settings)
    {
        var path = Path("Settings_Tab_Assistant", "Settings_Tab_Personas");
        var personas = await _personaService.GetPersonasAsync();
        var activeId = settings.GetPersonaForMode(WindowMode.Assistant);
        var active = activeId is null ? null : personas.FirstOrDefault(p => p.Id == activeId.Value);

        rows.Add(new HelpSettingRow("personas", "Active persona in the Assistant",
            active?.Name ?? "the built-in default", path));
        rows.Add(new HelpSettingRow("personas", "How Pia words its answers",
            "set by the active persona's System Prompt, Guardrails and Output Format", path));
        rows.Add(new HelpSettingRow("personas", "Available personas",
            personas.Count == 0 ? "none" : string.Join(", ", personas.Select(p => p.Name)), path));
    }

    private void AddMeetings(List<HelpSettingRow> rows, AppSettings settings)
    {
        var path = Path("Settings_Tab_Assistant", "Settings_Meeting_Tab");

        rows.Add(new HelpSettingRow("meetings", "Join a meeting and transcribe",
            OnOff(settings.MeetingAttendeeEnabled), path));
        rows.Add(new HelpSettingRow("meetings", "Live transcription from this room's microphone",
            OnOff(settings.DirectTranscriptionEnabled), path));
        rows.Add(new HelpSettingRow("meetings", "Speaker separation",
            OnOff(settings.EnableMeetingDiarization), path));
    }

    private void AddSync(List<HelpSettingRow> rows, AppSettings settings)
    {
        var path = Path("Settings_Tab_Account");

        rows.Add(new HelpSettingRow("sync", "Cloud sync", OnOff(settings.SyncEnabled), path));
        rows.Add(new HelpSettingRow("sync", "Signed in",
            string.IsNullOrWhiteSpace(settings.SyncUserId) ? "no" : "yes", path));
        rows.Add(new HelpSettingRow("sync", "Server",
            string.IsNullOrWhiteSpace(settings.ServerUrl) ? "not set" : HostOf(settings.ServerUrl!), path));
        rows.Add(new HelpSettingRow("sync", "End-to-end encryption", OnOff(settings.IsE2EEEnabled), path));
    }

    private void AddTools(List<HelpSettingRow> rows, AppSettings settings)
    {
        var path = Path("Settings_Tab_Assistant", "Settings_Tab_ToolPermissions");

        rows.Add(new HelpSettingRow("tools", "Always-allowed tools",
            settings.AlwaysAllowedTools.Count == 0
                ? "none — Pia asks before every write"
                : string.Join(", ", settings.AlwaysAllowedTools.Select(g => g.ToolName)),
            path));

        // Not listed by name: PluginService owns that state and already depends on the help handler,
        // so reading it back here would close a DI cycle. The path is the part the user needs anyway.
        rows.Add(new HelpSettingRow("tools", "Local MCP servers (extra tools from another program)",
            "added and enabled here",
            Path("Settings_Tab_Assistant", "Settings_Tab_McpServers")));
        rows.Add(new HelpSettingRow("tools", "Plugins from Pia Cloud",
            "switched on or off here; needs a Pia Cloud connection",
            Path("Settings_Tab_Plugins")));
    }

    private void AddAbout(List<HelpSettingRow> rows)
    {
        rows.Add(new HelpSettingRow("about", "Installed version", AppVersionInfo.Version, Path("Settings_Tab_About")));
    }

    /// <summary>Always rooted at the Settings entry in the sidebar, so the model never has to guess it.</summary>
    private string Path(params string[] localizationKeys) =>
        string.Join(" > ", localizationKeys.Prepend("Nav_Settings").Select(k => _localizationService[k]));

    /// <summary>Every localization key the paths are built from — the parity test walks this.</summary>
    public static IReadOnlyList<string> PathLocalizationKeys { get; } =
    [
        "Nav_Settings",
        "Settings_Tab_General", "Settings_Tab_Providers", "Settings_Tab_Optimize", "Settings_Tab_Assistant",
        "Settings_Tab_Account", "Settings_Tab_Plugins", "Settings_Tab_About",
        "Settings_InnerTab_Application", "Settings_InnerTab_Hotkeys", "Settings_InnerTab_Speech",
        "Settings_InnerTab_Privacy", "Settings_InnerTab_General",
        "Settings_Tab_Personas", "Settings_Tab_ToolPermissions", "Settings_Tab_McpServers", "Settings_Tab_Templates",
        "Settings_Agent_Tab", "Settings_Meeting_Tab",
        "Settings_UiLanguage", "Settings_DefaultWindowMode", "Settings_LaunchAtStartup", "Settings_StartMinimized",
        "Settings_AutoCaptureSelectedText", "Settings_AutoUpdateEnabled", "Settings_ExportDiagnostics",
        "Settings_ResetAppData",
        "Settings_Hotkey_Assistant", "Settings_Hotkey_Optimize", "Settings_Hotkey_FastPath", "Settings_Hotkey_ScreenCapture",
        "Settings_SpeechToTextLanguage", "Tts_Title", "Tts_VoiceSelection", "Stt_Title",
        "Settings_Privacy_Tokenization", "Settings_Privacy_Keywords",
        "Settings_OutputAction", "Settings_AutoTypeDelay",
    ];

    private static string OnOff(bool value) => value ? "on" : "off";

    private static string HotkeyText(KeyboardShortcut? shortcut) => shortcut?.DisplayText ?? "not set";

    private static string OutputActionText(OutputAction action) => action switch
    {
        OutputAction.AutoType => "typed into the window you came from",
        OutputAction.PasteToPreviousWindow => "pasted into the window you came from",
        _ => "copied to the clipboard",
    };

    /// <summary>Host only: the rest of a server URL is neither useful to the model nor the user's to leak.</summary>
    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    internal static string LanguageName(TargetLanguage language) => language switch
    {
        TargetLanguage.DE => "German",
        TargetLanguage.FR => "French",
        _ => "English",
    };
}
