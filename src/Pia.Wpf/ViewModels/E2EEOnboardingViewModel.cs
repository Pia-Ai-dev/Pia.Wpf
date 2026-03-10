using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Shared.E2EE;

namespace Pia.ViewModels;

public enum OnboardingState
{
    Initial,
    WaitingForApproval,
    EnteringRecoveryCode,
    Activating,
    Success,
    Error
}

public partial class E2EEOnboardingViewModel : ObservableObject
{
    private readonly IDeviceManagementService _deviceMgmt;
    private readonly IDeviceKeyService _deviceKeys;
    private readonly IE2EEService _e2ee;
    private readonly ISyncClientService _syncService;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<E2EEOnboardingViewModel> _logger;

    private CancellationTokenSource? _pollCts;
    private string? _onboardingSessionId;
    private int _consecutivePollErrors;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(10);
    private const int MaxConsecutivePollErrors = 3;

    [ObservableProperty]
    private OnboardingState _state = OnboardingState.Initial;

    [ObservableProperty]
    private string _deviceFingerprint = "";

    [ObservableProperty]
    private string _recoveryCodeInput = "";

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _statusMessage = "";

    /// <summary>
    /// Raised when onboarding completes successfully and sync should resume.
    /// </summary>
    public event EventHandler? OnboardingCompleted;

    public E2EEOnboardingViewModel(
        IDeviceManagementService deviceMgmt,
        IDeviceKeyService deviceKeys,
        IE2EEService e2ee,
        ISyncClientService syncService,
        ISettingsService settingsService,
        ILogger<E2EEOnboardingViewModel> logger)
    {
        _deviceMgmt = deviceMgmt;
        _deviceKeys = deviceKeys;
        _e2ee = e2ee;
        _syncService = syncService;
        _settingsService = settingsService;
        _logger = logger;
    }

