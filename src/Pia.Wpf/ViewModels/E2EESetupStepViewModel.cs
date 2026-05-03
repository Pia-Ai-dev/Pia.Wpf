using CommunityToolkit.Mvvm.ComponentModel;
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
}
