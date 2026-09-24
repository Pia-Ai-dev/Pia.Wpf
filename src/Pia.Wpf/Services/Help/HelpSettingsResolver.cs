using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.Tts;

namespace Pia.Services.Help;

/// <summary>
/// Reports what this install is actually set to, and where in the UI each value is changed. The guide
/// describes a build; this describes the machine in front of the user — and the path is resolved
/// through the localization service, so it names the labels they can actually see on screen.
/// </summary>
public sealed class HelpSettingsResolver
{
    public const string AllAreas = "all";

    /// <summary>The areas the tool offers. Ordered so the summary reads top-down like the app does.</summary>
    public static IReadOnlyList<string> Areas { get; } =
        ["language", "speech", "assistant", "agent", "providers", "personas", "meetings", "sync", "tools"];

    private readonly ISettingsService _settingsService;
    private readonly ILocalizationService _localizationService;
    private readonly IPersonaService _personaService;
    private readonly IProviderService _providerService;

    public HelpSettingsResolver(
        ISettingsService settingsService,
        ILocalizationService localizationService,
        IPersonaService personaService,
        IProviderService providerService)
    {
        _settingsService = settingsService;
        _localizationService = localizationService;
        _personaService = personaService;
        _providerService = providerService;
    }

    public async Task<IReadOnlyList<HelpSettingRow>> DescribeAsync(string? area, CancellationToken ct = default)
    {
        var wanted = Normalize(area);
        var settings = await _settingsService.GetSettingsAsync();
        var rows = new List<HelpSettingRow>();

        if (Wants(wanted, "language")) AddLanguage(rows, settings);
        if (Wants(wanted, "speech")) AddSpeech(rows, settings);
        if (Wants(wanted, "assistant")) AddAssistant(rows, settings);
        if (Wants(wanted, "agent")) AddAgent(rows, settings);
        if (Wants(wanted, "providers")) await AddProvidersAsync(rows, settings);
        if (Wants(wanted, "personas")) await AddPersonasAsync(rows, settings);
        if (Wants(wanted, "meetings")) AddMeetings(rows, settings);
        if (Wants(wanted, "sync")) AddSync(rows, settings);
        if (Wants(wanted, "tools")) AddTools(rows, settings);

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
    }

    /// <summary>Always rooted at the Settings entry in the sidebar, so the model never has to guess it.</summary>
    private string Path(params string[] localizationKeys) =>
        string.Join(" > ", localizationKeys.Prepend("Nav_Settings").Select(k => _localizationService[k]));

    /// <summary>Every localization key the paths are built from — the parity test walks this.</summary>
    public static IReadOnlyList<string> PathLocalizationKeys { get; } =
    [
        "Nav_Settings",
        "Settings_Tab_General", "Settings_Tab_Assistant", "Settings_Tab_Providers", "Settings_Tab_Account",
        "Settings_Tab_Personas", "Settings_Tab_ToolPermissions", "Settings_Tab_McpServers",
        "Settings_InnerTab_Application", "Settings_InnerTab_Speech", "Settings_InnerTab_General",
        "Settings_UiLanguage", "Settings_SpeechToTextLanguage",
        "Tts_Title", "Tts_VoiceSelection", "Stt_Title",
        "Settings_Agent_Tab", "Settings_Meeting_Tab",
    ];

    private static string OnOff(bool value) => value ? "on" : "off";

    /// <summary>Host only: the rest of a server URL is neither useful to the model nor the user's to leak.</summary>
    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    private static string LanguageName(TargetLanguage language) => language switch
    {
        TargetLanguage.DE => "German",
        TargetLanguage.FR => "French",
        _ => "English",
    };
}
