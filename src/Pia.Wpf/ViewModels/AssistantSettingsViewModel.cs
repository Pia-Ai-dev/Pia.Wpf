using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
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

    public IEnumerable<WindowMode> WindowModes => Enum.GetValues<WindowMode>();

    partial void OnDefaultWindowModeChanged(WindowMode value)
    {
        if (!_isLoading) SafeFireAndForget(SaveSettingsAsync());
    }

    partial void OnShowTodoPanelButtonChanged(bool value)
    {
        if (!_isLoading) SafeFireAndForget(SaveSettingsAsync());
    }

    public async Task InitializeAsync()
    {
        _isLoading = true;

        var settings = await _settingsService.GetSettingsAsync();
        DefaultWindowMode = settings.DefaultWindowMode;
        ShowTodoPanelButton = settings.ShowTodoPanelButton;

        _isLoading = false;
    }

    private async Task SaveSettingsAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        settings.DefaultWindowMode = DefaultWindowMode;
        settings.ShowTodoPanelButton = ShowTodoPanelButton;
        await _settingsService.SaveSettingsAsync(settings);
    }

    private async void SafeFireAndForget(Task task)
    {
        try { await task; }
        catch (Exception ex) { _logger.LogError(ex, "Background operation failed"); }
    }
}
