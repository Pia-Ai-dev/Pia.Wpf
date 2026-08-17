using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

public partial class AssistantSettingsViewModel : ObservableObject
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly ISettingsService _settingsService;
    private readonly IAssistantChatService _chatService;
    private readonly IDialogService _dialogService;
    private readonly ILocalizationService _localizationService;
    private readonly IAssistantFolderRelocationService _relocationService;
    private readonly IWorkingDirectoryService _workingDirectoryService;
    private readonly IPersonaService? _personaService;
    private bool _isLoading;
    // Guards the programmatic revert below from re-entering OnRosterOptionToggled and saving a state
    // nobody chose (07 D-G7.2, the checkbox-cap re-entrancy hazard).
    private bool _isSuppressingRosterToggle;
    // True once LoadAgentRosterOptionsAsync has SETTLED — either with a null IPersonaService (nothing to
    // load, by design) or a successful read. Left FALSE across a faulted GetPersonasAsync so that an
    // unrelated save (a slider drag elsewhere on this tab) does not overwrite a previously configured
    // roster with the empty AgentRosterOptions a failed load leaves behind. `_personaService is not null`
    // alone is NOT this guard: that is true in exactly the faulted-read case this exists to protect against.
    private bool _rosterLoaded;

    public ProvidersSettingsViewModel ProvidersVm { get; }
    public PersonaSettingsViewModel PersonasVm { get; }
    public ToolPermissionsSettingsViewModel ToolPermissionsVm { get; }
    public MeetingSettingsViewModel MeetingVm { get; }

    /// <summary>Index of the inner tab pill (0 = General, 1 = Personas, 2 = Tool access, 3 = Meeting, 4 = Agent runs).</summary>
    [ObservableProperty]
    private int _selectedInnerTabIndex;

    public AssistantSettingsViewModel(
        ProvidersSettingsViewModel providersVm,
        PersonaSettingsViewModel personasVm,
        ToolPermissionsSettingsViewModel toolPermissionsVm,
        MeetingSettingsViewModel meetingVm,
        ILogger<SettingsViewModel> logger,
        ISettingsService settingsService,
        IAssistantChatService chatService,
        IDialogService dialogService,
        ILocalizationService localizationService,
        IAssistantFolderRelocationService relocationService,
        IWorkingDirectoryService workingDirectoryService,
        // Batch 07: trailing and defaulted — this VM already owns PersonasVm but had no IPersonaService of
        // its own (R27), and going through PersonasVm.Personas would couple the roster surface to another
        // VM's load ordering. Null ⇒ the roster surface renders empty and no toggle can be made — the
        // "roster is the opt-in" property (D1) still holds with nothing configured.
        IPersonaService? personaService = null)
    {
        ProvidersVm = providersVm;
        PersonasVm = personasVm;
        ToolPermissionsVm = toolPermissionsVm;
        MeetingVm = meetingVm;
        _logger = logger;
        _settingsService = settingsService;
        _chatService = chatService;
        _dialogService = dialogService;
        _localizationService = localizationService;
        _relocationService = relocationService;
        _workingDirectoryService = workingDirectoryService;
        _personaService = personaService;
        _localizationService.LanguageChanged += (_, _) => OnPropertyChanged(nameof(RetentionDaysDisplay));
    }

    [ObservableProperty]
    private WindowMode _defaultWindowMode;

    [ObservableProperty]
    private bool _suggestionsEnabled;

    // Display-only: the current assistant files folder. Changed via the Change… command (which runs
    // the validated copy/verify/delete move), not by free-text editing.
    [ObservableProperty]
    private string? _filesFolder;

    [ObservableProperty]
    private bool _fileToolsEnabled = true;

    [ObservableProperty]
    private bool _gitToolsEnabled = true;

    // Whether git is installed on this machine. Set once from GitLocator on load; drives the git-tools
    // toggle's enabled state (greyed out when git is absent). The stored bool is inert when git is
    // absent because GitToolHandler.IsAvailable also requires GitLocator.IsAvailable.
    [ObservableProperty]
    private bool _gitToolsAvailable;

    // "<folder>\Vault" — shown beneath the folder so the user sees where memory lives.
    [ObservableProperty]
    private string? _vaultLocationDisplay;

    // Relative subpath new chats default their working directory to (forward slashes; empty = root).
    // Bound to an editable combo — the user can type a new folder or pick an existing one. Validated
    // for sandbox containment (and auto-created) on change; invalid input reverts to the saved value.
    [ObservableProperty]
    private string? _defaultWorkingDirectory;

    // Existing top-level folders under the files folder, offered as the combo's dropdown items.
    public ObservableCollection<string> AvailableWorkingDirectories { get; } = [];

    [ObservableProperty]
    private bool _chatHistoryEnabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RetentionDaysDisplay))]
    private int _chatHistoryRetentionDays = 30;

    [ObservableProperty]
    private bool _chatAutoTitleEnabled;

    public string RetentionDaysDisplay =>
        _localizationService.Format("Settings_Chat_RetentionDays", ChatHistoryRetentionDays);

    public IEnumerable<WindowMode> WindowModes => Enum.GetValues<WindowMode>();

    partial void OnDefaultWindowModeChanged(WindowMode value)
    {
        if (!_isLoading) SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    partial void OnSuggestionsEnabledChanged(bool value)
    {
        if (!_isLoading) SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    partial void OnFilesFolderChanged(string? value)
    {
        // The folder is persisted by the relocation move, not here. Just reflect the derived vault path.
        VaultLocationDisplay = string.IsNullOrWhiteSpace(value)
            ? null
            : _localizationService.Format("Settings_AssistantVaultLocation", _relocationService.GetVaultPath(value));
    }

    partial void OnFileToolsEnabledChanged(bool value)
    {
        if (!_isLoading) SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    partial void OnGitToolsEnabledChanged(bool value)
    {
        if (!_isLoading) SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    partial void OnDefaultWorkingDirectoryChanged(string? value)
    {
        if (_isLoading) return;
        HandleDefaultWorkingDirectoryChangeAsync(value).SafeFireAndForget(_logger);
    }

    /// <summary>
    /// Validates the typed/picked default working directory, creates the folder if needed, persists
    /// the normalized value, and refreshes the dropdown. On invalid input (rooted/escaping/blocked)
    /// reverts the field to the saved value and surfaces an error.
    /// </summary>
    private async Task HandleDefaultWorkingDirectoryChangeAsync(string? value)
    {
        var normalized = _workingDirectoryService.EnsureSubfolder(value);
        if (normalized is null)
        {
            var settings = await _settingsService.GetSettingsAsync();
            _isLoading = true;
            DefaultWorkingDirectory = settings.AssistantDefaultWorkingDirectory;
            _isLoading = false;
            await _dialogService.ShowMessageDialogAsync(
                _localizationService["Msg_Error"],
                _localizationService["Settings_Assistant_DefaultWorkingDirectory_Invalid"]);
            return;
        }

        if (!string.Equals(normalized, value, StringComparison.Ordinal))
        {
            _isLoading = true;
            DefaultWorkingDirectory = normalized;
            _isLoading = false;
        }

        await SaveSettingsAsync();
        RefreshAvailableWorkingDirectories();
    }

    private void RefreshAvailableWorkingDirectories()
    {
        var current = AvailableWorkingDirectories.ToList();
        // The vault is a valid file-tool target but must never be offered as a chat working
        // directory (EnsureSubfolder also rejects it on save) — filter it out of the picker.
        var next = _workingDirectoryService.ListSubfolders(string.Empty)
            .Where(name => !string.Equals(name, Pia.Infrastructure.AssistantWorkspace.VaultSubfolderName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (current.SequenceEqual(next)) return;

        AvailableWorkingDirectories.Clear();
        foreach (var name in next)
            AvailableWorkingDirectories.Add(name);
    }

    partial void OnChatHistoryEnabledChanged(bool value)
    {
        if (_isLoading) return;
        HandleChatHistoryToggleAsync(value).SafeFireAndForget(_logger);
    }

    partial void OnChatHistoryRetentionDaysChanged(int value)
    {
        if (_isLoading) return;
        var clamped = Math.Clamp(value, 1, 365);
        if (clamped != value)
        {
            ChatHistoryRetentionDays = clamped;
            return;
        }
        SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    partial void OnChatAutoTitleEnabledChanged(bool value)
    {
        if (!_isLoading) SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    // Agent-run budget knobs (Assistant → Agent runs). Clamped on change + persisted; built into a
    // RunProfile at run start (RunProfile.FromBudget). Defaults mirror RunProfile.Interactive.
    [ObservableProperty]
    private int _agentMaxSteps = 24;

    [ObservableProperty]
    private int _agentMaxReplans = 2;

    [ObservableProperty]
    private int _agentWallClockMinutes = 20;

    [ObservableProperty]
    private int _maxToolRoundsPerStep = 10;

    public string AgentMaxStepsDisplay => _localizationService.Format("Settings_Agent_MaxSteps_Value", AgentMaxSteps);
    public string AgentMaxReplansDisplay => _localizationService.Format("Settings_Agent_MaxReplans_Value", AgentMaxReplans);
    public string AgentWallClockDisplay => _localizationService.Format("Settings_Agent_WallClock_Value", AgentWallClockMinutes);
    public string MaxToolRoundsDisplay => _localizationService.Format("Settings_Agent_MaxToolRounds_Value", MaxToolRoundsPerStep);

    partial void OnAgentMaxStepsChanged(int value)
    {
        if (_isLoading) return;
        var clamped = Math.Clamp(value, RunProfile.MinSteps, RunProfile.MaxStepsCap);
        if (clamped != value) { AgentMaxSteps = clamped; return; }
        OnPropertyChanged(nameof(AgentMaxStepsDisplay));
        SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    partial void OnAgentMaxReplansChanged(int value)
    {
        if (_isLoading) return;
        var clamped = Math.Clamp(value, RunProfile.MinReplans, RunProfile.MaxReplansCap);
        if (clamped != value) { AgentMaxReplans = clamped; return; }
        OnPropertyChanged(nameof(AgentMaxReplansDisplay));
        SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    partial void OnAgentWallClockMinutesChanged(int value)
    {
        if (_isLoading) return;
        var clamped = Math.Clamp(value, RunProfile.MinWallClockMinutes, RunProfile.MaxWallClockMinutes);
        if (clamped != value) { AgentWallClockMinutes = clamped; return; }
        OnPropertyChanged(nameof(AgentWallClockDisplay));
        SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    partial void OnMaxToolRoundsPerStepChanged(int value)
    {
        if (_isLoading) return;
        var clamped = Math.Clamp(value, RunProfile.MinToolRounds, RunProfile.MaxToolRoundsCap);
        if (clamped != value) { MaxToolRoundsPerStep = clamped; return; }
        OnPropertyChanged(nameof(MaxToolRoundsDisplay));
        SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    // Scheduled/headless-run budget knobs (§17.5) — the caps an unattended run (a "Run in background"
    // detach or a scheduled AgentTask job) stops at. Separate envelope from the interactive Agent* knobs
    // (no user is watching). Clamped on the same RunProfile bounds + persisted. Defaults = RunProfile.Scheduled.
    [ObservableProperty]
    private int _scheduledMaxSteps = 24;

    [ObservableProperty]
    private int _scheduledMaxReplans = 2;

    [ObservableProperty]
    private int _scheduledWallClockMinutes = 45;

    public string ScheduledMaxStepsDisplay => _localizationService.Format("Settings_Scheduled_MaxSteps_Value", ScheduledMaxSteps);
    public string ScheduledMaxReplansDisplay => _localizationService.Format("Settings_Scheduled_MaxReplans_Value", ScheduledMaxReplans);
    public string ScheduledWallClockDisplay => _localizationService.Format("Settings_Scheduled_WallClock_Value", ScheduledWallClockMinutes);

    partial void OnScheduledMaxStepsChanged(int value)
    {
        if (_isLoading) return;
        var clamped = Math.Clamp(value, RunProfile.MinSteps, RunProfile.MaxStepsCap);
        if (clamped != value) { ScheduledMaxSteps = clamped; return; }
        OnPropertyChanged(nameof(ScheduledMaxStepsDisplay));
        SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    partial void OnScheduledMaxReplansChanged(int value)
    {
        if (_isLoading) return;
        var clamped = Math.Clamp(value, RunProfile.MinReplans, RunProfile.MaxReplansCap);
        if (clamped != value) { ScheduledMaxReplans = clamped; return; }
        OnPropertyChanged(nameof(ScheduledMaxReplansDisplay));
        SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    partial void OnScheduledWallClockMinutesChanged(int value)
    {
        if (_isLoading) return;
        var clamped = Math.Clamp(value, RunProfile.MinWallClockMinutes, RunProfile.MaxWallClockMinutes);
        if (clamped != value) { ScheduledWallClockMinutes = clamped; return; }
        OnPropertyChanged(nameof(ScheduledWallClockDisplay));
        SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    // T1-1: how many unattended runs execute AT ONCE. Sits with the Scheduled* knobs because it is the same
    // audience, but it is a WIDTH and not a budget — the three above bound what one run may spend, this one
    // bounds how many spend at all. Clamp bounds are AppSettings consts rather than RunProfile's: the pool is
    // not part of a run's envelope, and HeadlessRunLauncher clamps on the same pair.
    [ObservableProperty]
    private int _maxParallelBackgroundRuns = AppSettings.DefaultParallelBackgroundRuns;

    public string MaxParallelBackgroundRunsDisplay =>
        _localizationService.Format("Settings_Scheduled_ParallelRuns_Value", MaxParallelBackgroundRuns);

    partial void OnMaxParallelBackgroundRunsChanged(int value)
    {
        if (_isLoading) return;
        var clamped = Math.Clamp(value, AppSettings.MinParallelBackgroundRuns, AppSettings.MaxParallelBackgroundRunsCap);
        if (clamped != value) { MaxParallelBackgroundRuns = clamped; return; }
        OnPropertyChanged(nameof(MaxParallelBackgroundRunsDisplay));
        // The save is what makes the new width LIVE: HeadlessRunLauncher resizes its pool from
        // ISettingsService.SettingsChanged, which SaveSettingsAsync raises. No restart, and no other wiring.
        SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    // Reason-then-emit opt-in: split a plan turn into a tool-free reasoning turn plus the constrained
    // emit_plan turn on providers that drop the configured reasoning effort once tools are attached.
    // Global (not per-provider, and it covers unattended runs too), default OFF — it doubles the plan-turn
    // cost. No …Display property: a CheckBox's label is the resx string, there is no numeric readout.
    [ObservableProperty]
    private bool _agentPlanReasoningTurnEnabled;

    partial void OnAgentPlanReasoningTurnEnabledChanged(bool value)
    {
        if (!_isLoading) SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    // Batch 04 autonomy default: when on, an agent run carries a policy that auto-approves Pia's OWN write
    // tools BY CLASS (memory / todo / reminder / scheduling / files) so the run does not stop at a card for
    // every write. Never covers a delete-like tool, never Git, never an external (MCP) tool. Global and
    // default OFF — with it on, an unattended run can overwrite files with nobody watching.
    [ObservableProperty]
    private bool _agentRunAutoApproveBuiltInWrites;

    partial void OnAgentRunAutoApproveBuiltInWritesChanged(bool value)
    {
        if (!_isLoading) SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    // Batch 07 D1/D7 — "step specialists": one row per known persona, checked against the roster
    // configured for the current UserOperatingMode. THE ROSTER IS THE OPT-IN (D1): with nothing checked
    // here the plan prompt stays byte-identical to the pre-07 one. No mode picker in this surface —
    // it edits `settings.UserOperatingMode ?? Personal`, the same expression every resolution site uses (R25).
    public ObservableCollection<AgentRosterOptionViewModel> AgentRosterOptions { get; } = [];

    /// <summary>Drives <c>Settings_Agent_Roster_Empty</c>: true when no row is currently checked (not when
    /// the roster of KNOWN personas is empty — those are different questions).</summary>
    [ObservableProperty]
    private bool _hasSelectedRoster;

    private async Task LoadAgentRosterOptionsAsync(AppSettings settings)
    {
        AgentRosterOptions.Clear();
        if (_personaService is null)
        {
            HasSelectedRoster = false;
            _rosterLoaded = true; // intentional decision, nothing to protect — safe to gate saves on
            return;
        }

        var mode = settings.UserOperatingMode ?? UserOperatingMode.Personal;
        var roster = settings.GetAgentPersonaRoster(mode).ToHashSet();

        IReadOnlyList<Persona> personas;
        try
        {
            personas = await _personaService.GetPersonasAsync();
        }
        catch (Exception ex)
        {
            // An attribution/roster read must never break settings load — leave the surface empty AND
            // leave _rosterLoaded false, so a later unrelated save does not persist this empty surface
            // over a roster that is still configured in AppSettings.
            _logger.LogWarning(ex, "Could not load personas for the agent roster surface");
            HasSelectedRoster = false;
            return;
        }

        foreach (var persona in personas)
        {
            AgentRosterOptions.Add(new AgentRosterOptionViewModel(
                persona.Id, persona.Name, persona.Emoji, persona.AccentColor,
                roster.Contains(persona.Id), OnRosterOptionToggled));
        }

        HasSelectedRoster = AgentRosterOptions.Any(o => o.IsSelected);
        _rosterLoaded = true;
    }

    /// <summary>
    /// The parent enforces the roster cap here rather than in the row: <see cref="AppSettings.MaxAgentPersonaRoster"/>
    /// is a cross-row invariant. A 7th selection is refused SILENTLY-BUT-VISIBLY — the checkbox does not
    /// stick — mirroring <see cref="OnAgentMaxStepsChanged"/>'s clamp-and-return, not an error dialog.
    /// </summary>
    private void OnRosterOptionToggled(AgentRosterOptionViewModel option, bool isSelected)
    {
        if (_isLoading || _isSuppressingRosterToggle) return;

        if (isSelected && AgentRosterOptions.Count(o => o.IsSelected) > AppSettings.MaxAgentPersonaRoster)
        {
            // Revert without saving. Suppressed so the revert itself does not re-enter this handler.
            _isSuppressingRosterToggle = true;
            option.IsSelected = false;
            _isSuppressingRosterToggle = false;
            return;
        }

        HasSelectedRoster = AgentRosterOptions.Any(o => o.IsSelected);
        SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    public async Task InitializeAsync()
    {
        _isLoading = true;

        var settings = await _settingsService.GetSettingsAsync();
        DefaultWindowMode = settings.DefaultWindowMode;
        SuggestionsEnabled = settings.AssistantSuggestionsEnabled;
        FilesFolder = settings.AssistantFilesFolder; // OnFilesFolderChanged sets VaultLocationDisplay
        FileToolsEnabled = settings.AssistantFileToolsEnabled;
        GitToolsEnabled = settings.AssistantGitToolsEnabled;
        GitToolsAvailable = GitLocator.IsAvailable; // decided at startup (prewarmed), cached

        DefaultWorkingDirectory = settings.AssistantDefaultWorkingDirectory;
        RefreshAvailableWorkingDirectories();
        ChatHistoryEnabled = settings.ChatHistoryEnabled;
        ChatHistoryRetentionDays = Math.Clamp(settings.ChatHistoryRetentionDays, 1, 365);
        ChatAutoTitleEnabled = settings.ChatAutoTitleEnabled;

        AgentMaxSteps = Math.Clamp(settings.AgentMaxSteps, RunProfile.MinSteps, RunProfile.MaxStepsCap);
        AgentMaxReplans = Math.Clamp(settings.AgentMaxReplans, RunProfile.MinReplans, RunProfile.MaxReplansCap);
        AgentWallClockMinutes = Math.Clamp(settings.AgentWallClockMinutes, RunProfile.MinWallClockMinutes, RunProfile.MaxWallClockMinutes);
        MaxToolRoundsPerStep = Math.Clamp(settings.MaxToolRoundsPerStep, RunProfile.MinToolRounds, RunProfile.MaxToolRoundsCap);
        AgentPlanReasoningTurnEnabled = settings.AgentPlanReasoningTurnEnabled;
        AgentRunAutoApproveBuiltInWrites = settings.AgentRunAutoApproveBuiltInWrites;

        ScheduledMaxSteps = Math.Clamp(settings.ScheduledMaxSteps, RunProfile.MinSteps, RunProfile.MaxStepsCap);
        ScheduledMaxReplans = Math.Clamp(settings.ScheduledMaxReplans, RunProfile.MinReplans, RunProfile.MaxReplansCap);
        ScheduledWallClockMinutes = Math.Clamp(settings.ScheduledWallClockMinutes, RunProfile.MinWallClockMinutes, RunProfile.MaxWallClockMinutes);
        // Through the accessor, not the raw property: it is the same clamp the launcher's pool applies, so the
        // slider can never show a width the pool is not actually running at.
        MaxParallelBackgroundRuns = settings.GetMaxParallelBackgroundRuns();

        await MeetingVm.InitializeAsync();
        await LoadAgentRosterOptionsAsync(settings);

        _isLoading = false;

        // Displays are computed; refresh them once now that the values are loaded (OnXChanged skips
        // while _isLoading). Subsequent slider edits raise them individually.
        OnPropertyChanged(nameof(AgentMaxStepsDisplay));
        OnPropertyChanged(nameof(AgentMaxReplansDisplay));
        OnPropertyChanged(nameof(AgentWallClockDisplay));
        OnPropertyChanged(nameof(MaxToolRoundsDisplay));
        OnPropertyChanged(nameof(ScheduledMaxStepsDisplay));
        OnPropertyChanged(nameof(ScheduledMaxReplansDisplay));
        OnPropertyChanged(nameof(ScheduledWallClockDisplay));
        // T1-1's readout belongs in this block for the same reason the six above do: the Slider is bound to the
        // [ObservableProperty], which raises on load, but the TextBlock beneath it reads a computed string that
        // nothing raises while _isLoading. Omitting it left the two disagreeing until the user dragged the slider.
        OnPropertyChanged(nameof(MaxParallelBackgroundRunsDisplay));
    }

    [RelayCommand]
    private async Task ChangeFilesFolderAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = _localizationService["Settings_AssistantFilesFolder"],
            InitialDirectory = !string.IsNullOrWhiteSpace(FilesFolder) && System.IO.Directory.Exists(FilesFolder)
                ? FilesFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (dialog.ShowDialog() != true) return;

        var target = dialog.FolderName;

        var validation = _relocationService.Validate(target);
        if (validation != RelocationOutcome.Success)
        {
            await _dialogService.ShowMessageDialogAsync(
                _localizationService["Msg_Error"], MapOutcomeMessage(validation));
            return;
        }

        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            _localizationService["Settings_AssistantFilesFolder_Change"],
            _localizationService.Format("Settings_FolderMove_Confirm", target));
        if (!confirmed) return;

        var progress = new Progress<FolderMoveProgress>();
        RelocationResult? result = null;
        await _dialogService.ShowFolderMoveDialogAsync(progress, async () =>
            result = await _relocationService.MoveAsync(target, progress, CancellationToken.None));

        if (result is { Outcome: RelocationOutcome.Success or RelocationOutcome.NoChange })
        {
            // Update the display (persistence is owned by the relocation move).
            _isLoading = true;
            FilesFolder = target;
            _isLoading = false;
        }
        else
        {
            await _dialogService.ShowMessageDialogAsync(
                _localizationService["Msg_Error"],
                result is null
                    ? _localizationService["Settings_FolderMove_Failed"]
                    : MapOutcomeMessage(result.Outcome));
        }
    }

    private string MapOutcomeMessage(RelocationOutcome outcome) => outcome switch
    {
        RelocationOutcome.OutsideUserProfile => _localizationService["Settings_FolderMove_OutsideProfile"],
        RelocationOutcome.BlockedPath => _localizationService["Settings_FolderMove_Blocked"],
        RelocationOutcome.NestedInCurrent => _localizationService["Settings_FolderMove_Nested"],
        RelocationOutcome.NotEmpty => _localizationService["Settings_FolderMove_NotEmpty"],
        _ => _localizationService["Settings_FolderMove_Failed"],
    };

    [RelayCommand]
    private async Task DeleteAllChatHistoryAsync()
    {
        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            _localizationService["AssistantHistory_DeleteAllConfirmTitle"],
            _localizationService["AssistantHistory_DeleteAllConfirmBody"]);
        if (!confirmed) return;

        try
        {
            var deleted = await _chatService.DeleteAllAsync();
            _logger.LogInformation("Deleted all assistant chats ({Count} chats)", deleted.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete all assistant chats");
            await _dialogService.ShowMessageDialogAsync(
                _localizationService["Msg_Error"],
                ex.Message);
        }
    }

    private async Task HandleChatHistoryToggleAsync(bool enabled)
    {
        if (!enabled)
        {
            var confirmed = await _dialogService.ShowConfirmationDialogAsync(
                _localizationService["Settings_Chat_DisableConfirmTitle"],
                _localizationService["Settings_Chat_DisableConfirmBody"]);
            if (!confirmed)
            {
                _isLoading = true;
                ChatHistoryEnabled = true;
                _isLoading = false;
                return;
            }

            try
            {
                var deleted = await _chatService.DeleteAllAsync();
                _logger.LogInformation("Cleared assistant chats after disabling history ({Count} chats)", deleted.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear assistant chats on history disable");
            }
        }

        await SaveSettingsAsync();
    }

    private async Task SaveSettingsAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        settings.DefaultWindowMode = DefaultWindowMode;
        settings.AssistantSuggestionsEnabled = SuggestionsEnabled;
        // The folder is owned by the relocation move; never clear it here (the vault lives under it).
        if (!string.IsNullOrWhiteSpace(FilesFolder))
            settings.AssistantFilesFolder = FilesFolder;
        settings.AssistantFileToolsEnabled = FileToolsEnabled;
        settings.AssistantGitToolsEnabled = GitToolsEnabled;
        if (DefaultWorkingDirectory is not null)
            settings.AssistantDefaultWorkingDirectory = DefaultWorkingDirectory;
        settings.ChatHistoryEnabled = ChatHistoryEnabled;
        settings.ChatHistoryRetentionDays = ChatHistoryRetentionDays;
        settings.ChatAutoTitleEnabled = ChatAutoTitleEnabled;
        settings.AgentMaxSteps = AgentMaxSteps;
        settings.AgentMaxReplans = AgentMaxReplans;
        settings.AgentWallClockMinutes = AgentWallClockMinutes;
        settings.MaxToolRoundsPerStep = MaxToolRoundsPerStep;
        settings.AgentPlanReasoningTurnEnabled = AgentPlanReasoningTurnEnabled;
        settings.AgentRunAutoApproveBuiltInWrites = AgentRunAutoApproveBuiltInWrites;
        settings.ScheduledMaxSteps = ScheduledMaxSteps;
        settings.ScheduledMaxReplans = ScheduledMaxReplans;
        settings.ScheduledWallClockMinutes = ScheduledWallClockMinutes;
        settings.MaxParallelBackgroundRuns = MaxParallelBackgroundRuns;
        // Batch 07: gated on _rosterLoaded, NOT "_personaService is not null" — the latter is true in the
        // one case this guards against: LoadAgentRosterOptionsAsync's GetPersonasAsync fault arm leaves
        // AgentRosterOptions empty but _personaService non-null, and a later unrelated save (e.g. a
        // slider drag elsewhere on this tab) must not persist that empty surface over a roster that is
        // still configured in AppSettings.
        if (_rosterLoaded)
        {
            var mode = settings.UserOperatingMode ?? UserOperatingMode.Personal;
            settings.SetAgentPersonaRoster(mode, AgentRosterOptions.Where(o => o.IsSelected).Select(o => o.Id).ToList());
        }
        await _settingsService.SaveSettingsAsync(settings);
    }
}

/// <summary>
/// One row of the Batch 07 "step specialists" roster surface (§4.2). <see cref="Id"/>/<see cref="Name"/>/
/// <see cref="Emoji"/>/<see cref="AccentColor"/> mirror the persona; only <see cref="IsSelected"/> is
/// interactive. The cap (<see cref="AppSettings.MaxAgentPersonaRoster"/>) is enforced by the PARENT, not
/// here — it is a cross-row invariant a single row cannot see.
/// </summary>
public sealed partial class AgentRosterOptionViewModel : ObservableObject
{
    private readonly Action<AgentRosterOptionViewModel, bool> _onToggled;

    public Guid Id { get; }
    public string Name { get; }
    public string? Emoji { get; }
    public string? AccentColor { get; }

    [ObservableProperty]
    private bool _isSelected;

    public AgentRosterOptionViewModel(Guid id, string name, string? emoji, string? accentColor,
        bool isSelected, Action<AgentRosterOptionViewModel, bool> onToggled)
    {
        Id = id;
        Name = name;
        Emoji = emoji;
        AccentColor = accentColor;
        _isSelected = isSelected;
        _onToggled = onToggled;
    }

    partial void OnIsSelectedChanged(bool value) => _onToggled(this, value);
}
