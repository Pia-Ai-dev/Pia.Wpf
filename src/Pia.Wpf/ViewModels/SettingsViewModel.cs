using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Navigation;

namespace Pia.ViewModels;

public partial class SettingsViewModel : ObservableObject, INavigationAware
{
    private readonly ILogger<SettingsViewModel> _logger;

    public ProvidersSettingsViewModel ProvidersVm { get; }
    public OptimizeSettingsViewModel OptimizeVm { get; }
    public AssistantSettingsViewModel AssistantVm { get; }
    public ResearchSettingsViewModel ResearchVm { get; }
    public GeneralSettingsViewModel GeneralVm { get; }
    public AccountSettingsViewModel AccountVm { get; }

    [ObservableProperty]
    private int _selectedTabIndex;

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

        ProvidersVm = new ProvidersSettingsViewModel(this, logger, providerService, settingsService, dialogService, snackbarService, authService, localizationService);

        OptimizeVm = new OptimizeSettingsViewModel(ProvidersVm, logger, templateService, settingsService, textOptimizationService, dialogService, snackbarService, localizationService);

        AssistantVm = new AssistantSettingsViewModel(ProvidersVm, logger, settingsService);

        ResearchVm = new ResearchSettingsViewModel(ProvidersVm);

        GeneralVm = new GeneralSettingsViewModel(logger, settingsService, transcriptionService, dialogService, trayIconService, ttsService, snackbarService, localizationService);

        AccountVm = new AccountSettingsViewModel(logger, settingsService, dialogService, snackbarService, authService, syncClientService, localizationService, deviceManagement, deviceKeys, onboardingViewModel);
    }

    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is int tabIndex)
            SelectedTabIndex = tabIndex;
    }

    public async Task OnNavigatedToAsync(object? parameter)
    {
        try
        {
            await ProvidersVm.InitializeAsync();
            await OptimizeVm.InitializeAsync();
            await AssistantVm.InitializeAsync();
            await GeneralVm.InitializeAsync();
            await AccountVm.InitializeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize settings");
        }
    }

    public void OnNavigatedFrom()
    {
    }
}
