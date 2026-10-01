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
            .ThrowsAsync(new OnboardingSessionExpiredException("spent"));
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
    public async Task ActivateWithRecoveryCode_RejectedProof_DoesNotReRegister()
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
        _deviceMgmt.ActivateViaRecoveryAsync(Arg.Any<string>(), Arg.Any<string>())
            .ThrowsAsync(new HttpRequestException("Response status code does not indicate success: 400 (Bad Request)."));

        var sut = CreateSut();
        sut.ShowRecoveryCodeEntryCommand.Execute(null);
        sut.RecoveryCodeInput = "ABCD-EFGH-IJKL-MNOP";

        await sut.ActivateWithRecoveryCodeCommand.ExecuteAsync(null);

        Assert.Equal(1, callCount);
        Assert.Equal(OnboardingState.EnteringRecoveryCode, sut.State);
        Assert.Contains("Invalid recovery code", sut.ErrorMessage);
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
        await Task.Delay(100, TestContext.Current.CancellationToken);

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

    private static KeyHandover Handover(string? approverFingerprint) => new(
        new WrappedUmkBlob { DeviceId = "device-001", Ciphertext = "c", HkdfSalt = "s", CreatedByDeviceId = "dev-approver" },
        "approver-agreement-key", 1, approverFingerprint is null ? null : "Laptop", approverFingerprint);

    private async Task<E2EEOnboardingViewModel> ApprovedAsync(KeyHandover handover)
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
            Status = DeviceStatus.Active
        });
        _deviceMgmt.FetchKeyHandoverAsync().Returns(handover);

        var sut = new E2EEOnboardingViewModel(
            _deviceMgmt, _deviceKeys, _e2ee, _syncService, _settingsService,
            NullLogger<E2EEOnboardingViewModel>.Instance, pollIntervalOverride: TimeSpan.FromMilliseconds(10));
        await sut.StartDeviceApprovalCommand.ExecuteAsync(null);
        return sut;
    }

    // The server decides which device counts as the approver, so the key waits for a person to compare.
    [Fact]
    public async Task AnApprovalByAnotherDevice_WaitsForTheFingerprintToBeConfirmed()
    {
        var sut = await ApprovedAsync(Handover("AAAA-BBBB-CCCC-DDDD"));

        await WaitForState(sut, OnboardingState.ConfirmingApprover, timeout: TimeSpan.FromSeconds(5));

        Assert.Equal(OnboardingState.ConfirmingApprover, sut.State);
        Assert.Equal("AAAA-BBBB-CCCC-DDDD", sut.ApproverFingerprint);
        Assert.Equal("Laptop", sut.ApproverDeviceName);
        await _deviceMgmt.DidNotReceive().AcceptKeyHandoverAsync(Arg.Any<KeyHandover>());
        await _deviceMgmt.DidNotReceive().FetchAndUnwrapUmkAsync();
        sut.Cleanup();
    }

    [Fact]
    public async Task ConfirmingTheFingerprint_AcceptsTheKeyAndCompletes()
    {
        var handover = Handover("AAAA-BBBB-CCCC-DDDD");
        var sut = await ApprovedAsync(handover);
        var completed = false;
        sut.OnboardingCompleted += (_, _) => completed = true;
        await WaitForState(sut, OnboardingState.ConfirmingApprover, timeout: TimeSpan.FromSeconds(5));

        await sut.ConfirmApproverCommand.ExecuteAsync(null);

        Assert.Equal(OnboardingState.Success, sut.State);
        Assert.True(completed);
        await _deviceMgmt.Received(1).AcceptKeyHandoverAsync(handover);
        await _settingsService.Received(1).SaveSettingsAsync(Arg.Is<AppSettings>(s => s.IsE2EEEnabled));
        sut.Cleanup();
    }

    [Fact]
    public async Task AFingerprintThatDoesNotMatch_RefusesTheKey()
    {
        var sut = await ApprovedAsync(Handover("AAAA-BBBB-CCCC-DDDD"));
        var completed = false;
        sut.OnboardingCompleted += (_, _) => completed = true;
        await WaitForState(sut, OnboardingState.ConfirmingApprover, timeout: TimeSpan.FromSeconds(5));

        sut.RejectApproverCommand.Execute(null);
        await sut.ConfirmApproverCommand.ExecuteAsync(null);

        Assert.Equal(OnboardingState.Error, sut.State);
        Assert.False(string.IsNullOrEmpty(sut.ErrorMessage));
        Assert.Equal("", sut.ApproverFingerprint);
        Assert.False(completed);
        await _deviceMgmt.DidNotReceive().AcceptKeyHandoverAsync(Arg.Any<KeyHandover>());
        await _settingsService.DidNotReceive().SaveSettingsAsync(Arg.Any<AppSettings>());
        _syncService.DidNotReceive().NotifyE2EEOnboardingCompleted();
        sut.Cleanup();
    }

    [Fact]
    public async Task ThisDevicesOwnCopy_IsAcceptedWithoutAsking()
    {
        var handover = Handover(approverFingerprint: null);
        var sut = await ApprovedAsync(handover);

        await WaitForState(sut, OnboardingState.Success, timeout: TimeSpan.FromSeconds(5));

        Assert.Equal(OnboardingState.Success, sut.State);
        await _deviceMgmt.Received(1).AcceptKeyHandoverAsync(handover);
        sut.Cleanup();
    }

    [Fact]
    public async Task AKeyThatCannotBeOpenedAfterConfirming_EndsInAnError()
    {
        var sut = await ApprovedAsync(Handover("AAAA-BBBB-CCCC-DDDD"));
        _deviceMgmt.AcceptKeyHandoverAsync(Arg.Any<KeyHandover>())
            .ThrowsAsync(new System.Security.Cryptography.CryptographicException("bad wrap"));
        await WaitForState(sut, OnboardingState.ConfirmingApprover, timeout: TimeSpan.FromSeconds(5));

        await sut.ConfirmApproverCommand.ExecuteAsync(null);

        Assert.Equal(OnboardingState.Error, sut.State);
        Assert.False(string.IsNullOrEmpty(sut.ErrorMessage));
        await _settingsService.DidNotReceive().SaveSettingsAsync(Arg.Any<AppSettings>());
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
