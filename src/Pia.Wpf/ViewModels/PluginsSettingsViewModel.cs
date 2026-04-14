using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

public partial class PluginsSettingsViewModel : ObservableObject
{
    private readonly SettingsViewModel _parent;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly IPluginService _pluginService;
    private readonly IAuthService _authService;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogService;
    private readonly ILocalizationService _localizationService;
    private readonly Wpf.Ui.ISnackbarService _snackbarService;
    private readonly IHttpClientFactory _httpClientFactory;

    [ObservableProperty]
    private ObservableCollection<PluginItemViewModel> _plugins = [];

    [ObservableProperty]
    private bool _isCloudConnected;

    [ObservableProperty]
    private bool _isLoading;

    public PluginsSettingsViewModel(
        SettingsViewModel parent,
        ILogger<SettingsViewModel> logger,
        IPluginService pluginService,
        IAuthService authService,
        ISettingsService settingsService,
        IDialogService dialogService,
        ILocalizationService localizationService,
        Wpf.Ui.ISnackbarService snackbarService,
        IHttpClientFactory httpClientFactory)
    {
        _parent = parent;
        _logger = logger;
        _pluginService = pluginService;
        _authService = authService;
        _settingsService = settingsService;
        _dialogService = dialogService;
        _localizationService = localizationService;
        _snackbarService = snackbarService;
        _httpClientFactory = httpClientFactory;

        _isCloudConnected = _authService.IsLoggedIn;
        _authService.LoginStateChanged += OnLoginStateChanged;
        _pluginService.PluginsChanged += OnPluginsChanged;
    }

    private void OnPluginsChanged(object? sender, EventArgs e) => _ = LoadPluginsAsync();

    private void OnLoginStateChanged(object? sender, bool isLoggedIn)
    {
        IsCloudConnected = isLoggedIn;
        if (isLoggedIn)
            _ = LoadPluginsAsync();
    }

    public async Task InitializeAsync()
    {
        IsCloudConnected = _authService.IsLoggedIn;
        if (IsCloudConnected)
            await LoadPluginsAsync();
    }

    private async Task LoadPluginsAsync()
    {
        IsLoading = true;
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            var serverUrl = settings.ServerUrl;
            var configs = _pluginService.GetAllPluginConfigs();
            var items = configs
                .OrderByDescending(p => p.IsPreloaded)
                .ThenBy(p => p.Name)
                .Select(p => new PluginItemViewModel(p, serverUrl, _httpClientFactory, _authService))
                .ToList();

            App.Current.Dispatcher.Invoke(() =>
            {
                Plugins.Clear();
                foreach (var item in items)
                    Plugins.Add(item);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load plugin configs");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task TogglePluginAsync(PluginItemViewModel? plugin)
    {
        if (plugin is null) return;

        // Warn when disabling a built-in plugin
        if (plugin.IsPreloaded && !plugin.IsEnabled)
        {
            var confirmed = await _dialogService.ShowConfirmationDialogAsync(
                _localizationService["Plugins_DisableBuiltIn_Title"],
                _localizationService.Format("Plugins_DisableBuiltIn_Message", plugin.Name));

            if (!confirmed)
            {
                plugin.IsEnabled = true; // revert toggle
                return;
            }
        }

        plugin.IsActivating = true;
        plugin.UpdateStatus("Checking...");
        try
        {
            await _pluginService.SetPluginEnabledAsync(plugin.Id, plugin.IsEnabled);
            plugin.UpdateStatus(plugin.IsEnabled ? "Active" : "Inactive");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle plugin {Name}", plugin.Name);
            plugin.IsEnabled = !plugin.IsEnabled; // revert
            plugin.UpdateStatus("Error");
            _snackbarService.Show("Plugin Error",
                $"Failed to toggle {plugin.Name}: {ex.Message}",
                Wpf.Ui.Controls.ControlAppearance.Danger,
                null, TimeSpan.FromSeconds(5));
        }
        finally
        {
            plugin.IsActivating = false;
        }
    }

    [RelayCommand]
    private void GoToAccount()
    {
        _parent.SelectedTabIndex = 5;
    }
}
