namespace Pia.Tests.E2EE;

using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Shared.E2EE;
using Pia.ViewModels;
using Xunit;

public class E2EEOnboardingViewModelTests
{
    private readonly IDeviceManagementService _deviceMgmt;
    private readonly IDeviceKeyService _deviceKeys;
    private readonly IE2EEService _e2ee;
    private readonly ISyncClientService _syncService;
    private readonly ISettingsService _settingsService;
    private readonly AppSettings _settings;

    public E2EEOnboardingViewModelTests()
    {
        _deviceMgmt = Substitute.For<IDeviceManagementService>();
        _deviceKeys = Substitute.For<IDeviceKeyService>();
        _e2ee = Substitute.For<IE2EEService>();
        _syncService = Substitute.For<ISyncClientService>();
        _settingsService = Substitute.For<ISettingsService>();

        _settings = new AppSettings();
        _settingsService.GetSettingsAsync().Returns(_settings);
        _deviceKeys.GetDeviceId().Returns("device-001");
        _deviceKeys.GetFingerprint().Returns("ABCD-1234-EFGH-5678");
    }

    private E2EEOnboardingViewModel CreateSut() => new(
        _deviceMgmt, _deviceKeys, _e2ee, _syncService, _settingsService,
        NullLogger<E2EEOnboardingViewModel>.Instance);

    [Fact]
    public void InitialState_ShouldBeInitial()
    {
        var sut = CreateSut();
        Assert.Equal(OnboardingState.Initial, sut.State);
    }

    [Fact]
    public async Task StartDeviceApproval_ShouldRegisterAndTransitionToWaiting()
    {
        _deviceMgmt.RegisterPendingDeviceAsync().Returns(new DeviceRegistrationResponse
        {
            OnboardingSessionId = "session-abc",
            ServerChallenge = "challenge",
            IsFirstDevice = false
        });

        // Return pending status so polling doesn't immediately complete
        _deviceMgmt.GetDeviceStatusAsync("device-001").Returns(new DeviceStatusResponse
        {
            DeviceId = "device-001",
            Status = DeviceStatus.Pending
        });

        var sut = CreateSut();
        await sut.StartDeviceApprovalCommand.ExecuteAsync(null);

        Assert.Equal(OnboardingState.WaitingForApproval, sut.State);
        Assert.Equal("ABCD-1234-EFGH-5678", sut.DeviceFingerprint);
        await _deviceMgmt.Received(1).RegisterPendingDeviceAsync();

        sut.Cleanup(); // Stop polling
    }

    [Fact]
    public async Task StartDeviceApproval_OnFailure_ShouldTransitionToError()
    {
        _deviceMgmt.RegisterPendingDeviceAsync()
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var sut = CreateSut();
        await sut.StartDeviceApprovalCommand.ExecuteAsync(null);

        Assert.Equal(OnboardingState.Error, sut.State);
        Assert.Contains("Connection refused", sut.ErrorMessage);
    }

    [Fact]
    public void ShowRecoveryCodeEntry_ShouldTransitionToRecoveryState()
    {
        var sut = CreateSut();
        sut.ShowRecoveryCodeEntryCommand.Execute(null);

        Assert.Equal(OnboardingState.EnteringRecoveryCode, sut.State);
        Assert.Equal("", sut.RecoveryCodeInput);
    }

    [Fact]
    public async Task ActivateWithRecoveryCode_EmptyInput_ShouldShowError()
    {
        var sut = CreateSut();
        sut.ShowRecoveryCodeEntryCommand.Execute(null);
        sut.RecoveryCodeInput = "";

        await sut.ActivateWithRecoveryCodeCommand.ExecuteAsync(null);

        Assert.Equal(OnboardingState.EnteringRecoveryCode, sut.State);
        Assert.Equal("Please enter your recovery code.", sut.ErrorMessage);
    }

    [Fact]
    public async Task ActivateWithRecoveryCode_ValidCode_ShouldComplete()
    {
        _deviceMgmt.RegisterPendingDeviceAsync().Returns(new DeviceRegistrationResponse
        {
            OnboardingSessionId = "session-abc",
            ServerChallenge = "challenge",
            IsFirstDevice = false
        });

        var completed = false;
        var sut = CreateSut();
        sut.OnboardingCompleted += (_, _) => completed = true;

        sut.ShowRecoveryCodeEntryCommand.Execute(null);
        sut.RecoveryCodeInput = "ABCD-EFGH-IJKL-MNOP";

        await sut.ActivateWithRecoveryCodeCommand.ExecuteAsync(null);

        Assert.Equal(OnboardingState.Success, sut.State);
        Assert.True(completed);
        await _deviceMgmt.Received(1).RegisterPendingDeviceAsync();
        await _deviceMgmt.Received(1).ActivateViaRecoveryAsync("ABCD-EFGH-IJKL-MNOP", "session-abc");
        await _settingsService.Received(1).SaveSettingsAsync(Arg.Is<AppSettings>(s => s.IsE2EEEnabled));
    }

    [Fact]
    public async Task ActivateWithRecoveryCode_InvalidCode_ShouldStayOnRecoveryScreen()
    {
        _deviceMgmt.RegisterPendingDeviceAsync().Returns(new DeviceRegistrationResponse
        {
            OnboardingSessionId = "session-abc",
            ServerChallenge = "challenge",
            IsFirstDevice = false
        });
        // Use a non-"Invalid"/"expired" message so it hits the generic catch
        _deviceMgmt.ActivateViaRecoveryAsync(Arg.Any<string>(), Arg.Any<string>())
            .ThrowsAsync(new Exception("Bad recovery proof"));

        var sut = CreateSut();
        sut.ShowRecoveryCodeEntryCommand.Execute(null);
        sut.RecoveryCodeInput = "WRONG-CODE-HERE-XXXX";

        await sut.ActivateWithRecoveryCodeCommand.ExecuteAsync(null);

        Assert.Equal(OnboardingState.EnteringRecoveryCode, sut.State);
        Assert.Contains("Invalid recovery code", sut.ErrorMessage);
    }

