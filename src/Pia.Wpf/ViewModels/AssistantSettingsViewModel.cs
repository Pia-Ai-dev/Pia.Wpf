using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models;
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
    private bool _isLoading;

    public ProvidersSettingsViewModel ProvidersVm { get; }
    public PersonaSettingsViewModel PersonasVm { get; }
    public ToolPermissionsSettingsViewModel ToolPermissionsVm { get; }

    /// <summary>Index of the inner tab pill (0 = General, 1 = Personas, 2 = Tool access).</summary>
    [ObservableProperty]
    private int _selectedInnerTabIndex;

    public AssistantSettingsViewModel(
        ProvidersSettingsViewModel providersVm,
        PersonaSettingsViewModel personasVm,
        ToolPermissionsSettingsViewModel toolPermissionsVm,
        ILogger<SettingsViewModel> logger,
        ISettingsService settingsService,
        IAssistantChatService chatService,
        IDialogService dialogService,
        ILocalizationService localizationService,
        IAssistantFolderRelocationService relocationService)
    {
        ProvidersVm = providersVm;
        PersonasVm = personasVm;
        ToolPermissionsVm = toolPermissionsVm;
        _logger = logger;
        _settingsService = settingsService;
        _chatService = chatService;
        _dialogService = dialogService;
        _localizationService = localizationService;
        _relocationService = relocationService;
        _localizationService.LanguageChanged += (_, _) => OnPropertyChanged(nameof(RetentionDaysDisplay));
    }

    [ObservableProperty]
    private WindowMode _defaultWindowMode;

    [ObservableProperty]
    private bool _showTodoPanelButton = true;

    [ObservableProperty]
    private bool _suggestionsEnabled;

    // Display-only: the current assistant files folder. Changed via the Change… command (which runs
    // the validated copy/verify/delete move), not by free-text editing.
    [ObservableProperty]
    private string? _filesFolder;

    [ObservableProperty]
    private bool _fileToolsEnabled = true;

    // "<folder>\Vault" — shown beneath the folder so the user sees where memory lives.
    [ObservableProperty]
    private string? _vaultLocationDisplay;

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

    partial void OnShowTodoPanelButtonChanged(bool value)
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

    public async Task InitializeAsync()
    {
        _isLoading = true;

        var settings = await _settingsService.GetSettingsAsync();
        DefaultWindowMode = settings.DefaultWindowMode;
        ShowTodoPanelButton = settings.ShowTodoPanelButton;
        SuggestionsEnabled = settings.AssistantSuggestionsEnabled;
        FilesFolder = settings.AssistantFilesFolder; // OnFilesFolderChanged sets VaultLocationDisplay
        FileToolsEnabled = settings.AssistantFileToolsEnabled;
        ChatHistoryEnabled = settings.ChatHistoryEnabled;
        ChatHistoryRetentionDays = Math.Clamp(settings.ChatHistoryRetentionDays, 1, 365);
        ChatAutoTitleEnabled = settings.ChatAutoTitleEnabled;

        _isLoading = false;
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
        settings.ShowTodoPanelButton = ShowTodoPanelButton;
        settings.AssistantSuggestionsEnabled = SuggestionsEnabled;
        // The folder is owned by the relocation move; never clear it here (the vault lives under it).
        if (!string.IsNullOrWhiteSpace(FilesFolder))
            settings.AssistantFilesFolder = FilesFolder;
        settings.AssistantFileToolsEnabled = FileToolsEnabled;
        settings.ChatHistoryEnabled = ChatHistoryEnabled;
        settings.ChatHistoryRetentionDays = ChatHistoryRetentionDays;
        settings.ChatAutoTitleEnabled = ChatAutoTitleEnabled;
        await _settingsService.SaveSettingsAsync(settings);
    }
}
