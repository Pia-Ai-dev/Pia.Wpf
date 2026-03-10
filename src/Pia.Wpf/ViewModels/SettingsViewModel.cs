using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Shared.E2EE;
using Pia.ViewModels.Models;
using Pia.Navigation;
using System.Collections.ObjectModel;
using System.IO;

namespace Pia.ViewModels;

public partial class SettingsViewModel : ObservableObject, INavigationAware
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly IProviderService _providerService;
    private readonly ITemplateService _templateService;
    private readonly ISettingsService _settingsService;
    private readonly IAiClientService _aiClientService;
    private readonly ITextOptimizationService _textOptimizationService;
    private readonly ITranscriptionService _transcriptionService;
    private readonly INavigationService _navigationService;
    private readonly IDialogService _dialogService;
    private readonly ITrayIconService _trayIconService;
    private readonly ITtsService _ttsService;
    private readonly Wpf.Ui.ISnackbarService _snackbarService;
    private readonly IAuthService _authService;
    private readonly ISyncClientService _syncClientService;
    private readonly ILocalizationService _localizationService;
    private readonly IDeviceManagementService _deviceManagement;
    private readonly IDeviceKeyService _deviceKeys;
    private bool _isLoading;

    public E2EEOnboardingViewModel OnboardingViewModel { get; }

    public SettingsViewModel(
        ILogger<SettingsViewModel> logger,
        IProviderService providerService,
        ITemplateService templateService,
        ISettingsService settingsService,
        IAiClientService aiClientService,
        ITextOptimizationService textOptimizationService,
        ITranscriptionService transcriptionService,
        INavigationService navigationService,
        IDialogService dialogService,
        ITrayIconService trayIconService,
        ITtsService ttsService,
        Wpf.Ui.ISnackbarService snackbarService,
        IAuthService authService,
        ISyncClientService syncClientService,
        ILocalizationService localizationService,
        IDeviceManagementService deviceManagement,
        IDeviceKeyService deviceKeys,
        E2EEOnboardingViewModel onboardingViewModel)
    {
        _logger = logger;
        _providerService = providerService;
        _templateService = templateService;
        _settingsService = settingsService;
        _aiClientService = aiClientService;
        _textOptimizationService = textOptimizationService;
        _transcriptionService = transcriptionService;
        _navigationService = navigationService;
        _dialogService = dialogService;
        _trayIconService = trayIconService;
        _ttsService = ttsService;
        _snackbarService = snackbarService;
        _authService = authService;
        _syncClientService = syncClientService;
        _localizationService = localizationService;
        _deviceManagement = deviceManagement;
        _deviceKeys = deviceKeys;
        OnboardingViewModel = onboardingViewModel;

        // When onboarding completes, resume sync
        OnboardingViewModel.OnboardingCompleted += async (_, _) =>
        {
            IsE2EEOnboardingRequired = false;
            IsE2EEEnabled = true;
            DeviceFingerprint = _deviceKeys.GetFingerprint();
            await _syncClientService.PerformFirstSyncMigrationAsync();
            _syncClientService.StartBackgroundSync();
        };

        // When sync detects E2EE is enabled on the server but not locally, show onboarding
        _syncClientService.E2EEOnboardingRequired += (_, _) =>
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                IsE2EEOnboardingRequired = true;
            });
        };

        // When a pending device is detected during sync, prompt for approval
        _syncClientService.PendingDeviceDetected += (_, args) =>
        {
            // Dispatch to UI thread
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(
                () => HandlePendingDevicesAsync(args.PendingDevices));
        };

        Providers = new ObservableCollection<AiProvider>();
        Providers.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(NonCloudProviders));
            OnPropertyChanged(nameof(OptimizeProviderOptions));
            OnPropertyChanged(nameof(ShowCloudSetupBanner));
        };
        Templates = new ObservableCollection<OptimizationTemplate>();
    }

    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is int tabIndex)
            SelectedTabIndex = tabIndex;
    }

    public async Task OnNavigatedToAsync(object? parameter)
    {
        // Only load data if not already loaded
        if (Providers.Count > 0)
        {
            _isLoading = false;
            return;
        }

        _isLoading = true;

        var providersList = await _providerService.GetProvidersAsync();
        foreach (var provider in providersList)
            Providers.Add(provider);

        var templatesList = await _templateService.GetTemplatesAsync();
        foreach (var template in templatesList)
            Templates.Add(template);

        var settings = await _settingsService.GetSettingsAsync();
        DefaultTemplateId = settings.DefaultTemplateId;
        UseSameProviderForAllModes = settings.UseSameProviderForAllModes;
        OptimizeProviderId = settings.ModeProviderDefaults.TryGetValue(WindowMode.Optimize, out var optId) ? optId : null;
        AssistantProviderId = settings.ModeProviderDefaults.TryGetValue(WindowMode.Assistant, out var astId) ? astId : null;
        ResearchProviderId = settings.ModeProviderDefaults.TryGetValue(WindowMode.Research, out var resId) ? resId : null;
        OutputAction = settings.DefaultOutputAction;
        AutoTypeDelayMs = settings.AutoTypeDelayMs;
        WhisperModel = settings.WhisperModel;
        StartMinimized = settings.StartMinimized;
        ShowTodoPanelButton = settings.ShowTodoPanelButton;
        DefaultWindowMode = settings.DefaultWindowMode;
        _optimizeHotkey = settings.OptimizeHotkey;
        OptimizeHotkeyDisplayText = _optimizeHotkey.DisplayText;
        _assistantHotkey = settings.AssistantHotkey;
        AssistantHotkeyDisplayText = _assistantHotkey?.DisplayText ?? _localizationService["Msg_Settings_HotkeyNotSet"];
        _researchHotkey = settings.ResearchHotkey;
        ResearchHotkeyDisplayText = _researchHotkey?.DisplayText ?? _localizationService["Msg_Settings_HotkeyNotSet"];
        TargetSpeechLanguage = settings.TargetSpeechLanguage;
        UiLanguage = settings.UiLanguage;

        // Load sync state
        ServerUrl = settings.ServerUrl ?? "";
        TrustSelfSignedCertificates = settings.TrustSelfSignedCertificates;
        UpdateSyncState();

        // Load E2EE state
        IsE2EEEnabled = settings.IsE2EEEnabled;
        if (_deviceManagement.IsInitialized())
            DeviceFingerprint = _deviceKeys.GetFingerprint();

        // Load TTS state
        SelectedVoiceKey = settings.TtsVoiceModelKey;
        await LoadTtsVoicesAsync();

        // Load privacy settings
        TokenizationEnabled = settings.Privacy.TokenizationEnabled;
        foreach (var entry in PiiKeywords)
            entry.PropertyChanged -= OnPiiKeywordEntryChanged;
        var entries = settings.Privacy.PiiKeywords;
        foreach (var entry in entries)
            entry.PropertyChanged += OnPiiKeywordEntryChanged;
        PiiKeywords = new ObservableCollection<PiiKeywordEntry>(entries);

        await RefreshProviderDisplayItemsAsync();

        _isLoading = false;
    }

    public void OnNavigatedFrom()
    {
    }

    // Sync properties
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    [NotifyPropertyChangedFor(nameof(ShowCloudSetupBanner))]
    private bool _isSyncLoggedIn;

    [ObservableProperty]
    private string? _syncUserEmail;

    [ObservableProperty]
    private string? _syncUserDisplayName;

    [ObservableProperty]
    private string? _syncProvider;

    [ObservableProperty]
    private string _serverUrl = "";

    [ObservableProperty]
    private bool _trustSelfSignedCertificates;

    public bool IsDevMode => Bootstrapper.IsDevMode;

    [ObservableProperty]
    private bool _isSyncLoggingIn;

    // Local auth properties
    [ObservableProperty]
    private string _loginEmail = "";

    [ObservableProperty]
    private string _loginPassword = "";

    [ObservableProperty]
    private string? _loginErrorMessage;

    // E2EE properties
    [ObservableProperty]
    private bool _isE2EEEnabled;

    [ObservableProperty]
    private bool _canToggleE2EE = true;

    [ObservableProperty]
    private string _deviceFingerprint = "";

    [ObservableProperty]
    private bool _isE2EEOnboardingRequired;

    [ObservableProperty]
    private int _selectedTabIndex;

    [RelayCommand]
    private void GoToCloudSync() => SelectedTabIndex = 4;

    [ObservableProperty]
    private ObservableCollection<AiProvider> _providers;

    public ObservableCollection<ProviderDisplayItem> ProviderDisplayItems { get; } = new();

    public List<AiProvider> NonCloudProviders =>
        Providers.Where(p => p.ProviderType != AiProviderType.PiaCloud).ToList();

    public List<AiProvider> OptimizeProviderOptions =>
        UseSameProviderForAllModes ? NonCloudProviders : Providers.ToList();

    public string OptimizeProviderLabel =>
        UseSameProviderForAllModes
            ? Localization.LocalizationSource.Instance["Providers_AllModes"]
            : Localization.LocalizationSource.Instance["Providers_Optimize"];

    public bool ShowCloudSetupBanner =>
        !IsSyncLoggedIn && !Providers.Any(p => p.ProviderType != AiProviderType.PiaCloud);

    [ObservableProperty]
    private ObservableCollection<OptimizationTemplate> _templates;

    [ObservableProperty]
    private Guid? _defaultTemplateId;

    [ObservableProperty]
    private Guid? _optimizeProviderId;

    [ObservableProperty]
    private Guid? _assistantProviderId;

    [ObservableProperty]
    private Guid? _researchProviderId;

    [ObservableProperty]
    private bool _useSameProviderForAllModes = true;

    [ObservableProperty]
    private OutputAction _outputAction;

    [ObservableProperty]
    private int _autoTypeDelayMs;

    [ObservableProperty]
    private WhisperModelSize _whisperModel;

    [ObservableProperty]
    private bool _startMinimized;

    [ObservableProperty]
    private bool _showTodoPanelButton = true;

    [ObservableProperty]
    private WindowMode _defaultWindowMode;

    [ObservableProperty]
    private TargetSpeechLanguage _targetSpeechLanguage;

    [ObservableProperty]
    private TargetLanguage _uiLanguage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTestConnectionInProgress))]
    private Guid? _testingProviderId;

    public bool IsTestConnectionInProgress => TestingProviderId.HasValue;

    [ObservableProperty]
    private string _optimizeHotkeyDisplayText = "Ctrl+Alt+O";

    [ObservableProperty]
    private string _assistantHotkeyDisplayText = "";

    [ObservableProperty]
    private string _researchHotkeyDisplayText = "";

    private KeyboardShortcut _optimizeHotkey = KeyboardShortcut.DefaultCtrlAltO();
    private KeyboardShortcut? _assistantHotkey = KeyboardShortcut.DefaultCtrlAltP();
    private KeyboardShortcut? _researchHotkey = KeyboardShortcut.DefaultCtrlAltR();

    // TTS properties
    [ObservableProperty]
    private ObservableCollection<TtsVoice> _ttsVoices = new();

    [ObservableProperty]
    private string _selectedVoiceKey = "en_US-lessac-medium";

    // Privacy properties
    [ObservableProperty]
    private bool _tokenizationEnabled;

    [ObservableProperty]
    private ObservableCollection<PiiKeywordEntry> _piiKeywords = new();

    [ObservableProperty]
    private string _newKeywordInput = string.Empty;

    [ObservableProperty]
    private string _selectedNewCategory = "Custom";

    public List<string> AvailableCategories { get; } = ["Person", "Nickname", "Email", "Phone", "Address", "Date", "Custom"];

    public IEnumerable<OutputAction> OutputActions => Enum.GetValues<OutputAction>();
    public IEnumerable<WhisperModelSize> WhisperModels => Enum.GetValues<WhisperModelSize>();
    public IEnumerable<TargetSpeechLanguage> TargetSpeechLanguages => Enum.GetValues<TargetSpeechLanguage>();
    public IEnumerable<WindowMode> WindowModes => Enum.GetValues<WindowMode>();
    public IEnumerable<TargetLanguage> UiLanguages => Enum.GetValues<TargetLanguage>();

    private async void SafeFireAndForget(Task task)
    {
        try { await task; }
        catch (Exception ex) { _logger.LogError(ex, "Background operation failed"); }
    }

    partial void OnDefaultTemplateIdChanged(Guid? value)
    {
        if (!_isLoading) SafeFireAndForget(SaveGeneralSettingsAsync());
    }

    partial void OnTokenizationEnabledChanged(bool value)
    {
        if (!_isLoading) SafeFireAndForget(SaveGeneralSettingsAsync());
    }

    [RelayCommand]
    private async Task AddPiiKeywordAsync()
    {
        var keyword = NewKeywordInput?.Trim();
        if (string.IsNullOrWhiteSpace(keyword) || PiiKeywords.Any(e => string.Equals(e.Keyword, keyword, StringComparison.OrdinalIgnoreCase)))
            return;

        var entry = new PiiKeywordEntry { Keyword = keyword, Category = SelectedNewCategory };
        entry.PropertyChanged += OnPiiKeywordEntryChanged;
        PiiKeywords.Add(entry);
        NewKeywordInput = string.Empty;
        await SaveGeneralSettingsAsync();
    }

    [RelayCommand]
    private async Task RemovePiiKeywordAsync(PiiKeywordEntry entry)
    {
        entry.PropertyChanged -= OnPiiKeywordEntryChanged;
        if (PiiKeywords.Remove(entry))
            await SaveGeneralSettingsAsync();
    }

    private void OnPiiKeywordEntryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_isLoading && e.PropertyName == nameof(PiiKeywordEntry.Category))
            SafeFireAndForget(SaveGeneralSettingsAsync());
    }

    partial void OnOptimizeProviderIdChanged(Guid? value)
    {
        if (!_isLoading)
        {
            if (UseSameProviderForAllModes && value.HasValue)
            {
                // Pia Cloud only supports Optimize — don't propagate to other modes
                var isPiaCloud = Providers.Any(p => p.Id == value && p.ProviderType == AiProviderType.PiaCloud);
                if (!isPiaCloud)
                {
                    _isLoading = true;
                    AssistantProviderId = value;
                    ResearchProviderId = value;
                    _isLoading = false;
                }
            }
            SafeFireAndForget(SaveGeneralSettingsAsync());
        }
    }

    partial void OnAssistantProviderIdChanged(Guid? value)
    {
        if (!_isLoading) SafeFireAndForget(SaveGeneralSettingsAsync());
    }

    partial void OnResearchProviderIdChanged(Guid? value)
    {
        if (!_isLoading) SafeFireAndForget(SaveGeneralSettingsAsync());
    }

    partial void OnUseSameProviderForAllModesChanged(bool value)
    {
        OnPropertyChanged(nameof(OptimizeProviderOptions));
        OnPropertyChanged(nameof(OptimizeProviderLabel));
        if (!_isLoading)
        {
            if (value && OptimizeProviderId.HasValue)
            {
                // Pia Cloud only supports Optimize — don't propagate to other modes
                var isPiaCloud = Providers.Any(p => p.Id == OptimizeProviderId && p.ProviderType == AiProviderType.PiaCloud);
                if (!isPiaCloud)
                {
                    _isLoading = true;
                    AssistantProviderId = OptimizeProviderId;
                    ResearchProviderId = OptimizeProviderId;
                    _isLoading = false;
                }
            }
            SafeFireAndForget(SaveGeneralSettingsAsync());
        }
    }

    partial void OnOutputActionChanged(OutputAction value)
    {
        if (!_isLoading) SafeFireAndForget(SaveGeneralSettingsAsync());
    }

    partial void OnAutoTypeDelayMsChanged(int value)
    {
        if (!_isLoading) SafeFireAndForget(SaveGeneralSettingsAsync());
    }

    partial void OnWhisperModelChanged(WhisperModelSize value)
    {
        if (!_isLoading) SafeFireAndForget(SaveGeneralSettingsAsync());
    }

    partial void OnStartMinimizedChanged(bool value)
    {
        if (!_isLoading) SafeFireAndForget(SaveGeneralSettingsAsync());
    }

    partial void OnShowTodoPanelButtonChanged(bool value)
    {
        if (!_isLoading) SafeFireAndForget(SaveGeneralSettingsAsync());
    }

    partial void OnDefaultWindowModeChanged(WindowMode value)
    {
        if (!_isLoading) SafeFireAndForget(SaveGeneralSettingsAsync());
    }

    partial void OnTargetSpeechLanguageChanged(TargetSpeechLanguage value)
    {
        if (!_isLoading) SafeFireAndForget(SaveGeneralSettingsAsync());
    }

    partial void OnUiLanguageChanged(TargetLanguage value)
    {
        if (!_isLoading)
        {
            _localizationService.SetLanguage(value);
            SafeFireAndForget(SaveGeneralSettingsAsync());
        }
    }

    [RelayCommand]
    private async Task CaptureOptimizeHotkeyAsync()
    {
        var shortcut = await _dialogService.ShowHotkeyCaptureDialogAsync();
        if (shortcut != null && !HasInternalConflict(shortcut, WindowMode.Optimize))
        {
            _optimizeHotkey = shortcut;
            OptimizeHotkeyDisplayText = shortcut.DisplayText;
            await SaveGeneralSettingsAsync();
            _trayIconService.UpdateHotkey(WindowMode.Optimize, _optimizeHotkey);
        }
    }

    [RelayCommand]
    private async Task CaptureAssistantHotkeyAsync()
    {
        var shortcut = await _dialogService.ShowHotkeyCaptureDialogAsync();
        if (shortcut != null && !HasInternalConflict(shortcut, WindowMode.Assistant))
        {
            _assistantHotkey = shortcut;
            AssistantHotkeyDisplayText = shortcut.DisplayText;
            await SaveGeneralSettingsAsync();
            _trayIconService.UpdateHotkey(WindowMode.Assistant, _assistantHotkey);
        }
    }

    [RelayCommand]
    private async Task CaptureResearchHotkeyAsync()
    {
        var shortcut = await _dialogService.ShowHotkeyCaptureDialogAsync();
        if (shortcut != null && !HasInternalConflict(shortcut, WindowMode.Research))
        {
            _researchHotkey = shortcut;
            ResearchHotkeyDisplayText = shortcut.DisplayText;
            await SaveGeneralSettingsAsync();
            _trayIconService.UpdateHotkey(WindowMode.Research, _researchHotkey);
        }
    }

    [RelayCommand]
    private async Task ClearOptimizeHotkeyAsync()
    {
        _optimizeHotkey = KeyboardShortcut.DefaultCtrlAltO();
        OptimizeHotkeyDisplayText = _optimizeHotkey.DisplayText;
        await SaveGeneralSettingsAsync();
        _trayIconService.UpdateHotkey(WindowMode.Optimize, _optimizeHotkey);
    }

    [RelayCommand]
    private async Task ClearAssistantHotkeyAsync()
    {
        _assistantHotkey = KeyboardShortcut.DefaultCtrlAltP();
        AssistantHotkeyDisplayText = _localizationService["Msg_Settings_HotkeyNotSet"];
        await SaveGeneralSettingsAsync();
        _trayIconService.UpdateHotkey(WindowMode.Assistant, null);
    }

    [RelayCommand]
    private async Task ClearResearchHotkeyAsync()
    {
        _researchHotkey = KeyboardShortcut.DefaultCtrlAltR();
        ResearchHotkeyDisplayText = _localizationService["Msg_Settings_HotkeyNotSet"];
        await SaveGeneralSettingsAsync();
        _trayIconService.UpdateHotkey(WindowMode.Research, null);
    }

    private bool HasInternalConflict(KeyboardShortcut shortcut, WindowMode targetMode)
    {
        var allHotkeys = new Dictionary<WindowMode, KeyboardShortcut?>
        {
            { WindowMode.Optimize, _optimizeHotkey },
            { WindowMode.Assistant, _assistantHotkey },
            { WindowMode.Research, _researchHotkey }
        };

        foreach (var (mode, existing) in allHotkeys)
        {
            if (mode == targetMode || existing is null)
                continue;

            if (existing.Modifiers == shortcut.Modifiers && existing.VirtualKeyCode == shortcut.VirtualKeyCode)
            {
                _snackbarService.Show(_localizationService["Msg_Settings_Conflict"], _localizationService.Format("Msg_Settings_HotkeyAlreadyAssigned", mode), Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
                return true;
            }
        }

        return false;
    }

    [RelayCommand]
    private async Task AddProviderAsync()
    {
        var editModel = new ProviderEditModel();

        if (await _dialogService.ShowProviderEditDialogAsync(editModel, _providerService))
        {
            var savedProvider = await _providerService.AddProviderAsync(editModel.ToProvider(), editModel.ApiKey);
            await RefreshProvidersAsync();

            // Auto-test connection for the newly added provider
            var providerToTest = Providers.FirstOrDefault(p => p.Id == savedProvider.Id);
            if (providerToTest != null)
                SafeFireAndForget(TestConnectionAsync(providerToTest));
        }
    }

    [RelayCommand]
    private async Task EditProviderAsync(AiProvider? provider)
    {
        if (provider is null)
            return;

        var editModel = ProviderEditModel.FromProvider(provider);

        if (await _dialogService.ShowProviderEditDialogAsync(editModel, _providerService))
        {
            await _providerService.UpdateProviderAsync(editModel.ToProvider(), editModel.ApiKey);
            await RefreshProvidersAsync();

            // Auto-test connection for the updated provider
            var providerToTest = Providers.FirstOrDefault(p => p.Id == provider.Id);
            if (providerToTest != null)
                SafeFireAndForget(TestConnectionAsync(providerToTest));
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteProvider))]
    private async Task DeleteProviderAsync(AiProvider? provider)
    {
        if (provider is null)
            return;

        var isUsedByAnyMode = OptimizeProviderId == provider.Id
            || AssistantProviderId == provider.Id
            || ResearchProviderId == provider.Id;

        if (isUsedByAnyMode)
        {
            _snackbarService.Show(_localizationService["Msg_Warning"], _localizationService["Msg_Settings_CannotDeleteAssignedProvider"], Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
            return;
        }

        await _providerService.DeleteProviderAsync(provider.Id);
        await RefreshProvidersAsync();
    }

    [RelayCommand(CanExecute = nameof(CanTestConnection))]
    private async Task TestConnectionAsync(AiProvider? provider)
    {
        if (provider is null)
            return;

        TestingProviderId = provider.Id;

        try
        {
            var result = await _providerService.TestConnectionAsync(provider);

            if (result.SupportsToolCalling && result.SupportsStreaming)
            {
                _snackbarService.Show(_localizationService["Msg_Success"], _localizationService["Msg_Settings_ConnectionSuccess"], Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
            }
            else if (!result.SupportsToolCalling && !result.SupportsStreaming)
            {
                _snackbarService.Show(_localizationService["Msg_Success"], _localizationService["Msg_Settings_ConnectionSuccessNoToolsNoStreaming"], Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(6));
            }
            else if (!result.SupportsToolCalling)
            {
                _snackbarService.Show(_localizationService["Msg_Success"], _localizationService["Msg_Settings_ConnectionSuccessNoTools"], Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(6));
            }
            else
            {
                _snackbarService.Show(_localizationService["Msg_Success"], _localizationService["Msg_Settings_ConnectionSuccessNoStreaming"], Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(6));
            }

            await RefreshProvidersAsync();
        }
        catch (Exception ex)
        {
            _snackbarService.Show(_localizationService["Msg_Error"], _localizationService.Format("Msg_Settings_ConnectionFailed", ex.Message), Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(3));
        }
        finally
        {
            TestingProviderId = null;
        }
    }

    private bool CanTestConnection(AiProvider? provider)
    {
        if (provider is null) return false;
        if (provider.ProviderType == AiProviderType.PiaCloud && !IsSyncLoggedIn)
            return false;
        return true;
    }

    private async Task RefreshProvidersAsync()
    {
        var savedOptimizeId = OptimizeProviderId;
        var savedAssistantId = AssistantProviderId;
        var savedResearchId = ResearchProviderId;

        _isLoading = true;
        Providers.Clear();
        var providersList = await _providerService.GetProvidersAsync();
        foreach (var provider in providersList)
            Providers.Add(provider);

        OptimizeProviderId = savedOptimizeId;
        AssistantProviderId = savedAssistantId;
        ResearchProviderId = savedResearchId;
        _isLoading = false;

        await RefreshProviderDisplayItemsAsync();
    }

    private async Task RefreshProviderDisplayItemsAsync()
    {
        ProviderDisplayItems.Clear();
        foreach (var provider in Providers)
        {
            var isActive = await _providerService.IsProviderActiveAsync(provider);
            var isDefault = OptimizeProviderId == provider.Id
                || AssistantProviderId == provider.Id
                || ResearchProviderId == provider.Id;

            string? failReason = null;
            if (!isActive && isDefault)
            {
                failReason = provider.ProviderType == AiProviderType.PiaCloud
                    ? _localizationService["Providers_NotConnected"]
                    : _localizationService["Providers_NotConfigured"];
            }

            ProviderDisplayItems.Add(new ProviderDisplayItem
            {
                Provider = provider,
                IsActive = isActive,
                IsDefaultForAnyMode = isDefault,
                FailReason = failReason,
            });
        }
    }

    private async Task RefreshTemplatesAsync()
    {
        Templates.Clear();
        var templatesList = await _templateService.GetTemplatesAsync();
        foreach (var template in templatesList)
            Templates.Add(template);
    }

    private bool CanDeleteProvider(AiProvider? provider)
    {
        if (provider is null) return false;
        if (provider.ProviderType == AiProviderType.PiaCloud) return false;
        return provider.Id != OptimizeProviderId
            && provider.Id != AssistantProviderId
            && provider.Id != ResearchProviderId;
    }

    [RelayCommand]
    private async Task AddTemplateAsync()
    {
        var editModel = new TemplateEditModel(_textOptimizationService);

        if (await _dialogService.ShowTemplateEditDialogAsync(editModel))
        {
            await _templateService.AddTemplateAsync(editModel.ToTemplate());
            await RefreshTemplatesAsync();
            _snackbarService.Show(_localizationService["Msg_Success"], _localizationService["Msg_Settings_TemplateAdded"], Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
        }
    }

    [RelayCommand]
    private async Task ViewTemplatePromptAsync(OptimizationTemplate? template)
    {
        if (template is null)
            return;

        var prompt = template.IsBuiltIn
            ? template.Prompt
            : _localizationService["Msg_Settings_CustomTemplatePromptInfo"];

        await _dialogService.ShowMessageDialogAsync(template.Name, prompt);
    }

    [RelayCommand]
    private async Task EditTemplateAsync(OptimizationTemplate? template)
    {
        if (template is null || template.IsBuiltIn)
            return;

        var editModel = TemplateEditModel.FromTemplate(template, _textOptimizationService);

        if (await _dialogService.ShowTemplateEditDialogAsync(editModel))
        {
            await _templateService.UpdateTemplateAsync(editModel.ToTemplate());
            await RefreshTemplatesAsync();
            _snackbarService.Show(_localizationService["Msg_Success"], _localizationService["Msg_Settings_TemplateUpdated"], Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteTemplate))]
    private async Task DeleteTemplateAsync(OptimizationTemplate? template)
    {
        if (template is null)
            return;

        if (template.IsBuiltIn)
        {
            _snackbarService.Show(_localizationService["Msg_Warning"], _localizationService["Msg_Settings_CannotDeleteBuiltInTemplate"], Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
            return;
        }

        if (template.Id == DefaultTemplateId)
        {
            _snackbarService.Show(_localizationService["Msg_Warning"], _localizationService["Msg_Settings_CannotDeleteDefaultTemplate"], Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
            return;
        }

        await _templateService.DeleteTemplateAsync(template.Id);
        await RefreshTemplatesAsync();
        _snackbarService.Show(_localizationService["Msg_Success"], _localizationService["Msg_Settings_TemplateDeleted"], Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
    }

    [RelayCommand]
    private async Task SetDefaultTemplateAsync(OptimizationTemplate? template)
    {
        if (template is null)
            return;

        _isLoading = true;
        try
        {
            DefaultTemplateId = template.Id;
            await SaveGeneralSettingsAsync();
        }
        finally
        {
            _isLoading = false;
        }
    }

    private bool CanDeleteTemplate(OptimizationTemplate? template)
    {
        return template != null && !template.IsBuiltIn && template.Id != DefaultTemplateId;
    }

    [RelayCommand]
    private async Task DownloadWhisperModelAsync()
    {
        var modelName = Services.TranscriptionService.GetModelName(WhisperModel);

        var downloadCancellationToken = new CancellationTokenSource();
        var progress = new Progress<ModelDownloadProgress>();

        try
        {
            var downloadTask = _transcriptionService.DownloadModelAsync(WhisperModel, progress, downloadCancellationToken.Token);
            var dialogTask = _dialogService.ShowModelDownloadDialogAsync(modelName, progress, downloadCancellationToken.Token);

            var completedTask = await Task.WhenAny(downloadTask, dialogTask);
            var wasCancelled = downloadCancellationToken.Token.IsCancellationRequested;

            if (wasCancelled)
            {
                _snackbarService.Show(_localizationService["Msg_Cancelled"], _localizationService["Msg_Settings_ModelDownloadCancelled"], Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
            }
            else
            {
                await downloadTask;
                _snackbarService.Show(_localizationService["Msg_Success"], _localizationService["Msg_Settings_ModelDownloadCompleted"], Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
            }
        }
        catch (Exception ex)
        {
            _snackbarService.Show(_localizationService["Msg_Error"], _localizationService.Format("Msg_Settings_ModelDownloadFailed", ex.Message), Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(3));
        }
        finally
        {
            downloadCancellationToken?.Dispose();
        }
    }

    /// <summary>
    /// After a successful login, check if the account has E2EE enabled.
    /// If so and UMK is not available locally, trigger the onboarding flow
    /// instead of starting sync.
    /// </summary>
    private async Task HandlePostLoginAsync()
    {
        UpdateSyncState();

        var e2eeStatus = await _deviceManagement.CheckE2EEStatusAsync();
        if (e2eeStatus is { IsEnabled: true } && !_deviceManagement.IsInitialized())
        {
            // E2EE is enabled on the account but this device lacks the UMK.
            // Trigger onboarding instead of sync.
            _logger.LogInformation("E2EE enabled on account but UMK not available; onboarding required");
            IsE2EEOnboardingRequired = true;
            return;
        }

        await _syncClientService.PerformFirstSyncMigrationAsync();
        _syncClientService.StartBackgroundSync();
    }

    [RelayCommand]
    private async Task LoginWithGoogleAsync()
    {
        IsSyncLoggingIn = true;
        try
        {
            // Save server URL first (only in dev mode; production uses hardcoded URL)
            if (IsDevMode)
            {
                var settings = await _settingsService.GetSettingsAsync();
                settings.ServerUrl = ServerUrl;
                await _settingsService.SaveSettingsAsync(settings);
            }

            var (success, errorMessage) = await _authService.LoginAsync("google");
            if (success)
            {
                await HandlePostLoginAsync();
            }
            else if (errorMessage is not null)
            {
                LoginErrorMessage = errorMessage;
            }
        }
        finally
        {
            IsSyncLoggingIn = false;
        }
    }

    [RelayCommand]
    private async Task LoginWithMicrosoftAsync()
    {
        IsSyncLoggingIn = true;
        try
        {
            if (IsDevMode)
            {
                var settings = await _settingsService.GetSettingsAsync();
                settings.ServerUrl = ServerUrl;
                await _settingsService.SaveSettingsAsync(settings);
            }

            var (success, errorMessage) = await _authService.LoginAsync("microsoft");
            if (success)
            {
                await HandlePostLoginAsync();
            }
            else if (errorMessage is not null)
            {
                LoginErrorMessage = errorMessage;
            }
        }
        finally
        {
            IsSyncLoggingIn = false;
        }
    }

    [RelayCommand]
    private async Task LoginWithPasswordAsync()
    {
        if (string.IsNullOrWhiteSpace(LoginEmail) || string.IsNullOrWhiteSpace(LoginPassword))
        {
            LoginErrorMessage = _localizationService["Sync_LocalAuth_FieldsRequired"];
            return;
        }

        IsSyncLoggingIn = true;
        LoginErrorMessage = null;
        try
        {
            if (IsDevMode)
            {
                var settings = await _settingsService.GetSettingsAsync();
                settings.ServerUrl = ServerUrl;
                await _settingsService.SaveSettingsAsync(settings);
            }

            var (success, errorMessage) = await _authService.LoginWithPasswordAsync(LoginEmail, LoginPassword);
            if (success)
            {
                LoginPassword = "";
                await HandlePostLoginAsync();
            }
            else
            {
                LoginErrorMessage = errorMessage ?? _localizationService["Sync_LocalAuth_InvalidCredentials"];
            }
        }
        finally
        {
            IsSyncLoggingIn = false;
        }
    }

    [RelayCommand]
    private void OpenRegistrationPage()
    {
        var serverUrl = ServerUrl?.TrimEnd('/');
        if (string.IsNullOrEmpty(serverUrl))
        {
            LoginErrorMessage = _localizationService["Sync_LocalAuth_ServerUrlRequired"];
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"{serverUrl}/auth/register.html") { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenForgotPassword()
    {
        var serverUrl = ServerUrl?.TrimEnd('/');
        if (string.IsNullOrEmpty(serverUrl))
        {
            LoginErrorMessage = _localizationService["Sync_LocalAuth_ServerUrlRequired"];
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"{serverUrl}/auth/forgot-password.html") { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task SyncLogoutAsync()
    {
        _syncClientService.StopBackgroundSync();
        await _authService.LogoutAsync();
        UpdateSyncState();
    }

    private async Task HandlePendingDevicesAsync(List<DeviceInfo> pendingDevices)
    {
        foreach (var device in pendingDevices)
        {
            try
            {
                var fingerprint = _deviceKeys.ComputeFingerprint(device.AgreementPublicKey);
                var message = $"A new device wants to join your account.\n\n" +
                    $"Device: {device.DeviceName}\n" +
                    $"Fingerprint: {fingerprint}\n\n" +
                    $"Verify this fingerprint matches what is shown on the other device before approving.\n\n" +
                    $"Do you want to approve this device?";

                var approved = await _dialogService.ShowConfirmationDialogAsync(
                    "New Device Requesting Access", message);

                if (approved && device.OnboardingSessionId is not null)
                {
                    device.Fingerprint = fingerprint;
                    await _deviceManagement.ApproveDeviceAsync(
                        device.OnboardingSessionId, device);
                    _snackbarService.Show("Device Approved",
                        $"{device.DeviceName} has been approved and can now sync.",
                        Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(4));
                }
                else if (!approved)
                {
                    var reject = await _dialogService.ShowConfirmationDialogAsync(
                        "Reject Device?",
                        $"Do you want to reject and revoke {device.DeviceName}? " +
                        "If you don't recognize this device, you should revoke it.");
                    if (reject)
                    {
                        await _deviceManagement.RevokeDeviceAsync(device.DeviceId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to handle pending device {DeviceId}", device.DeviceId);
                _snackbarService.Show("Error", $"Failed to approve device: {ex.Message}",
                    Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(4));
            }
        }
    }

    [RelayCommand]
    private async Task CheckForPendingDevicesAsync()
    {
        try
        {
            var response = await _deviceManagement.GetDevicesAsync();
            var pending = response.Devices
                .Where(d => d.Status == DeviceStatus.Pending && d.OnboardingSessionId is not null)
                .ToList();

            if (pending.Count > 0)
            {
                await HandlePendingDevicesAsync(pending);
            }
            else
            {
                _snackbarService.Show("No Requests", "No pending device requests found.",
                    Wpf.Ui.Controls.ControlAppearance.Info, null, TimeSpan.FromSeconds(3));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check for pending devices");
            _snackbarService.Show("Error", "Failed to check for pending devices.",
                Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(4));
        }
    }

    private void UpdateSyncState()
    {
        IsSyncLoggedIn = _authService.IsLoggedIn;
        SyncUserEmail = _authService.UserEmail;
        SyncUserDisplayName = _authService.UserDisplayName;
        SyncProvider = _authService.Provider;
    }

    partial void OnIsE2EEEnabledChanged(bool value)
    {
        if (_isLoading) return;

        if (value)
            _ = EnableE2EEAsync();
        else
            _ = DisableE2EEAsync();
    }

    partial void OnTrustSelfSignedCertificatesChanged(bool value)
    {
        if (!_isLoading) SafeFireAndForget(SaveSyncSettingsAsync());
    }

    private async Task SaveSyncSettingsAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        if (IsDevMode)
        {
            settings.TrustSelfSignedCertificates = TrustSelfSignedCertificates;
            settings.ServerUrl = ServerUrl;
        }
        await _settingsService.SaveSettingsAsync(settings);
    }

    [RelayCommand]
    private async Task EnableE2EEAsync()
    {
        try
        {
            CanToggleE2EE = false;

            // Stop any in-progress sync and wait for it to finish
            await _syncClientService.StopBackgroundSyncAndWaitAsync();

            // Always generate a new key: use full bootstrap if device keys
            // don't exist yet, otherwise re-key with a fresh UMK
            string recoveryCode;
            if (!_deviceKeys.HasDeviceKeys())
                recoveryCode = await _deviceManagement.BootstrapFirstDeviceAsync();
            else
                recoveryCode = await _deviceManagement.ReKeyAsync();

            DeviceFingerprint = _deviceKeys.GetFingerprint();

            // Show recovery code to user with copy option
            var copyRequested = await _dialogService.ShowMessageWithCopyDialogAsync(
                "Recovery Code",
                $"Save this recovery code in a safe place. It is the ONLY way to recover your encrypted data if you lose all devices.\n\n{recoveryCode}\n\nIf you lose this code and all your devices, your encrypted data cannot be recovered.");
            if (copyRequested)
                System.Windows.Clipboard.SetText(recoveryCode);

            // Force full sync to re-encrypt all data with the new key
            await _syncClientService.PerformFirstSyncMigrationAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enable E2EE");
            _isLoading = true;
            IsE2EEEnabled = false;
            _isLoading = false;

            // Check if this failed because E2EE is already enabled on other devices —
            // if so, show onboarding instead of the error toggle
            var serverStatus = await _deviceManagement.CheckE2EEStatusAsync();
            if (serverStatus is { IsEnabled: true })
            {
                IsE2EEOnboardingRequired = true;
            }
            else
            {
                _snackbarService.Show("Error", $"Failed to enable E2EE: {ex.Message}",
                    Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            _syncClientService.StartBackgroundSync();
            CanToggleE2EE = true;
        }
    }

    private async Task DisableE2EEAsync()
    {
        try
        {
            CanToggleE2EE = false;

            // Stop any in-progress sync and wait for it to finish
            await _syncClientService.StopBackgroundSyncAndWaitAsync();

            // Persist disabled state
            var settings = await _settingsService.GetSettingsAsync();
            settings.IsE2EEEnabled = false;
            await _settingsService.SaveSettingsAsync(settings);

            // Force full sync to re-store all data as plaintext on the server
            await _syncClientService.PerformFirstSyncMigrationAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disable E2EE");
            _snackbarService.Show("Error", $"Failed to disable encryption: {ex.Message}",
                Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(5));
        }
        finally
        {
            _syncClientService.StartBackgroundSync();
            CanToggleE2EE = true;
        }
    }

    private async Task LoadTtsVoicesAsync()
    {
        var voices = await _ttsService.GetAvailableVoicesAsync();
        TtsVoices.Clear();
        foreach (var voice in voices)
        {
            voice.IsSelected = voice.Key == SelectedVoiceKey;
            TtsVoices.Add(voice);
        }
    }

    [RelayCommand]
    private async Task DownloadVoiceAsync(TtsVoice? voice)
    {
        if (voice is null || voice.IsDownloaded || voice.IsDownloading)
            return;

        voice.IsDownloading = true;
        voice.DownloadProgress = 0;

        try
        {
            var progress = new Progress<TtsDownloadProgress>(p =>
            {
                voice.DownloadProgress = p.PercentComplete;
            });

            await _ttsService.DownloadVoiceAsync(voice.Key, progress);
            voice.IsDownloaded = true;
            _snackbarService.Show(_localizationService["Msg_Success"], _localizationService.Format("Msg_Settings_VoiceDownloaded", voice.DisplayName), Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download voice {VoiceKey}", voice.Key);
            _snackbarService.Show(_localizationService["Msg_Error"], _localizationService.Format("Msg_Settings_VoiceDownloadFailed", ex.Message), Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(3));
        }
        finally
        {
            voice.IsDownloading = false;
        }
    }

    [RelayCommand]
    private async Task SelectVoiceAsync(TtsVoice? voice)
    {
        if (voice is null || !voice.IsDownloaded)
            return;

        foreach (var v in TtsVoices)
            v.IsSelected = false;

        voice.IsSelected = true;
        SelectedVoiceKey = voice.Key;

        try
        {
            await _ttsService.SetVoiceAsync(voice.Key);
            _snackbarService.Show(_localizationService["Msg_Settings_VoiceChanged"], _localizationService.Format("Msg_Settings_NowUsingVoice", voice.DisplayName), Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set voice {VoiceKey}", voice.Key);
            _snackbarService.Show(_localizationService["Msg_Error"], _localizationService.Format("Msg_Settings_VoiceSetFailed", ex.Message), Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(3));
        }
    }

    [RelayCommand]
    private async Task ResetAppDataAsync()
    {
        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            _localizationService["Settings_ResetAppData_Confirm_Title"],
            _localizationService["Settings_ResetAppData_Confirm_Message"]);

        if (!confirmed)
            return;

        try
        {
            // Stop background sync before deleting data
            _syncClientService.StopBackgroundSync();

            var roamingDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pia");
            var localDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pia");

            // Delete both AppData directories
            if (Directory.Exists(roamingDir))
                Directory.Delete(roamingDir, recursive: true);
            if (Directory.Exists(localDir))
                Directory.Delete(localDir, recursive: true);

            // Restart the application
            var exePath = Environment.ProcessPath;
            if (exePath is not null)
            {
                System.Diagnostics.Process.Start(exePath);
                System.Windows.Application.Current.Shutdown();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset application data");
            _snackbarService.Show(
                _localizationService["Msg_Error"],
                ex.Message,
                Wpf.Ui.Controls.ControlAppearance.Danger,
                null,
                TimeSpan.FromSeconds(5));
        }
    }

    private async Task SaveGeneralSettingsAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        settings.DefaultTemplateId = DefaultTemplateId;
        settings.UseSameProviderForAllModes = UseSameProviderForAllModes;
        settings.ModeProviderDefaults.Clear();
        if (OptimizeProviderId.HasValue)
            settings.SetProviderForMode(WindowMode.Optimize, OptimizeProviderId);
        if (AssistantProviderId.HasValue)
            settings.SetProviderForMode(WindowMode.Assistant, AssistantProviderId);
        if (ResearchProviderId.HasValue)
            settings.SetProviderForMode(WindowMode.Research, ResearchProviderId);
        settings.DefaultProviderId = null;
        settings.DefaultOutputAction = OutputAction;
        settings.AutoTypeDelayMs = AutoTypeDelayMs;
        settings.WhisperModel = WhisperModel;
        settings.StartMinimized = StartMinimized;
        settings.ShowTodoPanelButton = ShowTodoPanelButton;
        settings.DefaultWindowMode = DefaultWindowMode;
        settings.OptimizeHotkey = _optimizeHotkey;
        settings.AssistantHotkey = _assistantHotkey;
        settings.ResearchHotkey = _researchHotkey;
        settings.TargetSpeechLanguage = TargetSpeechLanguage;
        settings.UiLanguage = UiLanguage;

        settings.Privacy.TokenizationEnabled = TokenizationEnabled;
        settings.Privacy.PiiKeywords = PiiKeywords.Select(e => new PiiKeywordEntry { Keyword = e.Keyword, Category = e.Category }).ToList();

        await _settingsService.SaveSettingsAsync(settings);
    }
}
