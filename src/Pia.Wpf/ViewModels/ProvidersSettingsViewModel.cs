using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;
using System.Collections.ObjectModel;

namespace Pia.ViewModels;

public partial class ProvidersSettingsViewModel : ObservableObject
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly IProviderService _providerService;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogService;
    private readonly Wpf.Ui.ISnackbarService _snackbarService;
    private readonly IAuthService _authService;
    private readonly ILocalizationService _localizationService;
    private bool _isLoading;

    private readonly SettingsViewModel _parent;

    public ProvidersSettingsViewModel(SettingsViewModel parent,
        ILogger<SettingsViewModel> logger,
        IProviderService providerService,
        ISettingsService settingsService,
        IDialogService dialogService,
        Wpf.Ui.ISnackbarService snackbarService,
        IAuthService authService,
        ILocalizationService localizationService)
    {
        _parent = parent;
        _logger = logger;
        _providerService = providerService;
        _settingsService = settingsService;
        _dialogService = dialogService;
        _snackbarService = snackbarService;
        _authService = authService;
        _localizationService = localizationService;

        Providers = new ObservableCollection<AiProvider>();
        Providers.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(NonCloudProviders));
            OnPropertyChanged(nameof(OptimizeProviderOptions));
            OnPropertyChanged(nameof(ShowCloudSetupBanner));
        };
    }

    [ObservableProperty]
    private ObservableCollection<AiProvider> _providers;

    public ObservableCollection<ProviderDisplayItem> ProviderDisplayItems { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    [NotifyPropertyChangedFor(nameof(ShowCloudSetupBanner))]
    private bool _isSyncLoggedIn;

    [ObservableProperty]
    private Guid? _optimizeProviderId;

    [ObservableProperty]
    private Guid? _assistantProviderId;

    [ObservableProperty]
    private Guid? _researchProviderId;

    [ObservableProperty]
    private bool _useSameProviderForAllModes = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTestConnectionInProgress))]
    private Guid? _testingProviderId;

    public bool IsTestConnectionInProgress => TestingProviderId.HasValue;

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

    partial void OnOptimizeProviderIdChanged(Guid? value)
    {
        if (!_isLoading)
        {
            if (UseSameProviderForAllModes && value.HasValue)
            {
                var isPiaCloud = Providers.Any(p => p.Id == value && p.ProviderType == AiProviderType.PiaCloud);
                if (!isPiaCloud)
                {
                    _isLoading = true;
                    AssistantProviderId = value;
                    ResearchProviderId = value;
                    _isLoading = false;
                }
            }
            SafeFireAndForget(SaveProviderSettingsAsync());
        }
    }

    partial void OnAssistantProviderIdChanged(Guid? value)
    {
        if (!_isLoading) SafeFireAndForget(SaveProviderSettingsAsync());
    }

    partial void OnResearchProviderIdChanged(Guid? value)
    {
        if (!_isLoading) SafeFireAndForget(SaveProviderSettingsAsync());
    }

    partial void OnUseSameProviderForAllModesChanged(bool value)
    {
        OnPropertyChanged(nameof(OptimizeProviderOptions));
        OnPropertyChanged(nameof(OptimizeProviderLabel));
        if (!_isLoading)
        {
            if (value && OptimizeProviderId.HasValue)
            {
                var isPiaCloud = Providers.Any(p => p.Id == OptimizeProviderId && p.ProviderType == AiProviderType.PiaCloud);
                if (!isPiaCloud)
                {
                    _isLoading = true;
                    AssistantProviderId = OptimizeProviderId;
                    ResearchProviderId = OptimizeProviderId;
                    _isLoading = false;
                }
            }
            SafeFireAndForget(SaveProviderSettingsAsync());
        }
    }

    public async Task InitializeAsync()
    {
        _isLoading = true;

        var providersList = await _providerService.GetProvidersAsync();
        foreach (var provider in providersList)
            Providers.Add(provider);

        var settings = await _settingsService.GetSettingsAsync();
        UseSameProviderForAllModes = settings.UseSameProviderForAllModes;
        OptimizeProviderId = settings.ModeProviderDefaults.TryGetValue(WindowMode.Optimize, out var optId) ? optId : null;
        AssistantProviderId = settings.ModeProviderDefaults.TryGetValue(WindowMode.Assistant, out var astId) ? astId : null;
        ResearchProviderId = settings.ModeProviderDefaults.TryGetValue(WindowMode.Research, out var resId) ? resId : null;

        IsSyncLoggedIn = _authService.IsLoggedIn;

        await RefreshProviderDisplayItemsAsync();

        _isLoading = false;
    }

    [RelayCommand]
    private void GoToCloudSync() => _parent.SelectedTabIndex = 5; // Account tab

    [RelayCommand]
    private async Task AddProviderAsync()
    {
        var editModel = new ProviderEditModel();

        if (await _dialogService.ShowProviderEditDialogAsync(editModel, _providerService))
        {
            var savedProvider = await _providerService.AddProviderAsync(editModel.ToProvider(), editModel.ApiKey);
            await RefreshProvidersAsync();

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

    private bool CanDeleteProvider(AiProvider? provider)
    {
        if (provider is null) return false;
        if (provider.ProviderType == AiProviderType.PiaCloud) return false;
        return provider.Id != OptimizeProviderId
            && provider.Id != AssistantProviderId
            && provider.Id != ResearchProviderId;
    }

    public async Task RefreshProvidersAsync()
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

    private async Task SaveProviderSettingsAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        settings.UseSameProviderForAllModes = UseSameProviderForAllModes;
        settings.ModeProviderDefaults.Clear();
        if (OptimizeProviderId.HasValue)
            settings.SetProviderForMode(WindowMode.Optimize, OptimizeProviderId);
        if (AssistantProviderId.HasValue)
            settings.SetProviderForMode(WindowMode.Assistant, AssistantProviderId);
        if (ResearchProviderId.HasValue)
            settings.SetProviderForMode(WindowMode.Research, ResearchProviderId);
        settings.DefaultProviderId = null;
        await _settingsService.SaveSettingsAsync(settings);
    }

    private async void SafeFireAndForget(Task task)
    {
        try { await task; }
        catch (Exception ex) { _logger.LogError(ex, "Background operation failed"); }
    }
}