    [RelayCommand]
    private async Task StartDeviceApprovalAsync()
    {
        try
        {
            ErrorMessage = null;
            State = OnboardingState.WaitingForApproval;
            StatusMessage = "Registering device...";

            var response = await _deviceMgmt.RegisterPendingDeviceAsync();
            _onboardingSessionId = response.OnboardingSessionId;

            DeviceFingerprint = _deviceKeys.GetFingerprint();
            StatusMessage = "Waiting for approval from another device...";

            // Start polling for approval
            _pollCts?.Cancel();
            _pollCts = new CancellationTokenSource();
            _consecutivePollErrors = 0;
            _ = PollForApprovalAsync(_pollCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start device approval");
            ErrorMessage = $"Failed to register device: {ex.Message}";
            State = OnboardingState.Error;
        }
    }

    [RelayCommand]
    private void ShowRecoveryCodeEntry()
    {
        StopPolling();
        ErrorMessage = null;
        RecoveryCodeInput = "";
        State = OnboardingState.EnteringRecoveryCode;
    }

    [RelayCommand]
    private async Task ActivateWithRecoveryCodeAsync()
    {
        if (string.IsNullOrWhiteSpace(RecoveryCodeInput))
        {
            ErrorMessage = "Please enter your recovery code.";
            return;
        }

        try
        {
            ErrorMessage = null;
            State = OnboardingState.Activating;
            StatusMessage = "Verifying recovery code...";

            // Register or re-register if session expired
            await EnsureOnboardingSessionAsync();

            await _deviceMgmt.ActivateViaRecoveryAsync(
                RecoveryCodeInput.Trim(), _onboardingSessionId!);

            await CompleteOnboardingAsync();
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("expired", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Invalid", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(ex, "Onboarding session expired during recovery, re-registering");
            // Session expired or invalidated (e.g., after re-key) — try once more
            try
            {
                _onboardingSessionId = null;
                await EnsureOnboardingSessionAsync();
                await _deviceMgmt.ActivateViaRecoveryAsync(
                    RecoveryCodeInput.Trim(), _onboardingSessionId!);
                await CompleteOnboardingAsync();
            }
            catch (Exception retryEx)
            {
                _logger.LogError(retryEx, "Recovery activation retry failed");
                ErrorMessage = "Activation failed. The session may have been invalidated. Please try again.";
                State = OnboardingState.EnteringRecoveryCode;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recovery code activation failed");
            ErrorMessage = "Invalid recovery code or activation failed. Please try again.";
            State = OnboardingState.EnteringRecoveryCode;
        }
    }

    [RelayCommand]
    private void GoBack()
    {
        StopPolling();
        ErrorMessage = null;
        _onboardingSessionId = null;
        State = OnboardingState.Initial;
    }

    private async Task EnsureOnboardingSessionAsync()
    {
        if (_onboardingSessionId is null)
        {
            var response = await _deviceMgmt.RegisterPendingDeviceAsync();
            _onboardingSessionId = response.OnboardingSessionId;
        }
    }

    private async Task PollForApprovalAsync(CancellationToken ct)
    {
        var deviceId = _deviceKeys.GetDeviceId();
        var startTime = DateTime.UtcNow;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Check session timeout
                var elapsed = DateTime.UtcNow - startTime;
                if (elapsed > SessionTimeout)
                {
                    _logger.LogInformation("Onboarding session timed out after {Elapsed}", elapsed);
                    StatusMessage = "";
                    ErrorMessage = "The onboarding session has expired. If your other device is unavailable, use your recovery code instead.";
                    _onboardingSessionId = null;
                    State = OnboardingState.Error;
                    return;
                }

                // Show remaining time after 5 minutes
                if (elapsed > TimeSpan.FromMinutes(5))
                {
                    var remaining = SessionTimeout - elapsed;
                    StatusMessage = $"Waiting for approval... ({remaining.Minutes}m {remaining.Seconds}s remaining)";
                }

                await Task.Delay(PollInterval, ct);

                try
                {
                    var status = await _deviceMgmt.GetDeviceStatusAsync(deviceId);
                    _consecutivePollErrors = 0; // Reset on success

                    if (status is null)
                    {
                        // Device not found — may have been revoked or re-keyed
                        _logger.LogWarning("Device status returned null, device may have been revoked");
                        ErrorMessage = "This device was not found on the server. It may have been rejected or the encryption was re-keyed. Please try again.";
                        _onboardingSessionId = null;
                        State = OnboardingState.Error;
                        return;
                    }

                    if (status.Status == DeviceStatus.Revoked)
                    {
                        _logger.LogWarning("Device was revoked during onboarding");
                        ErrorMessage = "This device was rejected by the other device. If this was unintended, try again.";
                        _onboardingSessionId = null;
                        State = OnboardingState.Error;
                        return;
                    }

                    if (status.IsApproved)
                    {
                        _logger.LogInformation("Device approved, fetching UMK");
                        StatusMessage = "Device approved! Fetching encryption key...";

                        await _deviceMgmt.FetchAndUnwrapUmkAsync();
                        await CompleteOnboardingAsync();
                        return;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception pollEx)
                {
                    _consecutivePollErrors++;
                    _logger.LogWarning(pollEx, "Poll error ({Count}/{Max})",
                        _consecutivePollErrors, MaxConsecutivePollErrors);

                    if (_consecutivePollErrors >= MaxConsecutivePollErrors)
                    {
                        ErrorMessage = "Unable to reach the server. Check your connection and try again.";
                        State = OnboardingState.Error;
                        return;
                    }

                    // Transient error — continue polling
                    StatusMessage = "Connection issue, retrying...";
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Polling was cancelled (user navigated away)
        }
    }

    private async Task CompleteOnboardingAsync()
    {
        // Persist E2EE state
        var settings = await _settingsService.GetSettingsAsync();
        settings.IsE2EEEnabled = true;
        await _settingsService.SaveSettingsAsync(settings);

        DeviceFingerprint = _deviceKeys.GetFingerprint();
        StatusMessage = "Device activated! Your data is being synced.";
        State = OnboardingState.Success;

        _logger.LogInformation("E2EE onboarding completed successfully");

        // Notify parent to resume sync
        OnboardingCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void StopPolling()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
    }

    public void Cleanup()
    {
        StopPolling();
    }
}
