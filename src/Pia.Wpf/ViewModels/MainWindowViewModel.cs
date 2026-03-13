using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private WindowMode _mode;

    [ObservableProperty]
    private AppTheme _theme = AppTheme.System;

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

    public string WindowTitle => $"Pia - {Mode} (v{AppVersion})";

    public static string AppVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "unknown";

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
        Pia.Services.Interfaces.IAuthService authService)
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

        NavigationCommand = new RelayCommand<string>(ExecuteNavigationCommand);
        ToggleThemeCommand = new AsyncRelayCommand(ExecuteToggleThemeAsync);
        OpenDefaultWindowCommand = new AsyncRelayCommand(ExecuteOpenDefaultWindowAsync);
        OpenNewWindowCommand = new RelayCommand<WindowMode>(ExecuteOpenNewWindow);

        _navigationService.ViewModelChanged += OnViewModelChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;
        _providerService.ProvidersChanged += OnProvidersChanged;
        _authService.LoginStateChanged += OnLoginStateChanged;

        // Poll for update readiness (background download is fire-and-forget)
        var updateTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        updateTimer.Tick += (_, _) =>
        {
            if (_updateService.IsUpdateReady && !IsUpdateReady)
            {
                IsUpdateReady = true;
                UpdateVersion = _updateService.AvailableVersion;
            }
            if (IsUpdateReady)
                updateTimer.Stop();
        };
        updateTimer.Start();
    }

    public async Task InitializeAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();

        Theme = settings.Theme;
        _themeService.ApplyTheme(Theme);

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

            // Provider defaults may have changed — re-check setup state
            _ = RefreshSetupRequiredAsync();
        }, null);
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
            using var scope = Bootstrapper.ServiceProvider.CreateScope();
            var wizard = scope.ServiceProvider.GetRequiredService<Views.FirstRunWizardWindow>();
            wizard.ShowDialog();

            // Refresh setup state after wizard closes
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
                WindowMode.Research => "Research",
                _ => null
            },
            "Shortcut2" => Mode switch
            {
                WindowMode.Optimize => "History",
                WindowMode.Assistant => "Memory",
                WindowMode.Research => "Settings",
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
            case "Research":
                _navigationService.NavigateTo<ResearchViewModel>();
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

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _navigationService.ViewModelChanged -= OnViewModelChanged;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _providerService.ProvidersChanged -= OnProvidersChanged;
        _authService.LoginStateChanged -= OnLoginStateChanged;

        GC.SuppressFinalize(this);
    }
}