    [Fact]
    public async Task ActivateWithRecoveryCode_ExpiredSession_ShouldReRegister()
    {
        var callCount = 0;
        _deviceMgmt.RegisterPendingDeviceAsync().Returns(_ =>
        {
            callCount++;
            return new DeviceRegistrationResponse
            {
                OnboardingSessionId = $"session-{callCount}",
                ServerChallenge = "challenge",
                IsFirstDevice = false
            };
        });

        // First call: expired session. Second call: success.
        _deviceMgmt.ActivateViaRecoveryAsync(Arg.Any<string>(), "session-1")
            .ThrowsAsync(new HttpRequestException("Invalid or expired onboarding session"));
        _deviceMgmt.ActivateViaRecoveryAsync(Arg.Any<string>(), "session-2")
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        sut.ShowRecoveryCodeEntryCommand.Execute(null);
        sut.RecoveryCodeInput = "ABCD-EFGH-IJKL-MNOP";

        await sut.ActivateWithRecoveryCodeCommand.ExecuteAsync(null);

        Assert.Equal(OnboardingState.Success, sut.State);
        Assert.Equal(2, callCount); // Re-registered
    }

    [Fact]
    public void GoBack_ShouldResetToInitial()
    {
        var sut = CreateSut();
        sut.ShowRecoveryCodeEntryCommand.Execute(null);
        Assert.Equal(OnboardingState.EnteringRecoveryCode, sut.State);

        sut.GoBackCommand.Execute(null);
        Assert.Equal(OnboardingState.Initial, sut.State);
        Assert.Null(sut.ErrorMessage);
    }

    [Fact]
    public async Task GoBack_WhilePolling_ShouldStopPolling()
    {
        _deviceMgmt.RegisterPendingDeviceAsync().Returns(new DeviceRegistrationResponse
        {
            OnboardingSessionId = "session-abc",
            ServerChallenge = "challenge",
            IsFirstDevice = false
        });
        _deviceMgmt.GetDeviceStatusAsync("device-001").Returns(new DeviceStatusResponse
        {
            DeviceId = "device-001",
            Status = DeviceStatus.Pending
        });

        var sut = CreateSut();
        await sut.StartDeviceApprovalCommand.ExecuteAsync(null);
        Assert.Equal(OnboardingState.WaitingForApproval, sut.State);

        sut.GoBackCommand.Execute(null);
        Assert.Equal(OnboardingState.Initial, sut.State);

        // Allow a moment for the cancelled polling to settle
        await Task.Delay(100);

        // Should not have transitioned to error/success after going back
        Assert.Equal(OnboardingState.Initial, sut.State);
    }

    [Fact]
    public async Task Polling_DeviceRevoked_ShouldTransitionToError()
    {
        _deviceMgmt.RegisterPendingDeviceAsync().Returns(new DeviceRegistrationResponse
        {
            OnboardingSessionId = "session-abc",
            ServerChallenge = "challenge",
            IsFirstDevice = false
        });

        // First poll: pending. Second poll: revoked.
        var pollCount = 0;
        _deviceMgmt.GetDeviceStatusAsync("device-001").Returns(_ =>
        {
            pollCount++;
            return new DeviceStatusResponse
            {
                DeviceId = "device-001",
                Status = pollCount <= 1 ? DeviceStatus.Pending : DeviceStatus.Revoked
            };
        });

        var sut = CreateSut();
        await sut.StartDeviceApprovalCommand.ExecuteAsync(null);

        // Wait for polling to detect the revocation (poll interval is 5s, but in tests it runs quickly)
        await WaitForState(sut, OnboardingState.Error, timeout: TimeSpan.FromSeconds(15));

        Assert.Equal(OnboardingState.Error, sut.State);
        Assert.Contains("rejected", sut.ErrorMessage);

        sut.Cleanup();
    }

    [Fact]
    public async Task Polling_DeviceApproved_ShouldFetchUmkAndComplete()
    {
        _deviceMgmt.RegisterPendingDeviceAsync().Returns(new DeviceRegistrationResponse
        {
            OnboardingSessionId = "session-abc",
            ServerChallenge = "challenge",
            IsFirstDevice = false
        });

        // Return approved on first poll
        _deviceMgmt.GetDeviceStatusAsync("device-001").Returns(new DeviceStatusResponse
        {
            DeviceId = "device-001",
            Status = DeviceStatus.Active
        });

        var completed = false;
        var sut = CreateSut();
        sut.OnboardingCompleted += (_, _) => completed = true;

        await sut.StartDeviceApprovalCommand.ExecuteAsync(null);

        await WaitForState(sut, OnboardingState.Success, timeout: TimeSpan.FromSeconds(15));

        Assert.Equal(OnboardingState.Success, sut.State);
        Assert.True(completed);
        await _deviceMgmt.Received(1).FetchAndUnwrapUmkAsync();
        await _settingsService.Received(1).SaveSettingsAsync(Arg.Is<AppSettings>(s => s.IsE2EEEnabled));

        sut.Cleanup();
    }

    private static async Task WaitForState(E2EEOnboardingViewModel vm, OnboardingState expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (vm.State != expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }
}
