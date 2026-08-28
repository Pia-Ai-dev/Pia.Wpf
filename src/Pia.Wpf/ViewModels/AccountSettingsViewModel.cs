using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Helpers;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;
using Pia.Shared.E2EE;
using System.Text.Json;

namespace Pia.ViewModels;

public partial class AccountSettingsViewModel : UiThreadViewModel, IDisposable
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
    private readonly IMemoryService _memoryService;
    private readonly IPolicyService _policyService;

    /// <summary>Bind IsEnabled to Policy[nameof(AppSettings.X)] to grey a control out while policy enforces it.</summary>
    public PolicyLock Policy { get; }
    private bool _isLoading;
    private bool _disposed;

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
        IMemoryService memoryService,
        IPolicyService policyService,
        E2EEOnboardingViewModel onboardingViewModel)
        : base(requireUiThread: true)
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
        _memoryService = memoryService;
        _policyService = policyService;
        Policy = new PolicyLock(policyService);
        OnboardingViewModel = onboardingViewModel;

        OnboardingViewModel.OnboardingCompleted += async (_, _) =>
        {
            try
            {
                IsE2EEOnboardingRequired = false;
                _syncClientService.NotifyE2EEOnboardingCompleted();
                _isLoading = true;
                IsE2EEEnabled = true;
                _isLoading = false;
                DeviceFingerprint = _deviceKeys.GetFingerprint();
                await _syncClientService.PerformFirstSyncMigrationAsync();

                var settings = await _settingsService.GetSettingsAsync();
                LastSyncText = FormatRelativeTime(settings.LastSyncTimestamp);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "First sync after E2EE onboarding failed");
            }
            _syncClientService.StartBackgroundSync();
        };

        // Seed from current service state — the event may have fired before this
        // VM was constructed (e.g. during wizard login that was then skipped).
        IsE2EEOnboardingRequired = _syncClientService.IsE2EEOnboardingRequired;

        _syncClientService.E2EEOnboardingRequired += (_, _) =>
        {
            Post(() =>
            {
                IsE2EEOnboardingRequired = true;
            });
        };

        _syncClientService.E2EEOnboardingCleared += (_, _) =>
        {
            Post(() =>
            {
                IsE2EEOnboardingRequired = false;
            });
        };

        _authService.LoginStateChanged += (_, isLoggedIn) =>
        {
            if (isLoggedIn) return;
            Post(() =>
            {
                IsE2EEOnboardingRequired = false;
                _isLoading = true;
                IsE2EEEnabled = false;
                _isLoading = false;
                DeviceFingerprint = string.Empty;
                UpdateSyncState();
            });
        };

        _syncClientService.PendingDeviceDetected += (_, args) =>
        {
            Post(() =>
                HandlePendingDevicesAsync(args.PendingDevices).SafeFireAndForget(_logger));
        };

        _syncClientService.CurrentDeviceRevoked += (_, _) =>
        {
            Post(async () =>
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

    // IsServerUrlEnforced itself is unbound; the markup binds the derived property. The login-visibility
    // getters read allowedSyncProviders, which PolicyLock cannot reach.
    private void OnLocksChanged(object? sender, EventArgs e) => Post(() =>
    {
        OnPropertyChanged(nameof(IsServerUrlEditable));
        OnPropertyChanged(nameof(IsLocalLoginVisible));
        OnPropertyChanged(nameof(IsGoogleLoginVisible));
        OnPropertyChanged(nameof(IsMicrosoftLoginVisible));
        OnPropertyChanged(nameof(IsEntraIdLoginVisible));
        OnPropertyChanged(nameof(IsAnyOAuthLoginVisible));
    });

    // Raised from the policy pull thread, so the mirror has to be marshalled.
    private void OnSettingsChanged(object? sender, AppSettings settings) => Post(() =>
    {
        _isLoading = true;
        ApplySettings(settings);
        _isLoading = false;
    });

    // Sync properties
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerUrlEditable))]
    private bool _isSyncLoggedIn;

    // Enterprise policy enforcement
    public bool IsServerUrlEnforced => _policyService.IsEnforced(nameof(AppSettings.ServerUrl));

    public bool IsServerUrlEditable => !IsSyncLoggedIn && !IsServerUrlEnforced;

    public bool IsLocalLoginVisible => _policyService.IsLoginProviderAllowed("local");
    public bool IsGoogleLoginVisible => _policyService.IsLoginProviderAllowed("google");
    public bool IsMicrosoftLoginVisible => _policyService.IsLoginProviderAllowed("microsoft");
    public bool IsEntraIdLoginVisible => _policyService.IsLoginProviderAllowed("entraid");
    public bool IsAnyOAuthLoginVisible => IsGoogleLoginVisible || IsMicrosoftLoginVisible || IsEntraIdLoginVisible;

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

    public bool IsDevMode =>
