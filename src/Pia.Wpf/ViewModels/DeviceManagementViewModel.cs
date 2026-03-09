using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Services.E2EE;
using Pia.Shared.E2EE;
using Wpf.Ui;

namespace Pia.ViewModels;

public partial class DeviceManagementViewModel : ObservableObject
{
    private readonly IDeviceManagementService _deviceMgmt;
    private readonly IDeviceKeyService _deviceKeys;
    private readonly ILogger<DeviceManagementViewModel> _logger;
    private readonly ISnackbarService _snackbarService;

    [ObservableProperty]
    private ObservableCollection<DeviceInfo> _devices = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _currentDeviceId = "";

    [ObservableProperty]
    private string _currentFingerprint = "";

    public DeviceManagementViewModel(
        IDeviceManagementService deviceMgmt,
        IDeviceKeyService deviceKeys,
        ILogger<DeviceManagementViewModel> logger,
        ISnackbarService snackbarService)
    {
        _deviceMgmt = deviceMgmt;
        _deviceKeys = deviceKeys;
        _logger = logger;
        _snackbarService = snackbarService;
        _currentDeviceId = deviceKeys.GetDeviceId();
        _currentFingerprint = deviceKeys.GetFingerprint();
    }

    [RelayCommand]
    private async Task LoadDevicesAsync()
    {
        try
        {
            IsLoading = true;
            var response = await _deviceMgmt.GetDevicesAsync();

            // Compute fingerprints for all devices
            foreach (var device in response.Devices)
            {
                device.Fingerprint = _deviceKeys.ComputeFingerprint(device.AgreementPublicKey);
            }

            Devices = new ObservableCollection<DeviceInfo>(response.Devices);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load devices");
            _snackbarService.Show("Error", "Failed to load devices. Please check your connection.",
                Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(4));
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RevokeDeviceAsync(DeviceInfo? device)
    {
        if (device is null || device.DeviceId == CurrentDeviceId) return;

        try
        {
            await _deviceMgmt.RevokeDeviceAsync(device.DeviceId);
            await LoadDevicesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to revoke device {DeviceId}", device.DeviceId);
            _snackbarService.Show("Error", $"Failed to revoke device: {ex.Message}",
                Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(4));
        }
    }
}
