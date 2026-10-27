namespace Pia.Tests.ViewModels;

using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Models;
using Pia.Services.Credits;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Shared.E2EE;
using Pia.Tests.TestInfrastructure;
using Pia.ViewModels;
using Xunit;

/// <summary>The approval prompts stop once this device cannot approve, and never ask about a settled request.</summary>
public class AccountSettingsPendingDevicesTests
{
    private const string OwnId = "own-device";
    private const string ApproveTitle = "Settings_E2EE_ApproveDevice_Title";

    private readonly IAuthService _auth = Substitute.For<IAuthService>();
    private readonly IDeviceManagementService _deviceMgmt = Substitute.For<IDeviceManagementService>();
    private readonly IDeviceKeyService _deviceKeys = Substitute.For<IDeviceKeyService>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly ISyncClientService _sync = Substitute.For<ISyncClientService>();

    public AccountSettingsPendingDevicesTests()
    {
        _settings.GetSettingsAsync().Returns(new AppSettings());
        _loc.Culture.Returns(CultureInfo.GetCultureInfo("en"));
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _auth.IsLoggedIn.Returns(true);
        _deviceKeys.GetDeviceId().Returns(OwnId);
        _dialogs.ShowConfirmationDialogAsync(ApproveTitle, Arg.Any<string>()).Returns(true);
    }

    [Fact]
    public async Task SessionEndedOnTheFirstApproval_PromptsForNoFurtherDevice()
    {
        _deviceMgmt.GetDevicesAsync().Returns(List(Device(OwnId, DeviceStatus.Active),
            Device("a", DeviceStatus.Pending), Device("b", DeviceStatus.Pending)));
        _deviceMgmt.ApproveDeviceAsync(Arg.Any<string>(), Arg.Any<DeviceInfo>())
            .ThrowsAsync(new DeviceRegistrationRejectedException(E2EEErrorCodes.SessionEnded, "ended"));
        var sut = CreateSut();

        await sut.CheckForPendingDevicesCommand.ExecuteAsync(null);

        await _dialogs.Received(1).ShowConfirmationDialogAsync(ApproveTitle, Arg.Any<string>());
        await _dialogs.DidNotReceive().ShowMessageDialogAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ADeviceSettledBetweenPrompts_IsSkipped()
    {
        _deviceMgmt.GetDevicesAsync().Returns(
            List(Device(OwnId, DeviceStatus.Active), Device("a", DeviceStatus.Pending), Device("b", DeviceStatus.Pending)),
            List(Device(OwnId, DeviceStatus.Active), Device("a", DeviceStatus.Active), Device("b", DeviceStatus.Revoked)));
        var sut = CreateSut();

        await sut.CheckForPendingDevicesCommand.ExecuteAsync(null);

        await _dialogs.Received(1).ShowConfirmationDialogAsync(ApproveTitle, Arg.Any<string>());
        await _deviceMgmt.Received(1).ApproveDeviceAsync("session-a", Arg.Is<DeviceInfo>(d => d.DeviceId == "a"));
        await _deviceMgmt.DidNotReceive().ApproveDeviceAsync(Arg.Any<string>(), Arg.Is<DeviceInfo>(d => d.DeviceId == "b"));
    }

    [Fact]
    public async Task ThisDeviceRevokedAfterTheFirstPrompt_EndsTheLoop()
    {
        _deviceMgmt.GetDevicesAsync().Returns(
            List(Device(OwnId, DeviceStatus.Active), Device("a", DeviceStatus.Pending), Device("b", DeviceStatus.Pending)),
            List(Device(OwnId, DeviceStatus.Revoked), Device("b", DeviceStatus.Pending)));
        _deviceMgmt.ApproveDeviceAsync(Arg.Any<string>(), Arg.Any<DeviceInfo>())
            .ThrowsAsync(new InvalidOperationException("This device is not active on the server"));
        var sut = CreateSut();

        await sut.CheckForPendingDevicesCommand.ExecuteAsync(null);

        await _dialogs.Received(1).ShowConfirmationDialogAsync(ApproveTitle, Arg.Any<string>());
    }

    private static DeviceInfo Device(string id, DeviceStatus status) => new()
    {
        DeviceId = id,
        DeviceName = id,
        Status = status,
        AgreementPublicKey = "agreement",
        SigningPublicKey = "signing",
        OnboardingSessionId = status == DeviceStatus.Pending ? $"session-{id}" : null,
    };

    private static DeviceListResponse List(params DeviceInfo[] devices) => new() { Devices = [.. devices] };

    private AccountSettingsViewModel CreateSut()
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        return new AccountSettingsViewModel(
            NullLogger<SettingsViewModel>.Instance, _settings, _dialogs,
            Substitute.For<global::Wpf.Ui.ISnackbarService>(), _auth, _sync, _loc, _deviceMgmt,
            _deviceKeys, Substitute.For<IMemoryService>(),
            Substitute.For<IPolicyService>(),
            new E2EEOnboardingViewModel(
                _deviceMgmt, _deviceKeys, Substitute.For<IE2EEService>(),
                _sync, _settings, NullLogger<E2EEOnboardingViewModel>.Instance),
            Substitute.For<IAccountDataService>(), Substitute.For<IFileDialogService>(),
            Substitute.For<ICreditStatusService>());
    }
}