#if DEBUG
        true;
#else
        false;
#endif

    [ObservableProperty]
    private bool _isSyncLoggingIn;

    /// <summary>Set after a sign-in the server considers incomplete — single sign-on never sees the form.</summary>
    [ObservableProperty]
    private bool _requiresBusinessProfile;

    [ObservableProperty]
    private string _companyNameInput = "";

    [ObservableProperty]
    private string? _businessProfileError;

    [ObservableProperty]
    private string _loginEmail = "";

    [ObservableProperty]
    private string _loginPassword = "";

    [ObservableProperty]
    private string? _loginErrorMessage;

    // E2EE properties
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsE2EEToggleEnabled))]
    private bool _isE2EEEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsE2EEToggleEnabled))]
    private bool _canToggleE2EE = true;

    /// <summary>
    /// E2EE is permanent per account (the server rejects plaintext pushes once enabled),
    /// so the toggle locks as soon as encryption is on.
    /// </summary>
    public bool IsE2EEToggleEnabled => CanToggleE2EE && !IsE2EEEnabled;

    [ObservableProperty]
    private string _deviceFingerprint = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSyncNow))]
    private bool _isE2EEOnboardingRequired;

    // Sync status
    [ObservableProperty]
    private string _lastSyncText = "";

    [ObservableProperty]
    private string? _lastSyncItemsText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSyncNow))]
    private bool _isSyncing;

    public bool CanSyncNow => !IsSyncing && !IsE2EEOnboardingRequired;

    partial void OnIsE2EEEnabledChanged(bool value)
    {
        if (_isLoading) return;

        if (value)
        {
            _ = EnableE2EEAsync();
        }
        else
        {
            // E2EE is permanent once enabled: the server rejects plaintext pushes for
            // this account (e2ee_required), so a local "disable" would only stall sync
            // and destroy nothing but this device's ability to participate. Revert.
            _isLoading = true;
            IsE2EEEnabled = true;
            _isLoading = false;
            _snackbarService.Show("End-to-end encryption",
                "E2EE cannot be disabled once enabled. Resetting server sync data is the only way to start over without it.",
                Wpf.Ui.Controls.ControlAppearance.Info, null, TimeSpan.FromSeconds(5));
        }
    }

    partial void OnTrustSelfSignedCertificatesChanged(bool value)
    {
        if (!_isLoading) SaveSyncSettingsAsync().SafeFireAndForget(_logger);
    }

    public async Task InitializeAsync()
    {
        _isLoading = true;

        var settings = await _settingsService.GetSettingsAsync();

        // Not in ApplySettings: a reload mid-typing would wipe the box, and ServerUrl is a denied key anyway.
        ServerUrl = settings.ServerUrl ?? "";
        ApplySettings(settings);
        UpdateSyncState();

        // E2EE state
        IsE2EEEnabled = settings.IsE2EEEnabled;
        if (_deviceManagement.IsInitialized())
            DeviceFingerprint = _deviceKeys.GetFingerprint();

        _isLoading = false;
    }

    // IsE2EEEnabled is hand-managed across onboarding and revocation, so it is not mirrored here.
    private void ApplySettings(AppSettings settings)
    {
        TrustSelfSignedCertificates = settings.TrustSelfSignedCertificates;
        LastSyncText = FormatRelativeTime(settings.LastSyncTimestamp);
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
                await TrySeedPersonalProfileFromAuthAsync();
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
                await TrySeedPersonalProfileFromAuthAsync();
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
    private async Task LoginWithEntraIdAsync()
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

            var (success, errorMessage) = await _authService.LoginAsync("entraid");
            if (success)
            {
                await HandlePostLoginAsync();
                await TrySeedPersonalProfileFromAuthAsync();
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
    private async Task SubmitBusinessProfileAsync()
    {
        BusinessProfileError = null;

        if (string.IsNullOrWhiteSpace(CompanyNameInput))
        {
            BusinessProfileError = _localizationService["Sync_Cloud_BusinessProfile_CompanyRequired"];
            return;
        }

        var (success, error) = await _authService.SubmitBusinessProfileAsync(CompanyNameInput.Trim());
        if (!success)
        {
            BusinessProfileError = error;
            return;
        }

        RequiresBusinessProfile = false;
        CompanyNameInput = "";
        await HandlePostLoginAsync();
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

        RequiresBusinessProfile = await _authService.RequiresBusinessProfileAsync();
        if (RequiresBusinessProfile)
        {
            // Syncing would only collect 403s until the declaration is in.
            _logger.LogInformation("Account still owes its business profile; deferring sync");
            return;
        }

        var e2eeStatus = await _deviceManagement.CheckE2EEStatusAsync();
        if (e2eeStatus is { IsEnabled: true } && !_deviceManagement.IsInitialized())
        {
            _logger.LogInformation("E2EE enabled on account but UMK not available; onboarding required");
            IsE2EEOnboardingRequired = true;
            _syncClientService.NotifyE2EEOnboardingRequired();
            return;
        }

        await _syncClientService.PerformFirstSyncMigrationAsync();
        _syncClientService.StartBackgroundSync();
    }

    private async Task TrySeedPersonalProfileFromAuthAsync()
    {
        try
        {
            var displayName = _authService.UserDisplayName;
            if (string.IsNullOrWhiteSpace(displayName)) return;

            var existing = await _memoryService.GetObjectsByTypeAsync(MemoryObjectTypes.PersonalProfile);
            if (existing.Count > 0) return;

            var trimmed = displayName.Trim();
            var profileData = new
            {
                name = trimmed,
                nickname = "",
                location = "",
                operating_mode = UserOperatingMode.Personal.ToString().ToLowerInvariant(),
                preferred_name = trimmed
            };
            var jsonData = JsonSerializer.Serialize(profileData);
            await _memoryService.CreateObjectAsync(MemoryObjectTypes.PersonalProfile, "Personal Profile", jsonData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to seed personal profile from OAuth display name");
        }
    }

    [RelayCommand]
    private async Task SyncNowAsync()
    {
        if (IsSyncing || IsE2EEOnboardingRequired) return;

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
    private async Task ForceFullResyncAsync()
    {
        if (IsSyncing || IsE2EEOnboardingRequired) return;

        try
        {
            IsSyncing = true;
            _logger.LogInformation("Force full re-sync requested by user");
            await _syncClientService.ForceFullResyncAsync();

            var settings = await _settingsService.GetSettingsAsync();
            LastSyncText = FormatRelativeTime(settings.LastSyncTimestamp);

            _snackbarService.Show(
                _localizationService["Sync_ResyncTitle"] ?? "Re-sync",
                _localizationService["Sync_ResyncComplete"] ?? "Full re-sync completed",
                Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Force full re-sync failed");
            _snackbarService.Show("Error", $"Re-sync failed: {ex.Message}",
                Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(5));
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
                _syncClientService.NotifyE2EEOnboardingRequired();
                return;
            }

            var recoveryCode = await _deviceManagement.BootstrapFirstDeviceAsync();

            DeviceFingerprint = _deviceKeys.GetFingerprint();

            await _dialogService.ShowRecoveryCodeDialogAsync(recoveryCode);

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
                _syncClientService.NotifyE2EEOnboardingRequired();
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

}
