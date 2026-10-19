namespace Pia.Tests.E2EE;

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

/// <summary>The lost-key loop: a device whose CNG keys were recreated can never open an already
/// wrapped copy, so repeating the attempt must route to recovery instead of the AcceptFailed
/// error.</summary>
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

    [Fact]
    public async Task TheResumedHandover_RoutesKeyLossToRecovery()
    {
        AnApprovedDeviceWhoseCopyWillNotOpen(keyLost: true);
        var sut = CreateSut();

        await sut.StartDeviceApprovalCommand.ExecuteAsync(null);

        Assert.Equal(OnboardingState.EnteringRecoveryCode, sut.State);
        Assert.Equal(LocalizationSource.Instance["E2EE_Onboarding_KeyLost"], sut.ErrorMessage);
    }

    [Fact]
    public async Task WithoutKeyLoss_TheFailureStaysTheRetryableAcceptFailedError()
    {
        AnApprovedDeviceWhoseCopyWillNotOpen(keyLost: false);
        var sut = CreateSut();

        await sut.StartDeviceApprovalCommand.ExecuteAsync(null);

        Assert.Equal(OnboardingState.Error, sut.State);
        Assert.Equal(LocalizationSource.Instance["E2EE_Onboarding_Confirm_AcceptFailed"], sut.ErrorMessage);
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
