using System.Net.Http;
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
    public PluginsSettingsViewModel PluginsVm { get; }
    public PersonaSettingsViewModel PersonasVm { get; }

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
        IMemoryService memoryService,
        E2EEOnboardingViewModel onboardingViewModel,
        IAutostartService autostartService,
        IPluginService pluginService,
        IPluginIconLoader pluginIconLoader,
        IPolicyService policyService,
        IPersonaService personaService,
        IAssistantChatService assistantChatService,
        IToolPermissionService toolPermissionService)
    {
        _logger = logger;

        ProvidersVm = new ProvidersSettingsViewModel(this, logger, providerService, settingsService, dialogService, snackbarService, authService, localizationService, policyService, syncClientService);

        OptimizeVm = new OptimizeSettingsViewModel(ProvidersVm, logger, templateService, settingsService, textOptimizationService, dialogService, snackbarService, localizationService, policyService, authService);

        PersonasVm = new PersonaSettingsViewModel(logger, personaService, providerService, textOptimizationService, dialogService, snackbarService, localizationService, authService);

        var toolPermissionsVm = new ToolPermissionsSettingsViewModel(toolPermissionService, pluginService, logger);
        var meetingVm = new MeetingSettingsViewModel(logger, settingsService, localizationService);
        AssistantVm = new AssistantSettingsViewModel(ProvidersVm, PersonasVm, toolPermissionsVm, meetingVm, logger, settingsService, assistantChatService, dialogService, localizationService);

        ResearchVm = new ResearchSettingsViewModel(ProvidersVm);

        GeneralVm = new GeneralSettingsViewModel(logger, settingsService, transcriptionService, dialogService, trayIconService, ttsService, snackbarService, localizationService, autostartService, policyService);

        AccountVm = new AccountSettingsViewModel(logger, settingsService, dialogService, snackbarService, authService, syncClientService, localizationService, deviceManagement, deviceKeys, memoryService, policyService, onboardingViewModel);

        PluginsVm = new PluginsSettingsViewModel(this, logger, pluginService, authService, settingsService, dialogService, localizationService, snackbarService, pluginIconLoader);
    }

    public void OnNavigatedTo(object? parameter)
    {
        switch (parameter)
        {
            case int tabIndex:
                SelectedTabIndex = tabIndex;
                break;
            case ValueTuple<int, int> tabs:
                // (outer tab, inner tab). Inner tab applies only when the outer tab
                // hosts its own TabControl — Assistant (2) and General (4).
                SelectedTabIndex = tabs.Item1;
                if (tabs.Item1 == 2)
                    AssistantVm.SelectedInnerTabIndex = tabs.Item2;
                else if (tabs.Item1 == 4)
                    GeneralVm.SelectedInnerTabIndex = tabs.Item2;
                break;
        }
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
            await PluginsVm.InitializeAsync();
            await PersonasVm.InitializeAsync();
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
