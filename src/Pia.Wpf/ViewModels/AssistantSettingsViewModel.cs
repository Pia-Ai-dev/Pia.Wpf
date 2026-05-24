using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

public partial class AssistantSettingsViewModel : ObservableObject
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly ISettingsService _settingsService;
    private bool _isLoading;

    public ProvidersSettingsViewModel ProvidersVm { get; }

    public AssistantSettingsViewModel(
        ProvidersSettingsViewModel providersVm,
        ILogger<SettingsViewModel> logger,
        ISettingsService settingsService)
    {
        ProvidersVm = providersVm;
        _logger = logger;
        _settingsService = settingsService;
    }

    [ObservableProperty]
    private WindowMode _defaultWindowMode;

    [ObservableProperty]
    private bool _showTodoPanelButton = true;

    [ObservableProperty]
    private bool _suggestionsEnabled;

    [ObservableProperty]
    private string? _filesFolder;

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

    public async Task InitializeAsync()
    {
        _isLoading = true;

        var settings = await _settingsService.GetSettingsAsync();
        DefaultWindowMode = settings.DefaultWindowMode;
        ShowTodoPanelButton = settings.ShowTodoPanelButton;
        SuggestionsEnabled = settings.AssistantSuggestionsEnabled;
        FilesFolder = settings.AssistantFilesFolder;

        _isLoading = false;
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
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

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ClearFilesFolder() => FilesFolder = null;

    private async Task SaveSettingsAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        settings.DefaultWindowMode = DefaultWindowMode;
        settings.ShowTodoPanelButton = ShowTodoPanelButton;
        settings.AssistantSuggestionsEnabled = SuggestionsEnabled;
        settings.AssistantFilesFolder = string.IsNullOrWhiteSpace(FilesFolder) ? null : FilesFolder;
        await _settingsService.SaveSettingsAsync(settings);
    }

}
