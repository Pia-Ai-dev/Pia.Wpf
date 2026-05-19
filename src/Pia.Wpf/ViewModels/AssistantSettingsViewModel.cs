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
    private bool _isLoading;

    public ProvidersSettingsViewModel ProvidersVm { get; }

    public AssistantSettingsViewModel(
        ProvidersSettingsViewModel providersVm,
        ILogger<SettingsViewModel> logger,
        ISettingsService settingsService,
        IAssistantChatService chatService,
        IDialogService dialogService,
        ILocalizationService localizationService)
    {
        ProvidersVm = providersVm;
        _logger = logger;
        _settingsService = settingsService;
        _chatService = chatService;
        _dialogService = dialogService;
        _localizationService = localizationService;
        _localizationService.LanguageChanged += (_, _) => OnPropertyChanged(nameof(RetentionDaysDisplay));
    }

    [ObservableProperty]
    private WindowMode _defaultWindowMode;

    [ObservableProperty]
    private bool _showTodoPanelButton = true;

    [ObservableProperty]
    private bool _suggestionsEnabled;

    [ObservableProperty]
    private string? _filesFolder;

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
        FilesFolder = settings.AssistantFilesFolder;
        ChatHistoryEnabled = settings.ChatHistoryEnabled;
        ChatHistoryRetentionDays = Math.Clamp(settings.ChatHistoryRetentionDays, 1, 365);
        ChatAutoTitleEnabled = settings.ChatAutoTitleEnabled;

        _isLoading = false;
    }

    [RelayCommand]
    private void BrowseFilesFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select assistant files folder",
            InitialDirectory = !string.IsNullOrWhiteSpace(FilesFolder) && System.IO.Directory.Exists(FilesFolder)
                ? FilesFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog() == true)
            FilesFolder = dialog.FolderName;
    }

    [RelayCommand]
    private void ClearFilesFolder() => FilesFolder = null;

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
            _logger.LogInformation("Deleted all assistant chats ({Count} chats)", deleted);
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
                _logger.LogInformation("Cleared assistant chats after disabling history ({Count} chats)", deleted);
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
        settings.AssistantFilesFolder = string.IsNullOrWhiteSpace(FilesFolder) ? null : FilesFolder;
        settings.ChatHistoryEnabled = ChatHistoryEnabled;
        settings.ChatHistoryRetentionDays = ChatHistoryRetentionDays;
        settings.ChatAutoTitleEnabled = ChatAutoTitleEnabled;
        await _settingsService.SaveSettingsAsync(settings);
    }
}
