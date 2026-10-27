namespace Pia.Tests.ViewModels;

using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Credits;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Pia.ViewModels;
using Xunit;

/// <summary>Removal notices offer setup or sign-out, including after opening Settings later.</summary>
public class AccountSettingsDeviceGoneTests
{
    private readonly IAuthService _auth = Substitute.For<IAuthService>();
    private readonly IDeviceManagementService _deviceMgmt = Substitute.For<IDeviceManagementService>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly AppSettings _stored = new()
    {
        IsE2EEEnabled = true,
        E2EEEncryptedUmk = "stored-umk",
        E2EEUmkVersion = 2,
        E2EERecoveryConfigured = true,
    };
    private readonly ISyncClientService _sync = Substitute.For<ISyncClientService>();

    public AccountSettingsDeviceGoneTests()
    {
        _settings.GetSettingsAsync().Returns(_stored);
        _loc.Culture.Returns(CultureInfo.GetCultureInfo("en"));
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
    }

    [Fact]
    public void AnUnaskedSignOut_LeavesTheReasonOnTheLoginPage()
    {
        var sut = CreateSut();

        _auth.SignedOutUnexpectedly += Raise.Event<EventHandler<AuthSignedOutEventArgs>>(
            this, new AuthSignedOutEventArgs(AuthSignOutReason.SessionExpired));

        Assert.Equal("Auth_SignedOut_Expired", sut.LoginErrorMessage);
    }

    [Fact]
    public async Task ChoosingReOnboarding_RaisesTheOnboardingRequiredState()
    {
        _dialogs.ShowChoiceDialogAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(DialogChoice.Primary);
        var sut = CreateSut();
        sut.OnboardingViewModel.State = OnboardingState.Success;
        sut.OnboardingViewModel.RecoveryCodeInput = "old-code";
        sut.OnboardingViewModel.StatusMessage = "previous success";
        sut.OnboardingViewModel.DeviceFingerprint = "old-fingerprint";

        RaiseRevocation(true);

        Assert.True(sut.IsE2EEOnboardingRequired);
        Assert.Equal(OnboardingState.Initial, sut.OnboardingViewModel.State);
        Assert.Empty(sut.OnboardingViewModel.RecoveryCodeInput);
        Assert.Empty(sut.OnboardingViewModel.StatusMessage);
        Assert.Empty(sut.OnboardingViewModel.DeviceFingerprint);
        _sync.Received(1).NotifyE2EEOnboardingRequired();
        await _auth.DidNotReceive().LogoutAsync();
    }

    [Fact]
    public async Task ChoosingSignOut_EndsTheSession()
    {
        _dialogs.ShowChoiceDialogAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(DialogChoice.Secondary);
        _dialogs.ShowChoiceDialogAsync(
            "LocalReset_SignOut_Title", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(DialogChoice.Secondary);
        CreateSut();

        RaiseRevocation(true);

        await _auth.Received(1).LogoutAsync();
        Assert.False(_stored.IsE2EEEnabled);
    }

    [Theory]
    [InlineData(true, "Settings_E2EE_Gone_Revoked")]
    [InlineData(false, "Settings_E2EE_Gone_Missing")]
    public async Task TheNotice_NamesWhatHappened(bool wasRevoked, string expectedMessageKey)
    {
        var sut = CreateSut();

        RaiseRevocation(wasRevoked);

        Assert.False(sut.IsE2EEEnabled);
        Assert.False(_stored.IsE2EEEnabled);
        Assert.Null(_stored.E2EEEncryptedUmk);
        Assert.Equal(0, _stored.E2EEUmkVersion);
        Assert.False(_stored.E2EERecoveryConfigured);
        await _settings.DidNotReceive().SaveSettingsAsync(Arg.Any<AppSettings>());
        await _dialogs.Received(1).ShowChoiceDialogAsync(
            "Settings_E2EE_Gone_Title", expectedMessageKey,
            "Settings_E2EE_Gone_SetUpAgain", "Settings_E2EE_Gone_SignOut");
        await _auth.DidNotReceive().LogoutAsync();
    }

    [Fact]
    public async Task ARemovalBeforeSettingsOpens_IsShownOnceOnInitialization()
    {
        RaiseRevocation(false);
        var sut = CreateSut();

        await sut.InitializeAsync();
        await sut.InitializeAsync();

        Assert.True(sut.IsE2EEOnboardingRequired);
        Assert.False(sut.IsE2EEEnabled);
        await _dialogs.Received(1).ShowChoiceDialogAsync(
            "Settings_E2EE_Gone_Title", "Settings_E2EE_Gone_Missing",
            "Settings_E2EE_Gone_SetUpAgain", "Settings_E2EE_Gone_SignOut");
    }

    private void RaiseRevocation(bool wasRevoked)
    {
        _stored.ForgetE2EEKey();
        var notice = new CurrentDeviceRevokedEventArgs(wasRevoked);
        _sync.PendingDeviceRevocation.Returns(notice);
        _sync.IsE2EEOnboardingRequired.Returns(true);
        _sync.CurrentDeviceRevoked += Raise.Event<EventHandler<CurrentDeviceRevokedEventArgs>>(this, notice);
    }

    private AccountSettingsViewModel CreateSut()
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        return new AccountSettingsViewModel(
            NullLogger<SettingsViewModel>.Instance, _settings, _dialogs,
            Substitute.For<global::Wpf.Ui.ISnackbarService>(), _auth, _sync, _loc, _deviceMgmt,
            Substitute.For<IDeviceKeyService>(), Substitute.For<IMemoryService>(),
            Substitute.For<IPolicyService>(),
            new E2EEOnboardingViewModel(
                _deviceMgmt, Substitute.For<IDeviceKeyService>(), Substitute.For<IE2EEService>(),
                _sync, _settings, NullLogger<E2EEOnboardingViewModel>.Instance),
            Substitute.For<IAccountDataService>(), Substitute.For<IFileDialogService>(),
            Substitute.For<ICreditStatusService>(),
            Substitute.For<ILocalDataResetService>());
    }
}
