namespace Pia.Tests.E2EE;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Localization;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Shared.E2EE;
using Pia.ViewModels;
using Xunit;

/// <summary>A device whose keys were lost can never open or sign for its old id, so it is replaced under a new one
/// instead of looping on the AcceptFailed error.</summary>
public class E2EEOnboardingKeyLossTests
{
    private readonly IDeviceKeyService _deviceKeys = Substitute.For<IDeviceKeyService>();
    private readonly IDeviceManagementService _deviceMgmt = Substitute.For<IDeviceManagementService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly ISyncClientService _syncService = Substitute.For<ISyncClientService>();

    [Fact]
    public async Task ARefusedRegistration_ShowsTheMappedMessageAsItsOwn()
    {
        _deviceMgmt.RegisterPendingDeviceAsync()
            .ThrowsAsync(new DeviceRegistrationRejectedException(
                E2EEErrorCodes.TooManyPendingDevices, "Too many devices are waiting for approval."));
        var sut = CreateSut();

        await sut.StartDeviceApprovalCommand.ExecuteAsync(null);

        Assert.Equal(OnboardingState.Error, sut.State);
        Assert.Equal("Too many devices are waiting for approval.", sut.ErrorMessage);
    }

    [Theory]
    [InlineData("approval")]
    [InlineData("recovery")]
    [InlineData("retry")]
    public async Task ARefusedRegistration_ShowsPrivateDetailsButLogsOnlySafeExceptionText(string path)
    {
        const string detail = "private server response body";
        if (path != "retry")
            _deviceMgmt.RegisterPendingDeviceAsync()
                .ThrowsAsync(new DeviceRegistrationRejectedException("private error code", detail, 400));
        var logger = Substitute.For<ILogger<E2EEOnboardingViewModel>>();
        var sut = new E2EEOnboardingViewModel(
            _deviceMgmt, _deviceKeys, Substitute.For<IE2EEService>(), _syncService, _settingsService, logger);

        if (path == "approval")
            await sut.StartDeviceApprovalCommand.ExecuteAsync(null);
        else
        {
            if (path == "retry")
            {
                _deviceMgmt.RegisterPendingDeviceAsync().Returns(
                    _ => new DeviceRegistrationResponse { OnboardingSessionId = "expired", ServerChallenge = "challenge" },
                    _ => throw new DeviceRegistrationRejectedException("private error code", detail, 400));
                _deviceMgmt.ActivateViaRecoveryAsync(Arg.Any<string>(), "expired")
                    .ThrowsAsync(new OnboardingSessionExpiredException("The session expired."));
            }
            sut.RecoveryCodeInput = "recovery-code";
            await sut.ActivateWithRecoveryCodeCommand.ExecuteAsync(null);
        }

        Assert.Equal(detail, sut.ErrorMessage);
        var logs = logger.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(ILogger.Log)).ToList();
        Assert.NotEmpty(logs);
        Assert.All(logs, call =>
        {
            var args = call.GetArguments();
            Assert.DoesNotContain("private", args[2]?.ToString() ?? "");
            Assert.DoesNotContain("private", args[3]?.ToString() ?? "");
        });
    }

    [Fact]
    public async Task TheResumedHandover_ReplacesTheDeviceOnKeyLoss()
    {
        AnApprovedDeviceWhoseCopyWillNotOpen(keyLost: true);
        var sut = CreateSut();

        await sut.StartDeviceApprovalCommand.ExecuteAsync(null);

        await _deviceMgmt.Received(1).ReplaceLostDeviceAsync();
        Assert.Equal(OnboardingState.Error, sut.State);
        Assert.Equal(LocalizationSource.Instance["E2EE_Onboarding_KeyLost"], sut.ErrorMessage);
    }

    [Fact]
    public async Task AfterARestart_TheServersKeyMismatchAloneReplacesTheDevice()
    {
        _deviceKeys.GetDeviceId().Returns("device-001");
        _deviceKeys.KeyLossDetected.Returns(false);
        _deviceMgmt.GetDeviceStatusAsync("device-001").Returns(new DeviceStatusResponse
        {
            DeviceId = "device-001",
            Status = DeviceStatus.Active,
        });
        _deviceMgmt.FetchKeyHandoverAsync().ThrowsAsync(new DeviceKeyLostException());
        var sut = CreateSut();

        await sut.StartDeviceApprovalCommand.ExecuteAsync(null);

        await _deviceMgmt.Received(1).ReplaceLostDeviceAsync();
        await _deviceMgmt.DidNotReceive().RegisterPendingDeviceAsync();
    }

    [Fact]
    public async Task WithoutKeyLoss_TheFailureStaysTheRetryableAcceptFailedError()
    {
        AnApprovedDeviceWhoseCopyWillNotOpen(keyLost: false);
        var sut = CreateSut();

        await sut.StartDeviceApprovalCommand.ExecuteAsync(null);

        Assert.Equal(OnboardingState.Error, sut.State);
        Assert.Equal(LocalizationSource.Instance["E2EE_Onboarding_Confirm_AcceptFailed"], sut.ErrorMessage);
        await _deviceMgmt.DidNotReceive().ReplaceLostDeviceAsync();
    }

    [Fact]
    public async Task AnOwnCopyChangedOnTheServer_SaysSoInsteadOfTheGenericFailure()
    {
        AnApprovedDeviceWhoseCopyWillNotOpen(keyLost: false);
        _deviceMgmt.AcceptKeyHandoverAsync(Arg.Any<KeyHandover>()).ThrowsAsync(new StoredKeyCopyChangedException());
        var sut = CreateSut();

        await sut.StartDeviceApprovalCommand.ExecuteAsync(null);

        Assert.Equal(OnboardingState.Error, sut.State);
        Assert.Equal(LocalizationSource.Instance["E2EE_Onboarding_OwnCopyChanged"], sut.ErrorMessage);
    }

    [Fact]
    public async Task ARecoveryCodeEnteredAfterKeyLoss_ReplacesTheDeviceInstead()
    {
        _deviceKeys.KeyLossDetected.Returns(true);
        var sut = CreateSut();
        sut.RecoveryCodeInput = "recovery-code";

        await sut.ActivateWithRecoveryCodeCommand.ExecuteAsync(null);

        await _deviceMgmt.Received(1).ReplaceLostDeviceAsync();
        await _deviceMgmt.DidNotReceive().RegisterPendingDeviceAsync();
        await _deviceMgmt.DidNotReceive().ActivateViaRecoveryAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ARecoveryCodeOnADeviceTheServerKnowsWithOtherKeys_ReplacesTheDevice()
    {
        _deviceMgmt.ThrowIfDeviceKeysLostAsync().ThrowsAsync(new DeviceKeyLostException());
        var sut = CreateSut();
        sut.RecoveryCodeInput = "recovery-code";

        await sut.ActivateWithRecoveryCodeCommand.ExecuteAsync(null);

        await _deviceMgmt.Received(1).ReplaceLostDeviceAsync();
        await _deviceMgmt.DidNotReceive().RegisterPendingDeviceAsync();
        Assert.Equal(LocalizationSource.Instance["E2EE_Onboarding_KeyLost"], sut.ErrorMessage);
    }

    [Fact]
    public async Task Reset_DiscardsThePreviousRecoverySession()
    {
        _settingsService.GetSettingsAsync().Returns(new AppSettings());
        _deviceMgmt.RegisterPendingDeviceAsync().Returns(
            new DeviceRegistrationResponse { OnboardingSessionId = "old-session", ServerChallenge = "old" },
            new DeviceRegistrationResponse { OnboardingSessionId = "new-session", ServerChallenge = "new" });
        var sut = CreateSut();
        sut.RecoveryCodeInput = "first-code";
        await sut.ActivateWithRecoveryCodeCommand.ExecuteAsync(null);
        Assert.Equal(OnboardingState.Success, sut.State);

        sut.Reset();
        sut.RecoveryCodeInput = "second-code";
        await sut.ActivateWithRecoveryCodeCommand.ExecuteAsync(null);

        await _deviceMgmt.Received(1).ActivateViaRecoveryAsync("first-code", "old-session");
        await _deviceMgmt.Received(1).ActivateViaRecoveryAsync("second-code", "new-session");
        await _deviceMgmt.Received(2).RegisterPendingDeviceAsync();
    }

    [Fact]
    public async Task KeyLossDuringApprovalPolling_ReplacesTheDevice()
    {
        _deviceKeys.GetDeviceId().Returns("device-001");
        _deviceMgmt.GetDeviceStatusAsync("device-001").Returns(
            (DeviceStatusResponse?)null,
            new DeviceStatusResponse { DeviceId = "device-001", Status = DeviceStatus.Active });
        _deviceMgmt.RegisterPendingDeviceAsync().Returns(
            new DeviceRegistrationResponse { OnboardingSessionId = "session", ServerChallenge = "challenge" });
        _deviceMgmt.FetchKeyHandoverAsync().ThrowsAsync(new DeviceKeyLostException());
        var replaced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _deviceMgmt.ReplaceLostDeviceAsync().Returns(_ =>
        {
            replaced.TrySetResult();
            return Task.CompletedTask;
        });
        var sut = new E2EEOnboardingViewModel(
            _deviceMgmt, _deviceKeys, Substitute.For<IE2EEService>(), _syncService, _settingsService,
            NullLogger<E2EEOnboardingViewModel>.Instance, pollIntervalOverride: TimeSpan.FromMilliseconds(1));

        await sut.StartDeviceApprovalCommand.ExecuteAsync(null);
        await replaced.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(LocalizationSource.Instance["E2EE_Onboarding_KeyLost"], sut.ErrorMessage);
        await _deviceMgmt.DidNotReceive().AcceptKeyHandoverAsync(Arg.Any<KeyHandover>());
        sut.Cleanup();
    }

    private void AnApprovedDeviceWhoseCopyWillNotOpen(bool keyLost)
    {
        _deviceKeys.GetDeviceId().Returns("device-001");
        _deviceKeys.KeyLossDetected.Returns(keyLost);
        _settingsService.GetSettingsAsync().Returns(new AppSettings());
        _deviceMgmt.GetDeviceStatusAsync("device-001").Returns(new DeviceStatusResponse
        {
            DeviceId = "device-001",
            Status = DeviceStatus.Active,
        });
        var blob = new WrappedUmkBlob
        {
            DeviceId = "device-001",
            Ciphertext = "cannot-open",
            HkdfSalt = "salt",
            CreatedByDeviceId = "device-001",
        };
        _deviceMgmt.FetchKeyHandoverAsync().Returns(
            new KeyHandover(blob, "own-agreement-key", 1, null, null));
        _deviceMgmt.AcceptKeyHandoverAsync(Arg.Any<KeyHandover>())
            .ThrowsAsync(new System.Security.Cryptography.CryptographicException("tag mismatch"));
    }

    private E2EEOnboardingViewModel CreateSut() => new(
        _deviceMgmt, _deviceKeys, Substitute.For<IE2EEService>(), _syncService, _settingsService,
        NullLogger<E2EEOnboardingViewModel>.Instance);
}
