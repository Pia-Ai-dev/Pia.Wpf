using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Pia.Models;
using System.Reflection;

namespace Pia.ViewModels;

public partial class MainWindowViewModel : UiThreadViewModel, IDisposable
{
    private readonly ILogger<MainWindowViewModel> _logger;
    private bool _disposed;
    private readonly Navigation.INavigationService _navigationService;
    private readonly Services.Interfaces.ISettingsService _settingsService;
    private readonly Services.Interfaces.IThemeService _themeService;
    private readonly Services.Interfaces.IWindowManagerService _windowManagerService;
    private readonly Services.Interfaces.IUpdateService _updateService;
    private readonly Services.Interfaces.IProviderService _providerService;
    private readonly Services.Interfaces.IAuthService _authService;
    private readonly Services.Interfaces.ISyncClientService _syncClientService;
    private readonly Services.Operators.IAssignmentApiClient _assignmentApiClient;
    private Timer? _updateTimer;
    private bool _assignmentSurfaceAvailable;

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

    [ObservableProperty]
    private bool _isAssignmentsNavVisible;

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
        Pia.Services.Interfaces.ISyncClientService syncClientService,
        Pia.Services.Operators.IAssignmentApiClient assignmentApiClient)
        : base(requireUiThread: true)
    {
        _logger = logger;
        _navigationService = navigationService;
        _settingsService = settingsService;
        _themeService = themeService;
        _windowManagerService = windowManagerService;
        _updateService = updateService;
        _providerService = providerService;
        _authService = authService;
        _syncClientService = syncClientService;
        _assignmentApiClient = assignmentApiClient;
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
            Post(() =>
            {
                if (_updateService.IsUpdateReady && !IsUpdateReady)
                {
                    IsUpdateReady = true;
                    UpdateVersion = _updateService.AvailableVersion;
                }
                if (IsUpdateReady)
                    _updateTimer?.Dispose();
            });
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>The probe is never awaited here — a slow or absent server must not hold up the first
    /// navigation — so tests await this instead.</summary>
    internal Task PendingAssignmentSurfaceProbe { get; private set; } = Task.CompletedTask;

    public async Task InitializeAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();

        Theme = settings.Theme;
        _themeService.ApplyTheme(Theme);

        UpdateHotkeyHints(settings);

        // Started before the pre-navigated early return below, which a chat window opened for a finished
        // assignment takes.
        PendingAssignmentSurfaceProbe = RefreshAssignmentSurfaceAsync();

        await RefreshSetupRequiredAsync();

        // If the caller pre-navigated (e.g. ShowAssistantChat) before Loaded fired,
        // don't clobber their selection with the mode default.
        if (_navigationService.CurrentViewModel is not null)
            return;

        if (Mode == WindowMode.Assistant)
            _navigationService.NavigateTo<AssistantViewModel>();
        else
            _navigationService.NavigateTo<OptimizeViewModel>();
    }

    private async Task RefreshAssignmentSurfaceAsync()
    {
        try
        {
            _assignmentSurfaceAvailable = (await _assignmentApiClient.GetSurfaceAsync()).Available;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Could not read the background-assignment surface; keeping it hidden");
            _assignmentSurfaceAvailable = false;
        }

        await PostAsync(RefreshAssignmentsNavVisible);
    }

    private void RefreshAssignmentsNavVisible() =>
        IsAssignmentsNavVisible = _assignmentSurfaceAvailable && Mode == WindowMode.Assistant;

    partial void OnModeChanged(WindowMode value) => RefreshAssignmentsNavVisible();

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
        Post(() =>
        {
            _ = RefreshSetupRequiredAsync();
        });
    }

    private void OnLoginStateChanged(object? sender, bool isLoggedIn)
    {
        // Signing in is what turns the surface on, and the entry is otherwise probed only at startup.
        PendingAssignmentSurfaceProbe = RefreshAssignmentSurfaceAsync();

        Post(() =>
        {
            _ = RefreshSetupRequiredAsync();
        });
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        Post(() =>
        {
            if (settings.Theme != Theme)
            {
                Theme = settings.Theme;
                _themeService.ApplyTheme(Theme);
            }

            UpdateHotkeyHints(settings);

            // Provider defaults may have changed — re-check setup state
            _ = RefreshSetupRequiredAsync();
        });
    }

    private void UpdateHotkeyHints(AppSettings settings)
    {
        OptimizeHotkeyHint = settings.OptimizeHotkey.DisplayText;
        AssistantHotkeyHint = settings.AssistantHotkey?.DisplayText ?? string.Empty;
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
                or AssistantViewModel;
        }
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        // Navigate to Settings with the Providers tab selected.
        _navigationService.NavigateTo<SettingsViewModel, int>((int)SettingsTab.Providers);
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
        // Map keyboard shortcut indices to mode-specific destinations
        var resolved = destination switch
        {
            "Shortcut1" => Mode switch
            {
                WindowMode.Optimize => "Optimize",
                WindowMode.Assistant => "Assistant",
                _ => null
            },
            "Shortcut2" => Mode switch
            {
                WindowMode.Optimize => "History",
                WindowMode.Assistant => "Memory",
                _ => null
            },
            "Shortcut3" => Mode switch
            {
                WindowMode.Optimize => "Settings",
                WindowMode.Assistant => "Reminders",
                _ => null
            },
            "Shortcut4" => Mode switch
            {
                WindowMode.Assistant => "Settings",
                _ => null
            },
            _ => destination
        };

        switch (resolved)
        {
            case "Optimize":
                _navigationService.NavigateTo<OptimizeViewModel>();
                break;
            case "History":
                _navigationService.NavigateTo<HistoryViewModel>();
                break;
            case "Settings":
                _navigationService.NavigateTo<SettingsViewModel>();
                break;
            case "Assistant":
                _navigationService.NavigateTo<AssistantViewModel>();
                break;
            case "AssistantHistory":
                _navigationService.NavigateTo<AssistantHistoryViewModel>();
                break;
            case "Memory":
                _navigationService.NavigateTo<MemoryViewModel>();
                break;
            case "Reminders":
                _navigationService.NavigateTo<RemindersViewModel>();
                break;
            case "Routines":
                _navigationService.NavigateTo<RoutinesViewModel>();
                break;
            case "Assignments":
                _navigationService.NavigateTo<AssignmentsViewModel>();
                break;
            case "Todo":
                _navigationService.NavigateTo<TodoViewModel>();
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
        Post(() => IsE2EEOnboardingRequired = true);
    }

    private void OnE2EEOnboardingCleared(object? sender, EventArgs e)
    {
        Post(() => IsE2EEOnboardingRequired = false);
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
        _navigationService.NavigateTo<SettingsViewModel, int>((int)SettingsTab.Account);
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
