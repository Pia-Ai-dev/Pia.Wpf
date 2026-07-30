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
    private bool _isLoading;

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
        IWorkingDirectoryService workingDirectoryService)
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

    public string AgentMaxStepsDisplay => _localizationService.Format("Settings_Agent_MaxSteps_Value", AgentMaxSteps);
    public string AgentMaxReplansDisplay => _localizationService.Format("Settings_Agent_MaxReplans_Value", AgentMaxReplans);
    public string AgentWallClockDisplay => _localizationService.Format("Settings_Agent_WallClock_Value", AgentWallClockMinutes);

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
        AgentPlanReasoningTurnEnabled = settings.AgentPlanReasoningTurnEnabled;
        AgentRunAutoApproveBuiltInWrites = settings.AgentRunAutoApproveBuiltInWrites;

        ScheduledMaxSteps = Math.Clamp(settings.ScheduledMaxSteps, RunProfile.MinSteps, RunProfile.MaxStepsCap);
        ScheduledMaxReplans = Math.Clamp(settings.ScheduledMaxReplans, RunProfile.MinReplans, RunProfile.MaxReplansCap);
        ScheduledWallClockMinutes = Math.Clamp(settings.ScheduledWallClockMinutes, RunProfile.MinWallClockMinutes, RunProfile.MaxWallClockMinutes);

        await MeetingVm.InitializeAsync();

        _isLoading = false;

        // Displays are computed; refresh them once now that the values are loaded (OnXChanged skips
        // while _isLoading). Subsequent slider edits raise them individually.
        OnPropertyChanged(nameof(AgentMaxStepsDisplay));
        OnPropertyChanged(nameof(AgentMaxReplansDisplay));
        OnPropertyChanged(nameof(AgentWallClockDisplay));
        OnPropertyChanged(nameof(ScheduledMaxStepsDisplay));
        OnPropertyChanged(nameof(ScheduledMaxReplansDisplay));
        OnPropertyChanged(nameof(ScheduledWallClockDisplay));
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
        settings.AgentPlanReasoningTurnEnabled = AgentPlanReasoningTurnEnabled;
        settings.AgentRunAutoApproveBuiltInWrites = AgentRunAutoApproveBuiltInWrites;
        settings.ScheduledMaxSteps = ScheduledMaxSteps;
        settings.ScheduledMaxReplans = ScheduledMaxReplans;
        settings.ScheduledWallClockMinutes = ScheduledWallClockMinutes;
        await _settingsService.SaveSettingsAsync(settings);
    }
}
