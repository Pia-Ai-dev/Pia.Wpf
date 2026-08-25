using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;
using System.Collections.ObjectModel;

namespace Pia.ViewModels;

public partial class ProvidersSettingsViewModel : UiThreadViewModel, IDisposable
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly IProviderService _providerService;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogService;
    private readonly Wpf.Ui.ISnackbarService _snackbarService;
    private readonly IAuthService _authService;
    private readonly ILocalizationService _localizationService;
    private readonly IPolicyService _policyService;

    /// <summary>Bind IsEnabled to Policy[nameof(AppSettings.X)] to grey a control out while policy enforces it.</summary>
    public PolicyLock Policy { get; }
    private readonly ISyncClientService? _syncClientService;
    private bool _isLoading;
    private bool _disposed;

    private readonly SettingsViewModel _parent;

    public ProvidersSettingsViewModel(SettingsViewModel parent,
        ILogger<SettingsViewModel> logger,
        IProviderService providerService,
        ISettingsService settingsService,
        IDialogService dialogService,
        Wpf.Ui.ISnackbarService snackbarService,
        IAuthService authService,
        ILocalizationService localizationService,
        IPolicyService policyService,
        ISyncClientService? syncClientService = null)
    {
        _parent = parent;
        _logger = logger;
        _providerService = providerService;
        _settingsService = settingsService;
        _dialogService = dialogService;
        _snackbarService = snackbarService;
        _authService = authService;
        _localizationService = localizationService;
        _policyService = policyService;
        Policy = new PolicyLock(policyService);
        _syncClientService = syncClientService;

        Providers = new ObservableCollection<AiProvider>();
        Providers.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowCloudSetupBanner));
        };

        _authService.LoginStateChanged += OnLoginStateChanged;
        _providerService.ProvidersChanged += OnProvidersChanged;
        if (_syncClientService is not null)
            _syncClientService.SyncCompleted += OnSyncCompleted;
        _policyService.LocksChanged += OnLocksChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _policyService.LocksChanged -= OnLocksChanged;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        Policy.Dispose();
        GC.SuppressFinalize(this);
    }

    // Deliberately not RefreshProvidersAsync/ResolveOrDefault: on an app-wide save the configured provider
    // may not have synced down yet, and their PiaCloud fallback would strand that policy default for good.
    private void OnSettingsChanged(object? sender, AppSettings settings) => Post(() =>
    {
        _isLoading = true;

        CanManageProviders = settings.AllowProviderManagement;
        UseSameProviderForAllModes = settings.UseSameProviderForAllModes;

        if (ConfiguredIfKnown(settings, WindowMode.Optimize) is { } optimizeId)
            OptimizeProviderId = optimizeId;
        if (ConfiguredIfKnown(settings, WindowMode.Assistant) is { } assistantId)
            AssistantProviderId = assistantId;

        _isLoading = false;
    });

    private void OnSyncCompleted(object? sender, SyncCompletedEventArgs e)
    {
        if (e.ProvidersChanged || e.SettingsChanged)
            RefreshProvidersAsync().SafeFireAndForget(_logger);
    }

    private void OnLoginStateChanged(object? sender, bool isLoggedIn)
    {
        IsSyncLoggedIn = isLoggedIn;
        if (isLoggedIn)
            RefreshProvidersAsync().SafeFireAndForget(_logger);
    }

    private void OnProvidersChanged(object? sender, EventArgs e)
    {
        RefreshProvidersAsync().SafeFireAndForget(_logger);
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
    private bool _useSameProviderForAllModes = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTestConnectionInProgress))]
    private Guid? _testingProviderId;

    public bool IsTestConnectionInProgress => TestingProviderId.HasValue;

    // Enterprise policy enforcement
    public bool IsUseSameProviderEnforced => _policyService.IsEnforced(nameof(AppSettings.UseSameProviderForAllModes));

    // The indexer bindings are covered by PolicyLock; this getter is a separate binding target.
    private void OnLocksChanged(object? sender, EventArgs e) =>
        Post(() => OnPropertyChanged(nameof(IsUseSameProviderEnforced)));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddProviderCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditProviderCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteProviderCommand))]
    private bool _canManageProviders = true;

    public List<AiProvider> OptimizeProviderOptions => Providers.ToList();

    public List<AiProvider> NonCloudProviders =>
        Providers.Where(p => p.ProviderType != AiProviderType.PiaCloud).ToList();


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
                _isLoading = true;
                AssistantProviderId = value;
                _isLoading = false;
            }
            SaveProviderSettingsAsync().SafeFireAndForget(_logger);
        }
    }

    partial void OnAssistantProviderIdChanged(Guid? value)
    {
        if (!_isLoading) SaveProviderSettingsAsync().SafeFireAndForget(_logger);
    }

    partial void OnUseSameProviderForAllModesChanged(bool value)
    {
        OnPropertyChanged(nameof(OptimizeProviderLabel));
        if (!_isLoading)
        {
            if (value && OptimizeProviderId.HasValue)
            {
                _isLoading = true;
                AssistantProviderId = OptimizeProviderId;
                _isLoading = false;
            }
            SaveProviderSettingsAsync().SafeFireAndForget(_logger);
        }
    }

    public async Task InitializeAsync()
    {
        // Repair stale mode-default Ids BEFORE we read them, so a Guid that no
        // longer points to an existing provider (e.g. after sync reassignment)
        // is replaced with a sensible fallback rather than showing as "nothing
        // selected" in the dropdown.
        await _providerService.RepairModeDefaultsAsync();

        var providersList = await _providerService.GetProvidersAsync();
        var settings = await _settingsService.GetSettingsAsync();
        CanManageProviders = settings.AllowProviderManagement;
        var optimizeId = ResolveOrDefault(settings, WindowMode.Optimize, providersList);
        var assistantId = ResolveOrDefault(settings, WindowMode.Assistant, providersList);
        var displayItems = await BuildProviderDisplayItemsAsync(providersList, optimizeId, assistantId);

        await ApplyProvidersAsync(providersList, settings.UseSameProviderForAllModes, optimizeId, assistantId,
            displayItems, _authService.IsLoggedIn);

        _logger.LogInformation(
            "Settings page initialized: providers={Count}, modeDefaults Optimize={OptId} Assistant={AsstId}, useSame={UseSame}",
            providersList.Count, optimizeId, assistantId, settings.UseSameProviderForAllModes);
    }

    private Guid? ConfiguredIfKnown(AppSettings settings, WindowMode mode) =>
        settings.ModeProviderDefaults.TryGetValue(mode, out var configured)
        && Providers.Any(p => p.Id == configured)
            ? configured
            : null;

    private static Guid? ResolveOrDefault(
        AppSettings settings, WindowMode mode, IReadOnlyList<AiProvider> providers)
    {
        if (settings.ModeProviderDefaults.TryGetValue(mode, out var configured)
            && providers.Any(p => p.Id == configured))
        {
            return configured;
        }

        var piaCloud = providers.FirstOrDefault(p => p.Id == ProviderService.PiaCloudProviderId);
        if (piaCloud is not null) return piaCloud.Id;

        return providers.FirstOrDefault()?.Id;
    }

    [RelayCommand]
    private void GoToCloudSync() => _parent.SelectedTabIndex = (int)SettingsTab.Account;

    [RelayCommand]
    private void GoToProvidersTab() => _parent.SelectedTabIndex = (int)SettingsTab.Providers;

    [RelayCommand(CanExecute = nameof(CanManageProviders))]
    private async Task AddProviderAsync()
    {
        // CanExecute only greys the button out; ExecuteAsync does not consult it.
        if (!CanManageProviders)
            return;

        var editModel = new ProviderEditModel();

        if (await _dialogService.ShowProviderEditDialogAsync(editModel, _providerService))
        {
            var savedProvider = await _providerService.AddProviderAsync(editModel.ToProvider(), editModel.ApiKey);
            await RefreshProvidersAsync();

            var providerToTest = Providers.FirstOrDefault(p => p.Id == savedProvider.Id);
            if (providerToTest != null)
                TestConnectionAsync(providerToTest).SafeFireAndForget(_logger);
        }
    }

    [RelayCommand(CanExecute = nameof(CanManageProviders))]
    private async Task EditProviderAsync(AiProvider? provider)
    {
        if (provider is null)
            return;

        if (!CanManageProviders)
            return;

        var editModel = ProviderEditModel.FromProvider(provider);

        if (await _dialogService.ShowProviderEditDialogAsync(editModel, _providerService))
        {
            await _providerService.UpdateProviderAsync(editModel.ToProvider(), editModel.ApiKey);
            await RefreshProvidersAsync();

            var providerToTest = Providers.FirstOrDefault(p => p.Id == provider.Id);
            if (providerToTest != null)
                TestConnectionAsync(providerToTest).SafeFireAndForget(_logger);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteProvider))]
    private async Task DeleteProviderAsync(AiProvider? provider)
    {
        if (provider is null)
            return;

        if (!CanManageProviders)
            return;

        var isUsedByAnyMode = OptimizeProviderId == provider.Id
            || AssistantProviderId == provider.Id;

        if (isUsedByAnyMode)
        {
            _snackbarService.Show(_localizationService["Msg_Warning"], _localizationService["Msg_Settings_CannotDeleteAssignedProvider"], Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
            return;
        }

        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            _localizationService["Msg_Settings_DeleteProviderTitle"],
            _localizationService.Format("Msg_Settings_DeleteProviderConfirm", provider.Name));

        if (!confirmed)
            return;

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
        if (!CanManageProviders) return false;
        if (provider.ProviderType == AiProviderType.PiaCloud) return false;
        return provider.Id != OptimizeProviderId
            && provider.Id != AssistantProviderId;
    }

    public async Task RefreshProvidersAsync()
    {
        // Reachable from OnProvidersChanged / OnSyncCompleted / OnLoginStateChanged, which the sync
        // loop can raise on a background thread. Fetch and compute everything off-thread, then
        // marshal all VM-state mutation onto the captured UI context via ApplyProviders.
        var providersList = await _providerService.GetProvidersAsync();

        // Re-resolve through ResolveOrDefault rather than restoring the previous
        // in-memory Ids — those may now be stale (e.g. just reassigned by a sync
        // pull that called ReassignProviderIdAsync under us).
        var settings = await _settingsService.GetSettingsAsync();
        var optimizeId = ResolveOrDefault(settings, WindowMode.Optimize, providersList);
        var assistantId = ResolveOrDefault(settings, WindowMode.Assistant, providersList);
        var displayItems = await BuildProviderDisplayItemsAsync(providersList, optimizeId, assistantId);

        await ApplyProvidersAsync(providersList, settings.UseSameProviderForAllModes, optimizeId, assistantId,
            displayItems, IsSyncLoggedIn);
    }

    // Marshals every VM-state mutation onto the UI thread in one batch. _isLoading brackets the
    // observable-property assignments so their change handlers skip the debounced save during a
    // refresh (as before); the bound-collection mutations must run here to avoid the cross-thread
    // CollectionView exception.
    private Task ApplyProvidersAsync(
        IReadOnlyList<AiProvider> providersList, bool useSameProviderForAllModes,
        Guid? optimizeId, Guid? assistantId, IReadOnlyList<ProviderDisplayItem> displayItems,
        bool isSyncLoggedIn) => PostAsync(() =>
    {
        _isLoading = true;

        Providers.Clear();
        foreach (var provider in providersList)
            Providers.Add(provider);

        ProviderDisplayItems.Clear();
        foreach (var item in displayItems)
            ProviderDisplayItems.Add(item);

        // Set the SelectedValue-bound ids AFTER ItemsSource is populated. Setting a ComboBox's
        // SelectedValue (SelectedValuePath=Id) while its items are empty leaves it unmatched, and a
        // two-way binding then writes null back onto the property — the original ordering avoided this.
        UseSameProviderForAllModes = useSameProviderForAllModes;
        OptimizeProviderId = optimizeId;
        AssistantProviderId = assistantId;
        IsSyncLoggedIn = isSyncLoggedIn;

        _isLoading = false;
    });

    // Computes the display-item list off any thread (does IO via IsProviderActiveAsync) from the
    // supplied provider list — NOT the bound Providers collection, which must not be read while a
    // background refresh may be mutating it. The caller marshals the actual mutation via Post.
    private async Task<List<ProviderDisplayItem>> BuildProviderDisplayItemsAsync(
        IReadOnlyList<AiProvider> providers, Guid? optimizeId, Guid? assistantId)
    {
        var items = new List<ProviderDisplayItem>();
        foreach (var provider in providers)
        {
            var isActive = await _providerService.IsProviderActiveAsync(provider);
            var isDefault = optimizeId == provider.Id || assistantId == provider.Id;

            string? failReason = null;
            if (!isActive && isDefault)
            {
                failReason = provider.ProviderType == AiProviderType.PiaCloud
                    ? _localizationService["Providers_NotConnected"]
                    : _localizationService["Providers_NotConfigured"];
            }

            items.Add(new ProviderDisplayItem
            {
                Provider = provider,
                IsActive = isActive,
                IsDefaultForAnyMode = isDefault,
                FailReason = failReason,
            });
        }
        return items;
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
        settings.DefaultProviderId = null;
        await _settingsService.SaveSettingsAsync(settings);
    }

}
