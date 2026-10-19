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

/// <summary>The revoked device lands here: the notice must forget the local key and offer
/// re-onboarding or sign-out — and the reason the user did not choose must reach the UI.</summary>
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

        _sync.CurrentDeviceRevoked += Raise.Event<EventHandler<CurrentDeviceRevokedEventArgs>>(
            this, new CurrentDeviceRevokedEventArgs(true));

        Assert.True(sut.IsE2EEOnboardingRequired);
        _sync.Received(1).NotifyE2EEOnboardingRequired();
        await _auth.DidNotReceive().LogoutAsync();
    }

    [Fact]
    public async Task ChoosingSignOut_EndsTheSession()
    {
        _dialogs.ShowChoiceDialogAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(DialogChoice.Secondary);
        CreateSut();

        _sync.CurrentDeviceRevoked += Raise.Event<EventHandler<CurrentDeviceRevokedEventArgs>>(
            this, new CurrentDeviceRevokedEventArgs(true));

        await _auth.Received(1).LogoutAsync();
        Assert.False(_stored.IsE2EEEnabled);
    }

    [Theory]
    [InlineData(true, "Settings_E2EE_Gone_Revoked")]
    [InlineData(false, "Settings_E2EE_Gone_Missing")]
    public async Task TheNotice_ForgetsTheKeyAndNamesWhatHappened(bool wasRevoked, string expectedMessageKey)
    {
        var sut = CreateSut();

        _sync.CurrentDeviceRevoked += Raise.Event<EventHandler<CurrentDeviceRevokedEventArgs>>(
            this, new CurrentDeviceRevokedEventArgs(wasRevoked));

        Assert.False(sut.IsE2EEEnabled);
        Assert.False(_stored.IsE2EEEnabled);
        Assert.Null(_stored.E2EEEncryptedUmk);
        Assert.Equal(0, _stored.E2EEUmkVersion);
        Assert.False(_stored.E2EERecoveryConfigured);
        await _settings.Received(1).SaveSettingsAsync(_stored);
        await _dialogs.Received(1).ShowChoiceDialogAsync(
            "Settings_E2EE_Gone_Title", expectedMessageKey,
            "Settings_E2EE_Gone_SetUpAgain", "Settings_E2EE_Gone_SignOut");
        await _auth.DidNotReceive().LogoutAsync();
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
            Substitute.For<ICreditStatusService>());
    }
}
