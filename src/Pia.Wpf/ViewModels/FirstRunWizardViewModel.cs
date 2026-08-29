using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
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
    private readonly IPolicyService _policyService;
    private readonly ILogger<FirstRunWizardViewModel> _logger;

    public const int TotalSteps = 7;

    // --- Navigation ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFirstStep))]
    [NotifyPropertyChangedFor(nameof(IsLastStep))]
    [NotifyPropertyChangedFor(nameof(NextButtonText))]
    private int _currentStep;

    public bool IsFirstStep => CurrentStep == 0;
    public bool IsLastStep => CurrentStep == TotalSteps - 1;
    public string NextButtonText => IsLastStep
        ? _localizationService["Wizard_GetStarted"]
        : _localizationService["Wizard_Next"];

    public int VisibleStepCount => IsLoggedIn
        ? (IsE2EESetupVisible ? 6 : 5)
        : (IsProviderStepVisible ? 6 : 5);

    /// <summary>An account that still owes its declaration must not be offered E2EE — the server would 403.</summary>
    public bool IsE2EESetupVisible =>
        IsLoggedIn && !RequiresBusinessProfile && !IsE2EEOnboardingRequired && !_cloudAccountHasE2EE;

    /// <summary>
    /// The provider step is the one place outside settings that creates a provider, so the policy lock
    /// has to reach it too — otherwise a managed machine hands the user a provider form on first launch.
    /// </summary>
    public bool IsProviderStepVisible => !IsLoggedIn && _allowProviderManagement;

    private bool _cloudAccountHasE2EE;
    private bool _allowProviderManagement = true;

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

    partial void OnRequiresBusinessProfileChanged(bool value)
    {
        NextOrFinishCommand.NotifyCanExecuteChanged();
    }

    partial void OnUiLanguageChanged(TargetLanguage value)
    {
        _localizationService.SetLanguage(value);
        _ = PersistLanguageAsync(value);
    }

    private async Task LoadProviderPolicyAsync()
    {
        _allowProviderManagement = (await _settingsService.GetSettingsAsync()).AllowProviderManagement;
        OnPropertyChanged(nameof(IsProviderStepVisible));
        OnPropertyChanged(nameof(VisibleStepCount));
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
    [NotifyPropertyChangedFor(nameof(IsE2EESetupVisible))]
    private bool _isLoggedIn;

    [ObservableProperty]
    private bool _isLoggingIn;

    /// <summary>Set after a sign-in the server considers incomplete — single sign-on never sees the form.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleStepCount))]
    [NotifyPropertyChangedFor(nameof(IsE2EESetupVisible))]
    private bool _requiresBusinessProfile;

    [ObservableProperty]
    private string _companyNameInput = "";

    [ObservableProperty]
    private string? _businessProfileError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleStepCount))]
    [NotifyPropertyChangedFor(nameof(IsE2EESetupVisible))]
    private bool _isE2EEOnboardingRequired;

    public E2EEOnboardingViewModel OnboardingViewModel { get; }

    public E2EESetupStepViewModel E2EESetupViewModel { get; }

    [ObservableProperty]
    private string? _loginDisplayName;

    [ObservableProperty]
    private string? _loginEmail;

    [ObservableProperty]
    private string? _loginError;

    [ObservableProperty]
    private string _loginEmailInput = string.Empty;

    public string LoginPassword { get; set; } = string.Empty;

    public bool IsLocalLoginVisible => _policyService.IsLoginProviderAllowed("local");
    public bool IsGoogleLoginVisible => _policyService.IsLoginProviderAllowed("google");
    public bool IsMicrosoftLoginVisible => _policyService.IsLoginProviderAllowed("microsoft");
    public bool IsEntraIdLoginVisible => _policyService.IsLoginProviderAllowed("entraid");
    public bool IsAnyOAuthLoginVisible => IsGoogleLoginVisible || IsMicrosoftLoginVisible || IsEntraIdLoginVisible;

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
    public IAsyncRelayCommand LoginWithEntraIdCommand { get; }
    public IAsyncRelayCommand SubmitBusinessProfileCommand { get; }
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
        IPolicyService policyService,
        E2EEOnboardingViewModel onboardingViewModel,
        E2EESetupStepViewModel e2eeSetupViewModel,
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
        _policyService = policyService;
        OnboardingViewModel = onboardingViewModel;
        E2EESetupViewModel = e2eeSetupViewModel;
        _logger = logger;
        _uiLanguage = _localizationService.CurrentLanguage;
        _localizationService.LanguageChanged += (_, _) => OnPropertyChanged(nameof(NextButtonText));

        // When E2EE onboarding completes in wizard, start sync
        OnboardingViewModel.OnboardingCompleted += async (_, _) =>
        {
            try
            {
                IsE2EEOnboardingRequired = false;
                _syncClientService.NotifyE2EEOnboardingCompleted();
                await _syncClientService.PerformFirstSyncMigrationAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "First sync after E2EE onboarding failed in wizard");
            }
            _syncClientService.StartBackgroundSync();
        };

        NextOrFinishCommand = new AsyncRelayCommand(HandleNextOrFinishAsync, CanExecuteNextOrFinish);
        BackCommand = new RelayCommand(ExecuteBack, CanExecuteBack);

        // E2EE setup step controls when the wizard advances past step 2
        E2EESetupViewModel.AdvanceRequested += AdvanceFromE2EEStep;
        E2EESetupViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(E2EESetupStepViewModel.State)
                or nameof(E2EESetupStepViewModel.IsBusy)
                or nameof(E2EESetupStepViewModel.HasConfirmedRecoveryCode)
                or nameof(E2EESetupStepViewModel.CanGoBack))
            {
                NextOrFinishCommand.NotifyCanExecuteChanged();
                BackCommand.NotifyCanExecuteChanged();
            }
        };
        SkipCommand = new AsyncRelayCommand(ExecuteSkipAsync);

        LoadProviderPolicyAsync().SafeFireAndForget(_logger);
        FinishCommand = new AsyncRelayCommand(ExecuteFinishAsync);
        VoiceInputNameCommand = new AsyncRelayCommand(ExecuteVoiceInputNameAsync);
        VoiceInputNicknameCommand = new AsyncRelayCommand(ExecuteVoiceInputNicknameAsync);
        VoiceInputLocationCommand = new AsyncRelayCommand(ExecuteVoiceInputLocationAsync);
        SetOperatingModeCommand = new RelayCommand<string>(ExecuteSetOperatingMode);
        LoginWithGoogleCommand = new AsyncRelayCommand(LoginWithGoogleAsync);
        LoginWithMicrosoftCommand = new AsyncRelayCommand(LoginWithMicrosoftAsync);
        LoginWithEntraIdCommand = new AsyncRelayCommand(LoginWithEntraIdAsync);
        SubmitBusinessProfileCommand = new AsyncRelayCommand(SubmitBusinessProfileAsync);
        LoginWithPasswordCommand = new AsyncRelayCommand(LoginWithPasswordAsync);
        OpenRegistrationPageCommand = new RelayCommand(ExecuteOpenRegistrationPage);
        OpenForgotPasswordCommand = new RelayCommand(ExecuteOpenForgotPassword);
        TestProviderConnectionCommand = new AsyncRelayCommand(TestProviderConnectionAsync);
        FetchModelsCommand = new AsyncRelayCommand(FetchModelsAsync);
    }

    public async Task InitializeAsync()
    {
        try
        {
            // No IsLoggedIn guard: it can still be false while the stored token loads, and the
            // service answers null without a token, which leaves the state alone.
            var requires = await _authService.RequiresBusinessProfileAsync();

            // A restored session has to reach the view too, or the declaration blocks Next behind a
            // step still offering the sign-in buttons.
            if (_authService.IsLoggedIn)
            {
                IsLoggedIn = true;
                LoginDisplayName = _authService.UserDisplayName;
                LoginEmail = _authService.UserEmail;
            }

            if (requires is bool outstanding)
                RequiresBusinessProfile = outstanding;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the business-profile state on wizard load");
        }
    }

    // --- Navigation ---

    private bool CanExecuteNextOrFinish()
    {
        if (IsCompleting) return false;

        // Account step (step 1): E2EE onboarding and the trader declaration both have to finish here.
        if (CurrentStep == 1 && (IsE2EEOnboardingRequired || RequiresBusinessProfile)) return false;

        // E2EE setup step (step 2): block Next while busy or while waiting for recovery-code confirmation
        if (CurrentStep == 2 && IsE2EESetupVisible)
        {
            if (E2EESetupViewModel.IsBusy) return false;
            if (E2EESetupViewModel.State == E2EESetupState.SavingRecoveryCode
                && !E2EESetupViewModel.HasConfirmedRecoveryCode) return false;
        }

        // Provider step (step 3): block Next unless connection test passed (only when shown)
        if (CurrentStep == 3 && IsProviderStepVisible && !ConnectionTestPassed) return false;

        return true;
    }

    private bool CanExecuteBack()
    {
        if (IsFirstStep || IsCompleting) return false;
        if (CurrentStep == 2 && IsE2EESetupVisible && !E2EESetupViewModel.CanGoBack) return false;
        return true;
    }

    private void ExecuteNext()
    {
        if (CurrentStep >= TotalSteps - 1) return;

        CurrentStep = CurrentStep switch
        {
            1 when IsE2EESetupVisible => 2,
            1 when !IsProviderStepVisible => 4,     // skip both E2EE (2) and Provider (3)
            1 => 3,                                 // provider step is shown: go to it
            2 => IsProviderStepVisible ? 3 : 4,     // E2EE step is only reachable when visible
            _ => CurrentStep + 1,
        };

        NotifyNavigationChanged();
    }

    private void ExecuteBack()
    {
        if (CurrentStep <= 0) return;

        CurrentStep = CurrentStep switch
        {
            2 => 1,
            3 => IsE2EESetupVisible ? 2 : 1,
            4 when !IsProviderStepVisible => IsE2EESetupVisible ? 2 : 1,
            4 => 3,
            _ => CurrentStep - 1,
        };

        NotifyNavigationChanged();
    }

    private void AdvanceFromE2EEStep(bool e2eeEnabled)
    {
        // Always called from CurrentStep == 2; skip Provider when it is not shown.
        CurrentStep = IsProviderStepVisible ? 3 : 4;
        NotifyNavigationChanged();
    }

    private void NotifyNavigationChanged()
    {
        NextOrFinishCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
    }

    private async Task HandleNextOrFinishAsync()
    {
        // On the E2EE step, route Next through the step view model.
        if (CurrentStep == 2 && IsE2EESetupVisible)
        {
            await E2EESetupViewModel.ProceedCommand.ExecuteAsync(null);
            return;
        }

        if (IsLastStep)
            await ExecuteFinishAsync();
        else
            ExecuteNext();
    }

    // --- Account login ---

    private async Task LoginWithGoogleAsync() => await LoginAsync("google");
    private async Task LoginWithMicrosoftAsync() => await LoginAsync("microsoft");
    private async Task LoginWithEntraIdAsync() => await LoginAsync("entraid");

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

                if (string.IsNullOrWhiteSpace(UserName) && !string.IsNullOrWhiteSpace(_authService.UserDisplayName))
                    UserName = _authService.UserDisplayName;

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

    private async Task SubmitBusinessProfileAsync()
    {
        BusinessProfileError = null;

        var (success, error) = await BusinessProfileSubmission.SubmitAsync(
            _authService, _localizationService, CompanyNameInput);

        BusinessProfileError = error;
        if (!success) return;

        RequiresBusinessProfile = false;
        CompanyNameInput = "";
        await HandlePostLoginSyncAsync();
    }

    /// <summary>The account's E2EE state decides between inline onboarding, deferring to the setup step, and syncing now.</summary>
    private async Task HandlePostLoginSyncAsync()
    {
        RequiresBusinessProfile = _authService.RequiresBusinessProfile;
        if (RequiresBusinessProfile)
        {
            // Probing E2EE or syncing would only collect 403s until the declaration is in.
            _logger.LogInformation("Account still owes its business profile; deferring sync");
            return;
        }

        var e2eeStatus = await _deviceManagement.CheckE2EEStatusAsync();

        if (e2eeStatus is { IsEnabled: true } && !_deviceManagement.IsInitialized())
        {
            _logger.LogInformation("E2EE enabled on account but UMK not available; showing onboarding in wizard");
            _cloudAccountHasE2EE = true;
            IsE2EEOnboardingRequired = true;
            _syncClientService.NotifyE2EEOnboardingRequired();
            OnPropertyChanged(nameof(IsE2EESetupVisible));
            OnPropertyChanged(nameof(VisibleStepCount));
            return;
        }

        if (e2eeStatus is null or { IsEnabled: false })
        {
            _logger.LogInformation("E2EE not enabled on account; deferring first sync until E2EE setup step decides");
            _cloudAccountHasE2EE = false;
            OnPropertyChanged(nameof(IsE2EESetupVisible));
            OnPropertyChanged(nameof(VisibleStepCount));
            // Do NOT start sync here — the E2EE step will start it when the user makes a choice.
            return;
        }

        // E2EE already on and UMK available — start sync.
        _cloudAccountHasE2EE = true;
        OnPropertyChanged(nameof(IsE2EESetupVisible));
        OnPropertyChanged(nameof(VisibleStepCount));
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
            settings.DefaultTemplateId ??= Shared.BuiltInTemplates.ClarityAndGrammarId;
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
            if (IsProviderStepVisible && ConnectionTestPassed)
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
            settings.DefaultTemplateId ??= Shared.BuiltInTemplates.ClarityAndGrammarId;
            settings.SetPersonaForMode(WindowMode.Assistant, OperatingMode == UserOperatingMode.Business
                ? Shared.BuiltInPersonas.PiaBusinessId
                : Shared.BuiltInPersonas.PiaPersonalId);
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
