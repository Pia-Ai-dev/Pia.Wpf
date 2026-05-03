using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

public partial class E2EESetupStepViewModel : ObservableObject
{
    private readonly IDeviceManagementService _deviceMgmt;
    private readonly IDeviceKeyService _deviceKeys;
    private readonly ISyncClientService _syncService;
    private readonly IOutputService _outputService;
    private readonly ILogger<E2EESetupStepViewModel> _logger;

    [ObservableProperty]
    private E2EESetupState _state = E2EESetupState.Choice;

    [ObservableProperty]
    private bool _shouldEnableE2EE = true;

    [ObservableProperty]
    private string? _recoveryCode;

    [ObservableProperty]
    private bool _hasConfirmedRecoveryCode;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isBusy;

    public E2EESetupStepViewModel(
        IDeviceManagementService deviceMgmt,
        IDeviceKeyService deviceKeys,
        ISyncClientService syncService,
        IOutputService outputService,
        ILogger<E2EESetupStepViewModel> logger)
    {
        _deviceMgmt = deviceMgmt;
        _deviceKeys = deviceKeys;
        _syncService = syncService;
        _outputService = outputService;
        _logger = logger;
    }

    /// <summary>
    /// Raised when the wizard should advance to the next step.
    /// The bool indicates whether E2EE was enabled (true) or skipped (false).
    /// </summary>
    public event Action<bool>? AdvanceRequested;

    [RelayCommand]
    private async Task ProceedAsync()
    {
        switch (State)
        {
            case E2EESetupState.Choice when ShouldEnableE2EE:
                await BootstrapAsync();
                break;
            case E2EESetupState.Choice when !ShouldEnableE2EE:
                State = E2EESetupState.ConfirmingOptOut;
                break;
            case E2EESetupState.ConfirmingOptOut:
                await CompleteOptOutAsync();
                break;
            case E2EESetupState.SavingRecoveryCode when HasConfirmedRecoveryCode:
                await CompleteEnableAsync();
                break;
        }
    }

    private async Task BootstrapAsync()
    {
        try
        {
            IsBusy = true;
            ErrorMessage = null;
            State = E2EESetupState.Bootstrapping;
            _logger.LogInformation("E2EE bootstrap starting from wizard");

            var code = await _deviceMgmt.BootstrapFirstDeviceAsync();
            RecoveryCode = code;
            State = E2EESetupState.SavingRecoveryCode;
            _logger.LogInformation("E2EE bootstrap completed; awaiting recovery-code confirmation");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "E2EE bootstrap failed during wizard");
            ErrorMessage = ex.Message;
            State = E2EESetupState.Choice;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CompleteEnableAsync()
    {
        State = E2EESetupState.Completed;
        try
        {
            await _syncService.PerformFirstSyncMigrationAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "First sync after E2EE bootstrap failed in wizard");
        }
        _syncService.StartBackgroundSync();
        AdvanceRequested?.Invoke(true);
    }

    private async Task CompleteOptOutAsync()
    {
        try
        {
            await _syncService.PerformFirstSyncMigrationAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "First sync after E2EE opt-out failed in wizard");
        }
        _syncService.StartBackgroundSync();
        AdvanceRequested?.Invoke(false);
    }
}
