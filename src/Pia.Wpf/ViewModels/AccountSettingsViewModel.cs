using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Shared.E2EE;
using System.Collections.ObjectModel;
using System.IO;

namespace Pia.ViewModels;

public partial class AccountSettingsViewModel : ObservableObject
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogService;
    private readonly Wpf.Ui.ISnackbarService _snackbarService;
    private readonly IAuthService _authService;
    private readonly ISyncClientService _syncClientService;
    private readonly ILocalizationService _localizationService;
    private readonly IDeviceManagementService _deviceManagement;
    private readonly IDeviceKeyService _deviceKeys;
    private bool _isLoading;

    public E2EEOnboardingViewModel OnboardingViewModel { get; }

    public AccountSettingsViewModel(
        ILogger<SettingsViewModel> logger,
        ISettingsService settingsService,
        IDialogService dialogService,
        Wpf.Ui.ISnackbarService snackbarService,
        IAuthService authService,
        ISyncClientService syncClientService,
        ILocalizationService localizationService,
        IDeviceManagementService deviceManagement,
        IDeviceKeyService deviceKeys,
        E2EEOnboardingViewModel onboardingViewModel)
    {
        _logger = logger;
        _settingsService = settingsService;
        _dialogService = dialogService;
        _snackbarService = snackbarService;
        _authService = authService;
        _syncClientService = syncClientService;
        _localizationService = localizationService;
        _deviceManagement = deviceManagement;
        _deviceKeys = deviceKeys;
        OnboardingViewModel = onboardingViewModel;

        OnboardingViewModel.OnboardingCompleted += async (_, _) =>
        {
            IsE2EEOnboardingRequired = false;
            IsE2EEEnabled = true;
            DeviceFingerprint = _deviceKeys.GetFingerprint();
            await _syncClientService.PerformFirstSyncMigrationAsync();
            _syncClientService.StartBackgroundSync();
        };

        _syncClientService.E2EEOnboardingRequired += (_, _) =>
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                IsE2EEOnboardingRequired = true;
            });
        };

        _syncClientService.PendingDeviceDetected += (_, args) =>
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(
                () => HandlePendingDevicesAsync(args.PendingDevices));
        };

        _syncClientService.CurrentDeviceRevoked += (_, _) =>
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(async () =>
            {
                _isLoading = true;
                IsE2EEEnabled = false;
                DeviceFingerprint = string.Empty;
                _isLoading = false;

                var settings = await _settingsService.GetSettingsAsync();
                settings.IsE2EEEnabled = false;
                await _settingsService.SaveSettingsAsync(settings);

                _snackbarService.Show("E2EE Disabled",
                    "This device was removed from E2EE. Encryption has been disabled.",
                    Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(8));
            });
        };
    }

    // Inner tab index
    [ObservableProperty]
    private int _selectedInnerTabIndex;

    // Sync properties
    [ObservableProperty]
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

    // Sync status
    [ObservableProperty]
    private string _lastSyncText = "";

    [ObservableProperty]
    private string? _lastSyncItemsText;

    [ObservableProperty]
    private bool _isSyncing;

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

    partial void OnTokenizationEnabledChanged(bool value)
    {
        if (!_isLoading) SafeFireAndForget(SavePrivacySettingsAsync());
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

    public async Task InitializeAsync()
    {
        _isLoading = true;

        var settings = await _settingsService.GetSettingsAsync();

        // Sync state
        ServerUrl = settings.ServerUrl ?? "";
        TrustSelfSignedCertificates = settings.TrustSelfSignedCertificates;
        UpdateSyncState();
        LastSyncText = FormatRelativeTime(settings.LastSyncTimestamp);

        // E2EE state
        IsE2EEEnabled = settings.IsE2EEEnabled;
        if (_deviceManagement.IsInitialized())
            DeviceFingerprint = _deviceKeys.GetFingerprint();

        // Privacy settings
        TokenizationEnabled = settings.Privacy.TokenizationEnabled;
        foreach (var entry in PiiKeywords)
            entry.PropertyChanged -= OnPiiKeywordEntryChanged;
        var entries = settings.Privacy.PiiKeywords;
        foreach (var entry in entries)
            entry.PropertyChanged += OnPiiKeywordEntryChanged;
        PiiKeywords = new ObservableCollection<PiiKeywordEntry>(entries);

        _isLoading = false;
    }

    private void UpdateSyncState()
    {
        IsSyncLoggedIn = _authService.IsLoggedIn;
        SyncUserEmail = _authService.UserEmail;
        SyncUserDisplayName = _authService.UserDisplayName;
        SyncProvider = _authService.Provider;
    }

    private string FormatRelativeTime(DateTime? utcTimestamp)
    {
        if (utcTimestamp is null)
            return _localizationService["Sync_NeverSynced"];

        var elapsed = DateTime.UtcNow - utcTimestamp.Value;

        return elapsed.TotalSeconds < 60 ? _localizationService["Sync_JustNow"]
            : elapsed.TotalMinutes < 60 ? string.Format(_localizationService["Sync_MinutesAgo"], (int)elapsed.TotalMinutes)
            : elapsed.TotalHours < 24 ? string.Format(_localizationService["Sync_HoursAgo"], (int)elapsed.TotalHours)
            : string.Format(_localizationService["Sync_DaysAgo"], (int)elapsed.TotalDays);
    }

    // Login commands
    [RelayCommand]
    private async Task LoginWithGoogleAsync()
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

    private async Task HandlePostLoginAsync()
    {
        UpdateSyncState();

        var e2eeStatus = await _deviceManagement.CheckE2EEStatusAsync();
        if (e2eeStatus is { IsEnabled: true } && !_deviceManagement.IsInitialized())
        {
            _logger.LogInformation("E2EE enabled on account but UMK not available; onboarding required");
            IsE2EEOnboardingRequired = true;
            return;
        }

        await _syncClientService.PerformFirstSyncMigrationAsync();
        _syncClientService.StartBackgroundSync();
    }

    [RelayCommand]
    private async Task SyncNowAsync()
    {
        if (IsSyncing) return;

        try
        {
            IsSyncing = true;
            var result = await _syncClientService.SyncNowAsync();

            var settings = await _settingsService.GetSettingsAsync();
            LastSyncText = FormatRelativeTime(settings.LastSyncTimestamp);

            if (result is not null)
            {
                LastSyncItemsText = string.Format(
                    _localizationService["Sync_ItemCounts"],
                    result.PushedCount, result.PulledCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual sync failed");
        }
        finally
        {
            IsSyncing = false;
        }
    }

    [RelayCommand]
    private async Task SyncLogoutAsync()
    {
        _syncClientService.StopBackgroundSync();
        await _authService.LogoutAsync();
        UpdateSyncState();
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

    private async Task EnableE2EEAsync()
    {
        try
        {
            CanToggleE2EE = false;

            await _syncClientService.StopBackgroundSyncAndWaitAsync();

            var serverStatus = await _deviceManagement.CheckE2EEStatusAsync();
            if (serverStatus is { IsEnabled: true })
            {
                IsE2EEOnboardingRequired = true;
                return;
            }

            var recoveryCode = await _deviceManagement.BootstrapFirstDeviceAsync();

            DeviceFingerprint = _deviceKeys.GetFingerprint();

            var copyRequested = await _dialogService.ShowMessageWithCopyDialogAsync(
                "Recovery Code",
                $"Save this recovery code in a safe place. It is the ONLY way to recover your encrypted data if you lose all devices.\n\n{recoveryCode}\n\nIf you lose this code and all your devices, your encrypted data cannot be recovered.");
            if (copyRequested)
                System.Windows.Clipboard.SetText(recoveryCode);

            await _syncClientService.PerformFirstSyncMigrationAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enable E2EE");
            _isLoading = true;
            IsE2EEEnabled = false;
            _isLoading = false;

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

            await _syncClientService.StopBackgroundSyncAndWaitAsync();

            var settings = await _settingsService.GetSettingsAsync();
            settings.IsE2EEEnabled = false;
            await _settingsService.SaveSettingsAsync(settings);

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

    // PII keyword commands
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
        await SavePrivacySettingsAsync();
    }

    [RelayCommand]
    private async Task RemovePiiKeywordAsync(PiiKeywordEntry entry)
    {
        entry.PropertyChanged -= OnPiiKeywordEntryChanged;
        if (PiiKeywords.Remove(entry))
            await SavePrivacySettingsAsync();
    }

    private void OnPiiKeywordEntryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_isLoading && e.PropertyName == nameof(PiiKeywordEntry.Category))
            SafeFireAndForget(SavePrivacySettingsAsync());
    }

    // Reset app data
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
            _syncClientService.StopBackgroundSync();

            var roamingDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pia");
            var localDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pia");

            if (Directory.Exists(roamingDir))
                Directory.Delete(roamingDir, recursive: true);
            if (Directory.Exists(localDir))
                Directory.Delete(localDir, recursive: true);

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

    private async Task SavePrivacySettingsAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        settings.Privacy.TokenizationEnabled = TokenizationEnabled;
        settings.Privacy.PiiKeywords = PiiKeywords.Select(e => new PiiKeywordEntry { Keyword = e.Keyword, Category = e.Category }).ToList();
        await _settingsService.SaveSettingsAsync(settings);
    }

    private async void SafeFireAndForget(Task task)
    {
        try { await task; }
        catch (Exception ex) { _logger.LogError(ex, "Background operation failed"); }
    }
}
