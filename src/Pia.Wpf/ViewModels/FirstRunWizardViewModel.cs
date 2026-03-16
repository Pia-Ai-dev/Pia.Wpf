using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

public partial class FirstRunWizardViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IMemoryService _memoryService;
    private readonly IVoiceInputService _voiceInputService;
    private readonly ILocalizationService _localizationService;
    private readonly IAuthService _authService;
    private readonly IProviderService _providerService;
    private readonly ISyncClientService _syncClientService;
    private readonly IDeviceManagementService _deviceManagement;
    private readonly ILogger<FirstRunWizardViewModel> _logger;

    public const int TotalSteps = 6;

    // --- Navigation ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFirstStep))]
    [NotifyPropertyChangedFor(nameof(IsLastStep))]
    [NotifyPropertyChangedFor(nameof(NextButtonText))]
    private int _currentStep;

    public bool IsFirstStep => CurrentStep == 0;
    public bool IsLastStep => CurrentStep == TotalSteps - 1;
    public string NextButtonText => IsLastStep ? "Get Started" : "Next";

    /// <summary>Visible step count: 5 when logged in (step 2 hidden), 6 otherwise.</summary>
    public int VisibleStepCount => IsLoggedIn ? 5 : 6;

    // --- Profile (existing) ---

    [ObservableProperty]
    private string _userName = string.Empty;

    [ObservableProperty]
    private string _nickname = string.Empty;

    [ObservableProperty]
    private string _location = string.Empty;

    [ObservableProperty]
    private UserOperatingMode _operatingMode = UserOperatingMode.Personal;

    [ObservableProperty]
    private bool _isCompleting;

    [ObservableProperty]
    private TargetLanguage _uiLanguage;

    public IEnumerable<TargetLanguage> UiLanguages => Enum.GetValues<TargetLanguage>();

    partial void OnIsE2EEOnboardingRequiredChanged(bool value)
    {
        NextOrFinishCommand.NotifyCanExecuteChanged();
    }

    partial void OnUiLanguageChanged(TargetLanguage value)
    {
        _localizationService.SetLanguage(value);
        _ = PersistLanguageAsync(value);
    }

    private async Task PersistLanguageAsync(TargetLanguage language)
    {
        var settings = await _settingsService.GetSettingsAsync();
        settings.UiLanguage = language;
        await _settingsService.SaveSettingsAsync(settings);
    }

    // --- Account Setup (step 1) ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleStepCount))]
    [NotifyPropertyChangedFor(nameof(HasProviderConfigured))]
    [NotifyPropertyChangedFor(nameof(AccountSummary))]
    private bool _isLoggedIn;

    [ObservableProperty]
    private bool _isLoggingIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleStepCount))]
    private bool _isE2EEOnboardingRequired;

    public E2EEOnboardingViewModel OnboardingViewModel { get; }

    [ObservableProperty]
    private string? _loginDisplayName;

    [ObservableProperty]
    private string? _loginEmail;

    [ObservableProperty]
    private string? _loginError;

    [ObservableProperty]
    private string _loginEmailInput = string.Empty;

    public string LoginPassword { get; set; } = string.Empty;

    // --- Provider Setup (step 2) ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProviderSummary))]
    private AiProviderType _selectedProviderType = AiProviderType.OpenAI;

    [ObservableProperty]
    private string _providerEndpoint = string.Empty;

    [ObservableProperty]
    private string _providerApiKey = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProviderSummary))]
    private string _providerModelName = string.Empty;

    [ObservableProperty]
    private string _azureDeploymentName = string.Empty;

    [ObservableProperty]
    private bool _isTestingConnection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProviderConfigured))]
    private bool _connectionTestPassed;

    [ObservableProperty]
    private string? _connectionTestError;

    [ObservableProperty]
    private bool _isFetchingModels;

    [ObservableProperty]
    private string? _fetchModelsError;

    public ObservableCollection<string> AvailableModels { get; } = [];

    /// <summary>Provider types available in wizard (excludes PiaCloud).</summary>
    public IReadOnlyList<AiProviderType> WizardProviderTypes { get; } =
        [AiProviderType.OpenAI, AiProviderType.AzureOpenAI, AiProviderType.Ollama, AiProviderType.OpenRouter, AiProviderType.OpenAICompatible, AiProviderType.Mistral];

    partial void OnSelectedProviderTypeChanged(AiProviderType value)
    {
        // Reset connection test when provider type changes
        ConnectionTestPassed = false;
        ConnectionTestError = null;
        AvailableModels.Clear();
        FetchModelsError = null;

        // Set sensible defaults
        ProviderEndpoint = value switch
        {
            AiProviderType.Ollama => "http://localhost:11434/v1",
            AiProviderType.OpenAI => "https://api.openai.com/v1",
            AiProviderType.OpenRouter => "https://openrouter.ai/api/v1",
            AiProviderType.Mistral => "https://api.mistral.ai/v1",
            _ => ProviderEndpoint
        };

        NextOrFinishCommand.NotifyCanExecuteChanged();
    }

    // --- Ready step summary ---

    public bool HasProviderConfigured => IsLoggedIn || ConnectionTestPassed;

    public string ProviderSummary => ConnectionTestPassed
        ? $"{SelectedProviderType} — {ProviderModelName}"
        : IsLoggedIn ? "Pia Cloud" : "";

    public string AccountSummary => IsLoggedIn
        ? $"{LoginDisplayName} ({LoginEmail})"
        : "";

    // --- Test result tracking for persisting ---
    private TestConnectionResult? _lastTestResult;

    // --- Events ---

    public event Action? WizardCompleted;

    // --- Commands ---

    public IAsyncRelayCommand NextOrFinishCommand { get; }
    public IRelayCommand BackCommand { get; }
    public IAsyncRelayCommand SkipCommand { get; }
    public IAsyncRelayCommand FinishCommand { get; }
    public IAsyncRelayCommand VoiceInputNameCommand { get; }
    public IAsyncRelayCommand VoiceInputNicknameCommand { get; }
    public IAsyncRelayCommand VoiceInputLocationCommand { get; }
    public IRelayCommand<string> SetOperatingModeCommand { get; }
    public IAsyncRelayCommand LoginWithGoogleCommand { get; }
    public IAsyncRelayCommand LoginWithMicrosoftCommand { get; }
    public IAsyncRelayCommand LoginWithPasswordCommand { get; }
    public IRelayCommand OpenRegistrationPageCommand { get; }
    public IRelayCommand OpenForgotPasswordCommand { get; }
    public IAsyncRelayCommand TestProviderConnectionCommand { get; }
    public IAsyncRelayCommand FetchModelsCommand { get; }

    public FirstRunWizardViewModel(
        ISettingsService settingsService,
        IMemoryService memoryService,
        IVoiceInputService voiceInputService,
        ILocalizationService localizationService,
        IAuthService authService,
        IProviderService providerService,
        ISyncClientService syncClientService,
        IDeviceManagementService deviceManagement,
        E2EEOnboardingViewModel onboardingViewModel,
        ILogger<FirstRunWizardViewModel> logger)
    {
        _settingsService = settingsService;
        _memoryService = memoryService;
        _voiceInputService = voiceInputService;
        _localizationService = localizationService;
        _authService = authService;
        _providerService = providerService;
        _syncClientService = syncClientService;
        _deviceManagement = deviceManagement;
        OnboardingViewModel = onboardingViewModel;
        _logger = logger;
        _uiLanguage = _localizationService.CurrentLanguage;

        // When E2EE onboarding completes in wizard, start sync
        OnboardingViewModel.OnboardingCompleted += async (_, _) =>
        {
            IsE2EEOnboardingRequired = false;
            await _syncClientService.PerformFirstSyncMigrationAsync();
            _syncClientService.StartBackgroundSync();
        };

        NextOrFinishCommand = new AsyncRelayCommand(HandleNextOrFinishAsync, CanExecuteNextOrFinish);
        BackCommand = new RelayCommand(ExecuteBack, CanExecuteBack);
        SkipCommand = new AsyncRelayCommand(ExecuteSkipAsync);
        FinishCommand = new AsyncRelayCommand(ExecuteFinishAsync);
        VoiceInputNameCommand = new AsyncRelayCommand(ExecuteVoiceInputNameAsync);
        VoiceInputNicknameCommand = new AsyncRelayCommand(ExecuteVoiceInputNicknameAsync);
        VoiceInputLocationCommand = new AsyncRelayCommand(ExecuteVoiceInputLocationAsync);
        SetOperatingModeCommand = new RelayCommand<string>(ExecuteSetOperatingMode);
        LoginWithGoogleCommand = new AsyncRelayCommand(LoginWithGoogleAsync);
        LoginWithMicrosoftCommand = new AsyncRelayCommand(LoginWithMicrosoftAsync);
        LoginWithPasswordCommand = new AsyncRelayCommand(LoginWithPasswordAsync);
        OpenRegistrationPageCommand = new RelayCommand(ExecuteOpenRegistrationPage);
        OpenForgotPasswordCommand = new RelayCommand(ExecuteOpenForgotPassword);
        TestProviderConnectionCommand = new AsyncRelayCommand(TestProviderConnectionAsync);
        FetchModelsCommand = new AsyncRelayCommand(FetchModelsAsync);
    }

    // --- Navigation ---

    private bool CanExecuteNextOrFinish()
    {
        if (IsCompleting) return false;

        // Block Next on account step while E2EE onboarding is in progress
        if (CurrentStep == 1 && IsE2EEOnboardingRequired) return false;

        // Block Next on provider step unless test passed
        if (CurrentStep == 2 && !ConnectionTestPassed) return false;

        return true;
    }

    private bool CanExecuteBack() => !IsFirstStep && !IsCompleting;

    private void ExecuteNext()
    {
        if (CurrentStep >= TotalSteps - 1) return;

        // Skip provider step if logged in
        if (CurrentStep == 1 && IsLoggedIn)
            CurrentStep = 3;
        else
            CurrentStep++;

        NotifyNavigationChanged();
    }

    private void ExecuteBack()
    {
        if (CurrentStep <= 0) return;

        // Skip provider step if logged in
        if (CurrentStep == 3 && IsLoggedIn)
            CurrentStep = 1;
        else
            CurrentStep--;

        NotifyNavigationChanged();
    }

    private void NotifyNavigationChanged()
    {
        NextOrFinishCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
    }

    private async Task HandleNextOrFinishAsync()
    {
        if (IsLastStep)
            await ExecuteFinishAsync();
        else
            ExecuteNext();
    }

    // --- Account login ---

    private async Task LoginWithGoogleAsync() => await LoginAsync("google");
    private async Task LoginWithMicrosoftAsync() => await LoginAsync("microsoft");

    private async Task LoginAsync(string provider)
    {
        IsLoggingIn = true;
        LoginError = null;

        try
        {
            var (success, errorMessage) = await _authService.LoginAsync(provider);
            if (success)
            {
                IsLoggedIn = true;
                LoginDisplayName = _authService.UserDisplayName;
                LoginEmail = _authService.UserEmail;

                await _providerService.EnsureBuiltInProviderAsync();
                await HandlePostLoginSyncAsync();

                // Update navigation since step 2 is now skipped
                OnPropertyChanged(nameof(VisibleStepCount));
                NextOrFinishCommand.NotifyCanExecuteChanged();
            }
            else if (errorMessage is not null)
            {
                LoginError = errorMessage;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login failed during wizard");
            LoginError = ex.Message;
        }
        finally
        {
            IsLoggingIn = false;
        }
    }

    // --- Local auth ---

    private async Task LoginWithPasswordAsync()
    {
        if (string.IsNullOrWhiteSpace(LoginEmailInput) || string.IsNullOrWhiteSpace(LoginPassword))
        {
            LoginError = _localizationService["Sync_LocalAuth_FieldsRequired"];
            return;
        }

        IsLoggingIn = true;
        LoginError = null;

        try
        {
            var (success, errorMessage) = await _authService.LoginWithPasswordAsync(LoginEmailInput, LoginPassword);
            if (success)
            {
                LoginPassword = string.Empty;
                IsLoggedIn = true;
                LoginDisplayName = _authService.UserDisplayName;
                LoginEmail = _authService.UserEmail;

                await _providerService.EnsureBuiltInProviderAsync();
                await HandlePostLoginSyncAsync();

                OnPropertyChanged(nameof(VisibleStepCount));
                NextOrFinishCommand.NotifyCanExecuteChanged();
            }
            else
            {
                LoginError = errorMessage ?? _localizationService["Sync_LocalAuth_InvalidCredentials"];
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Password login failed during wizard");
            LoginError = ex.Message;
        }
        finally
        {
            IsLoggingIn = false;
        }
    }

    private void ExecuteOpenRegistrationPage()
    {
        _ = OpenAuthPageAsync("auth/register.html");
    }

    private void ExecuteOpenForgotPassword()
    {
        _ = OpenAuthPageAsync("auth/forgot-password.html");
    }

    private async Task OpenAuthPageAsync(string path)
    {
        var settings = await _settingsService.GetSettingsAsync();
        var serverUrl = settings.ServerUrl?.TrimEnd('/');
        if (string.IsNullOrEmpty(serverUrl))
        {
            LoginError = _localizationService["Sync_LocalAuth_ServerUrlRequired"];
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"{serverUrl}/{path}") { UseShellExecute = true });
    }

    /// <summary>
    /// Check E2EE status before starting sync. If E2EE is enabled on the account,
    /// show onboarding instead of syncing (to avoid pushing unencrypted data).
    /// </summary>
    private async Task HandlePostLoginSyncAsync()
    {
        var e2eeStatus = await _deviceManagement.CheckE2EEStatusAsync();
        if (e2eeStatus is { IsEnabled: true } && !_deviceManagement.IsInitialized())
        {
            _logger.LogInformation("E2EE enabled on account but UMK not available; showing onboarding in wizard");
            IsE2EEOnboardingRequired = true;
            return;
        }

        await _syncClientService.PerformFirstSyncMigrationAsync();
        _syncClientService.StartBackgroundSync();
    }

    // --- Provider test/fetch ---

    private async Task TestProviderConnectionAsync()
    {
        if (IsTestingConnection) return;

        IsTestingConnection = true;
        ConnectionTestPassed = false;
        ConnectionTestError = null;

        try
        {
            var tempProvider = new AiProvider
            {
                Name = SelectedProviderType.ToString(),
                ProviderType = SelectedProviderType,
                Endpoint = ProviderEndpoint.Trim(),
                ModelName = ProviderModelName.Trim(),
                AzureDeploymentName = AzureDeploymentName.Trim()
            };

            var apiKey = string.IsNullOrWhiteSpace(ProviderApiKey) ? null : ProviderApiKey;
            _lastTestResult = await _providerService.TestConnectionAsync(tempProvider, apiKey);
            ConnectionTestPassed = _lastTestResult.Success;
            if (_lastTestResult.Success)
                ConnectionTestError = null;
        }
        catch (Exception ex)
        {
            ConnectionTestError = ex.Message;
        }
        finally
        {
            IsTestingConnection = false;
            NextOrFinishCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task FetchModelsAsync()
    {
        if (IsFetchingModels) return;

        IsFetchingModels = true;
        FetchModelsError = null;

        try
        {
            var apiKey = string.IsNullOrWhiteSpace(ProviderApiKey) ? null : ProviderApiKey;
            var models = await _providerService.FetchModelsAsync(ProviderEndpoint.Trim(), apiKey, SelectedProviderType);

            AvailableModels.Clear();
            foreach (var model in models)
                AvailableModels.Add(model);

            if (models.Count == 0)
                FetchModelsError = "No models found at this endpoint.";
        }
        catch (Exception ex)
        {
            FetchModelsError = ex.Message;
        }
        finally
        {
            IsFetchingModels = false;
        }
    }

    // --- Skip / Finish ---

    private async Task ExecuteSkipAsync()
    {
        try
        {
            IsCompleting = true;
            var settings = await _settingsService.GetSettingsAsync();
            settings.HasCompletedFirstRunWizard = true;
            await _settingsService.SaveSettingsAsync(settings);
            WizardCompleted?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to skip wizard");
            WizardCompleted?.Invoke();
        }
        finally
        {
            IsCompleting = false;
        }
    }

    private async Task ExecuteFinishAsync()
    {
        try
        {
            IsCompleting = true;

            // Persist profile
            var hasProfileData = !string.IsNullOrWhiteSpace(UserName)
                || !string.IsNullOrWhiteSpace(Nickname)
                || !string.IsNullOrWhiteSpace(Location);

            if (hasProfileData)
            {
                var preferredName = !string.IsNullOrWhiteSpace(Nickname) ? Nickname : UserName;
                var profileData = new
                {
                    name = UserName.Trim(),
                    nickname = Nickname.Trim(),
                    location = Location.Trim(),
                    operating_mode = OperatingMode.ToString().ToLowerInvariant(),
                    preferred_name = preferredName.Trim()
                };

                var jsonData = JsonSerializer.Serialize(profileData);

                var existing = await _memoryService.GetObjectsByTypeAsync(MemoryObjectTypes.PersonalProfile);
                if (existing.Count > 0)
                {
                    await _memoryService.UpdateObjectDataAsync(existing[0].Id, "Personal Profile", jsonData);
                }
                else
                {
                    await _memoryService.CreateObjectAsync(MemoryObjectTypes.PersonalProfile, "Personal Profile", jsonData);
                }
            }

            // Persist provider configured during wizard (skip-login path)
            if (!IsLoggedIn && ConnectionTestPassed)
            {
                var provider = new AiProvider
                {
                    Name = SelectedProviderType.ToString(),
                    ProviderType = SelectedProviderType,
                    Endpoint = ProviderEndpoint.Trim(),
                    ModelName = ProviderModelName.Trim(),
                    AzureDeploymentName = AzureDeploymentName.Trim(),
                    SupportsToolCalling = _lastTestResult?.SupportsToolCalling ?? true,
                    SupportsStreaming = _lastTestResult?.SupportsStreaming ?? true
                };

                var apiKey = string.IsNullOrWhiteSpace(ProviderApiKey) ? null : ProviderApiKey;
                await _providerService.AddProviderAsync(provider, apiKey);
            }

            var settings = await _settingsService.GetSettingsAsync();
            settings.HasCompletedFirstRunWizard = true;
            settings.UserOperatingMode = OperatingMode;
            await _settingsService.SaveSettingsAsync(settings);

            WizardCompleted?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to complete wizard");
            WizardCompleted?.Invoke();
        }
        finally
        {
            IsCompleting = false;
        }
    }

    // --- Voice input ---

    private async Task ExecuteVoiceInputNameAsync()
    {
        var result = await _voiceInputService.CaptureVoiceInputAsync();
        if (!string.IsNullOrWhiteSpace(result))
            UserName = result.Trim();
    }

    private async Task ExecuteVoiceInputNicknameAsync()
    {
        var result = await _voiceInputService.CaptureVoiceInputAsync();
        if (!string.IsNullOrWhiteSpace(result))
            Nickname = result.Trim();
    }

    private async Task ExecuteVoiceInputLocationAsync()
    {
        var result = await _voiceInputService.CaptureVoiceInputAsync();
        if (!string.IsNullOrWhiteSpace(result))
            Location = result.Trim();
    }

    private void ExecuteSetOperatingMode(string? mode)
    {
        if (Enum.TryParse<UserOperatingMode>(mode, out var parsed))
            OperatingMode = parsed;
    }
}
