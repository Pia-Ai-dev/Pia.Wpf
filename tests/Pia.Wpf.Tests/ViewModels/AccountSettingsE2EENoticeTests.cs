namespace Pia.Tests.ViewModels;

using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Credits;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Shared.E2EE;
using Pia.Tests.TestInfrastructure;
using Pia.ViewModels;
using Xunit;

public class AccountSettingsE2EENoticeTests
{
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IAuthService _auth = Substitute.For<IAuthService>();
    private readonly ISyncClientService _sync = Substitute.For<ISyncClientService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly IDeviceManagementService _deviceMgmt = Substitute.For<IDeviceManagementService>();
    private readonly IDeviceKeyService _deviceKeys = Substitute.For<IDeviceKeyService>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();

    public AccountSettingsE2EENoticeTests()
    {
        _settings.GetSettingsAsync().Returns(new AppSettings());
        _loc.Culture.Returns(CultureInfo.GetCultureInfo("en"));
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => $"{ci[0]}|{string.Join("|", (object[])ci[1])}");
    }

    private AccountSettingsViewModel CreateSut(bool signedIn = true)
    {
        // AccountSettingsViewModel demands a captured context; inline keeps the assertions synchronous.
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());

        var sut = new AccountSettingsViewModel(
            NullLogger<SettingsViewModel>.Instance, _settings, _dialogs,
            Substitute.For<global::Wpf.Ui.ISnackbarService>(), _auth, _sync, _loc, _deviceMgmt,
            _deviceKeys, Substitute.For<IMemoryService>(), Substitute.For<IPolicyService>(),
            new E2EEOnboardingViewModel(
                _deviceMgmt, _deviceKeys, Substitute.For<IE2EEService>(), _sync, _settings,
                NullLogger<E2EEOnboardingViewModel>.Instance),
            Substitute.For<IAccountDataService>(), Substitute.For<IFileDialogService>(),
            Substitute.For<ICreditStatusService>());
        sut.IsSyncLoggedIn = signedIn;
        return sut;
    }

    [Fact]
    public void SignedInWithoutEncryption_ShowsTheNotice()
        => Assert.True(CreateSut().IsE2EEOffNoticeVisible);

    [Fact]
    public void SignedOut_ShowsNoNotice()
        => Assert.False(CreateSut(signedIn: false).IsE2EEOffNoticeVisible);

    [Fact]
    public void AnAccountThatNeedsOnboarding_ShowsTheOnboardingInstead()
    {
        var sut = CreateSut();
        sut.IsE2EEOnboardingRequired = true;

        Assert.False(sut.IsE2EEOffNoticeVisible);
    }

    [Fact]
    public async Task DismissingTheNotice_HidesItAndLeavesEncryptionOff()
    {
        var sut = CreateSut();

        sut.DismissE2EEOffNoticeCommand.Execute(null);

        Assert.False(sut.IsE2EEOffNoticeVisible);
        Assert.False(sut.IsE2EEEnabled);
        await _deviceMgmt.DidNotReceive().BootstrapFirstDeviceAsync();
    }

    [Fact]
    public async Task TheNoticeButton_RunsTheSameEnableFlowAsTheToggle()
    {
        var sut = CreateSut();
        var raised = new List<string?>();
        sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        sut.EnableE2EEFromNoticeCommand.Execute(null);

        Assert.True(sut.IsE2EEEnabled);
        Assert.False(sut.IsE2EEOffNoticeVisible);
        Assert.Contains(nameof(AccountSettingsViewModel.IsE2EEOffNoticeVisible), raised);
        await _sync.Received(1).StopBackgroundSyncAndWaitAsync();
        await _deviceMgmt.Received(1).BootstrapFirstDeviceAsync();
    }

    [Fact]
    public void TheNoticeButton_IsDisabledWhileEncryptionIsBeingEnabled()
    {
        var sut = CreateSut();
        sut.CanToggleE2EE = false;

        Assert.False(sut.EnableE2EEFromNoticeCommand.CanExecute(null));
        Assert.False(sut.IsE2EEOffNoticeVisible);
    }

    // The approver's own fingerprint is what the joining device asks the person to compare next.
    [Fact]
    public async Task TheApprovalDialog_ShowsBothFingerprints()
    {
        _deviceKeys.ComputeFingerprint("join-key").Returns("JOIN-FP");
        _deviceKeys.GetFingerprint().Returns("OWN-FP");
        _deviceMgmt.GetDevicesAsync().Returns(new DeviceListResponse
        {
            Devices =
            [
                new DeviceInfo
                {
                    DeviceId = "dev-new", DeviceName = "New PC", Status = DeviceStatus.Pending,
                    AgreementPublicKey = "join-key", SigningPublicKey = "sign-key", OnboardingSessionId = "session-1",
                },
            ],
        });
        _dialogs.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(false);

        await CreateSut().CheckForPendingDevicesCommand.ExecuteAsync(null);

        await _dialogs.Received(1).ShowConfirmationDialogAsync(
            "Settings_E2EE_ApproveDevice_Title",
            "Settings_E2EE_ApproveDevice_Message|New PC|JOIN-FP|OWN-FP");
        await _deviceMgmt.DidNotReceive().ApproveDeviceAsync(Arg.Any<string>(), Arg.Any<DeviceInfo>());
    }
}
