using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Pia.Models;
using System.Reflection;

namespace Pia.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly SynchronizationContext _syncContext;
    private bool _disposed;
    private readonly Navigation.INavigationService _navigationService;
    private readonly Services.Interfaces.ISettingsService _settingsService;
    private readonly Services.Interfaces.IThemeService _themeService;
    private readonly Services.Interfaces.IWindowManagerService _windowManagerService;
    private readonly Services.Interfaces.IUpdateService _updateService;
    private readonly Services.Interfaces.IProviderService _providerService;
    private readonly Services.Interfaces.IAuthService _authService;
    private readonly Services.Interfaces.ISyncClientService _syncClientService;
    private Timer? _updateTimer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private WindowMode _mode;

    [ObservableProperty]
    private AppTheme _theme = AppTheme.System;

    [ObservableProperty]
    private string _optimizeHotkeyHint = string.Empty;

    [ObservableProperty]
    private string _assistantHotkeyHint = string.Empty;

    [ObservableProperty]
    private string _researchHotkeyHint = string.Empty;

    [ObservableProperty]
    private ObservableObject? _currentView;

    [ObservableProperty]
    private string _currentNavigationItem = string.Empty;

    [ObservableProperty]
    private bool _isUpdateReady;

    [ObservableProperty]
    private string? _updateVersion;

    [ObservableProperty]
    private bool _isUpdateBarDismissed;

    [ObservableProperty]
    private bool _isE2EEOnboardingRequired;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSetupOverlay))]
    private bool _isSetupRequired;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSetupOverlay))]
    private bool _isOnFeatureView;

    /// <summary>
    /// Show the setup overlay when no usable AI provider is configured
    /// and the user is viewing a feature page (Optimize, Assistant, Research).
    /// </summary>
    public bool ShowSetupOverlay => IsSetupRequired && IsOnFeatureView;

    public bool ShowUpdateBar => IsUpdateReady && !IsUpdateBarDismissed;

    public bool ShowE2EEOnboardingBar => IsE2EEOnboardingRequired;

    public string WindowTitle => $"Pia - {Mode} (v{AppVersion})";

    public string AppVersion { get; }

    public IRelayCommand<string> NavigationCommand { get; }
    public IRelayCommand ToggleThemeCommand { get; }
    public IRelayCommand OpenDefaultWindowCommand { get; }
    public IRelayCommand<WindowMode> OpenNewWindowCommand { get; }

    public MainWindowViewModel(
        ILogger<MainWindowViewModel> logger,
        Pia.Navigation.INavigationService navigationService,
        Pia.Services.Interfaces.ISettingsService settingsService,
        Pia.Services.Interfaces.IThemeService themeService,
        Pia.Services.Interfaces.IWindowManagerService windowManagerService,
        Pia.Services.Interfaces.IUpdateService updateService,
        Pia.Services.Interfaces.IProviderService providerService,
        Pia.Services.Interfaces.IAuthService authService,
        Pia.Services.Interfaces.ISyncClientService syncClientService)
    {
        _logger = logger;
        _syncContext = SynchronizationContext.Current ?? throw new InvalidOperationException("Must be created on UI thread");
        _navigationService = navigationService;
        _settingsService = settingsService;
        _themeService = themeService;
        _windowManagerService = windowManagerService;
        _updateService = updateService;
        _providerService = providerService;
        _authService = authService;
        _syncClientService = syncClientService;
        IsE2EEOnboardingRequired = _syncClientService.IsE2EEOnboardingRequired;

        AppVersion = updateService.CurrentVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown";

        NavigationCommand = new RelayCommand<string>(ExecuteNavigationCommand);
        ToggleThemeCommand = new AsyncRelayCommand(ExecuteToggleThemeAsync);
        OpenDefaultWindowCommand = new AsyncRelayCommand(ExecuteOpenDefaultWindowAsync);
        OpenNewWindowCommand = new RelayCommand<WindowMode>(ExecuteOpenNewWindow);

        _navigationService.ViewModelChanged += OnViewModelChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;
        _providerService.ProvidersChanged += OnProvidersChanged;
        _authService.LoginStateChanged += OnLoginStateChanged;
        _syncClientService.E2EEOnboardingRequired += OnE2EEOnboardingRequired;
        _syncClientService.E2EEOnboardingCleared += OnE2EEOnboardingCleared;

        // Poll for update readiness (background download is fire-and-forget)
        _updateTimer = new Timer(_ =>
        {
            _syncContext.Post(_ =>
            {
                if (_updateService.IsUpdateReady && !IsUpdateReady)
                {
                    IsUpdateReady = true;
                    UpdateVersion = _updateService.AvailableVersion;
                }
                if (IsUpdateReady)
                    _updateTimer?.Dispose();
            }, null);
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public async Task InitializeAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();

        Theme = settings.Theme;
        _themeService.ApplyTheme(Theme);

        UpdateHotkeyHints(settings);

        await RefreshSetupRequiredAsync();

        if (Mode == WindowMode.Assistant)
            _navigationService.NavigateTo<AssistantViewModel>();
        else if (Mode == WindowMode.Research)
            _navigationService.NavigateTo<ResearchViewModel>();
        else
            _navigationService.NavigateTo<OptimizeViewModel>();
    }

    private async Task RefreshSetupRequiredAsync()
    {
        try
        {
            var providers = await _providerService.GetProvidersAsync();
            var hasNonCloudProvider = providers.Any(p => p.ProviderType != AiProviderType.PiaCloud);
            var isLoggedIn = _authService.IsLoggedIn;

            IsSetupRequired = !hasNonCloudProvider && !isLoggedIn;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check provider setup state");
        }
    }

    private void OnProvidersChanged(object? sender, EventArgs e)
    {
        _syncContext.Post(_ =>
        {
            _ = RefreshSetupRequiredAsync();
        }, null);
    }

    private void OnLoginStateChanged(object? sender, bool isLoggedIn)
    {
        _syncContext.Post(_ =>
        {
            _ = RefreshSetupRequiredAsync();
        }, null);
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        _syncContext.Post(_ =>
        {
            if (settings.Theme != Theme)
            {
                Theme = settings.Theme;
                _themeService.ApplyTheme(Theme);
            }

            UpdateHotkeyHints(settings);

            // Provider defaults may have changed — re-check setup state
            _ = RefreshSetupRequiredAsync();
        }, null);
    }

    private void UpdateHotkeyHints(AppSettings settings)
    {
        OptimizeHotkeyHint = settings.OptimizeHotkey.DisplayText;
        AssistantHotkeyHint = settings.AssistantHotkey?.DisplayText ?? string.Empty;
        ResearchHotkeyHint = settings.ResearchHotkey?.DisplayText ?? string.Empty;
    }

    private void OnViewModelChanged(ObservableObject? viewModel)
    {
        CurrentView = viewModel;

        if (viewModel is not null)
        {
            var typeName = viewModel.GetType().Name;
            CurrentNavigationItem = typeName.EndsWith("ViewModel", StringComparison.Ordinal)
                ? typeName[..^"ViewModel".Length]
                : typeName;

            IsOnFeatureView = viewModel is OptimizeViewModel
                or AssistantViewModel
                or ResearchViewModel;
        }
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        // Navigate to Settings with the Providers tab selected (index 0)
        _navigationService.NavigateTo<SettingsViewModel, int>(0);
    }

    [RelayCommand]
    private void OpenFirstRunWizard()
    {
        try
        {
            _windowManagerService.ShowFirstRunWizard();
            _ = RefreshSetupRequiredAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open first run wizard");
        }
    }

    private void ExecuteNavigationCommand(string? destination)
    {
        var resolved = destination switch
        {
            "Shortcut1" => "Optimize",
            "Shortcut2" => "Assistant",
            "Shortcut3" => "Research",
            "Shortcut4" => "Settings",
            _ => destination
        };

        switch (resolved)
        {
            case "Optimize":
                NavigateToPrimary(WindowMode.Optimize);
                break;
            case "Assistant":
                NavigateToPrimary(WindowMode.Assistant);
                break;
            case "Research":
                NavigateToPrimary(WindowMode.Research);
                break;
            case "History":
                _navigationService.NavigateTo<HistoryViewModel>();
                break;
            case "Settings":
                _navigationService.NavigateTo<SettingsViewModel>();
                break;
            case "Memory":
                _navigationService.NavigateTo<MemoryViewModel>();
                break;
            case "Reminders":
                _navigationService.NavigateTo<RemindersViewModel>();
                break;
            case "Todo":
                _navigationService.NavigateTo<TodoViewModel>();
                break;
        }
    }

    private void NavigateToPrimary(WindowMode target)
    {
        if (target != Mode && !_windowManagerService.TryChangeWindowMode(Mode, target))
            return;

        switch (target)
        {
            case WindowMode.Optimize:
                _navigationService.NavigateTo<OptimizeViewModel>();
                break;
            case WindowMode.Assistant:
                _navigationService.NavigateTo<AssistantViewModel>();
                break;
            case WindowMode.Research:
                _navigationService.NavigateTo<ResearchViewModel>();
                break;
        }
    }

    private async Task ExecuteToggleThemeAsync()
    {
        Theme = Theme switch
        {
            AppTheme.System => AppTheme.Dark,
            AppTheme.Dark => AppTheme.Light,
            AppTheme.Light => AppTheme.System,
            _ => AppTheme.System
        };

        _themeService.ApplyTheme(Theme);

        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            settings.Theme = Theme;
            await _settingsService.SaveSettingsAsync(settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save theme setting");
        }
    }

    private async Task ExecuteOpenDefaultWindowAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        _windowManagerService.ShowWindow(settings.DefaultWindowMode);
    }

    private void ExecuteOpenNewWindow(WindowMode mode)
    {
        _windowManagerService.ShowWindow(mode);
    }

    partial void OnIsUpdateReadyChanged(bool value) => OnPropertyChanged(nameof(ShowUpdateBar));
    partial void OnIsUpdateBarDismissedChanged(bool value) => OnPropertyChanged(nameof(ShowUpdateBar));
    partial void OnIsE2EEOnboardingRequiredChanged(bool value) => OnPropertyChanged(nameof(ShowE2EEOnboardingBar));

    private void OnE2EEOnboardingRequired(object? sender, EventArgs e)
    {
        _syncContext.Post(_ => IsE2EEOnboardingRequired = true, null);
    }

    private void OnE2EEOnboardingCleared(object? sender, EventArgs e)
    {
        _syncContext.Post(_ => IsE2EEOnboardingRequired = false, null);
    }

    [RelayCommand]
    private void RestartToUpdate()
    {
        _updateService.ApplyUpdateAndRestart();
    }

    [RelayCommand]
    private void DismissUpdateBar()
    {
        IsUpdateBarDismissed = true;
    }

    [RelayCommand]
    private void OpenE2EEOnboarding()
    {
        _navigationService.NavigateTo<SettingsViewModel, int>(0);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _updateTimer?.Dispose();
        _navigationService.ViewModelChanged -= OnViewModelChanged;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _providerService.ProvidersChanged -= OnProvidersChanged;
        _authService.LoginStateChanged -= OnLoginStateChanged;
        _syncClientService.E2EEOnboardingRequired -= OnE2EEOnboardingRequired;
        _syncClientService.E2EEOnboardingCleared -= OnE2EEOnboardingCleared;

        GC.SuppressFinalize(this);
    }
}
