namespace Pia.Tests.ViewModels;

using System.Globalization;
using System.Net;
using System.Net.Http;
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

// After its keys were lost this computer joined under a new id; the old id is revoked from here, signed by the new one.
public class AccountSettingsPreviousInstallationTests
{
    private readonly IAuthService _auth = Substitute.For<IAuthService>();
    private readonly IDeviceManagementService _deviceMgmt = Substitute.For<IDeviceManagementService>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly AppSettings _stored = new()
    {
        IsE2EEEnabled = true,
        SyncUserId = "account-1",
        ReplacedDeviceId = "old-id",
        ReplacedDeviceAccountId = "account-1",
    };

    public AccountSettingsPreviousInstallationTests()
    {
        _settings.GetSettingsAsync().Returns(_stored);
        _loc.Culture.Returns(CultureInfo.GetCultureInfo("en"));
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _dialogs.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
    }

    [Fact]
    public async Task AnActiveDeviceOfTheSameAccount_OffersTheRemoval()
    {
        var sut = CreateSut();

        await sut.InitializeAsync();

        Assert.Equal("old-id", sut.PreviousInstallationId);
        Assert.True(sut.IsPreviousInstallationVisible);
    }

    [Fact]
    public async Task UntilThisDeviceIsActive_TheRemovalIsHidden()
    {
        _stored.IsE2EEEnabled = false;
        var sut = CreateSut();

        await sut.InitializeAsync();

        Assert.False(sut.IsPreviousInstallationVisible);
    }

    [Fact]
    public async Task Removing_RevokesTheOldIdAndForgetsIt()
    {
        var sut = await InitializedSutAsync();

        await sut.RemovePreviousInstallationCommand.ExecuteAsync(null);

        await _deviceMgmt.Received(1).RevokeDeviceAsync("old-id");
        Assert.Null(_stored.ReplacedDeviceId);
        Assert.False(sut.IsPreviousInstallationVisible);
    }

    [Fact]
    public async Task AnOldIdTheServerNoLongerKnows_IsForgottenToo()
    {
        _deviceMgmt.RevokeDeviceAsync("old-id")
            .ThrowsAsync(new HttpRequestException("gone", null, HttpStatusCode.NotFound));
        var sut = await InitializedSutAsync();

        await sut.RemovePreviousInstallationCommand.ExecuteAsync(null);

        Assert.Null(_stored.ReplacedDeviceId);
        await _dialogs.DidNotReceive().ShowMessageDialogAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ARefusedSignature_KeepsTheOfferAndSaysWhy()
    {
        _deviceMgmt.RevokeDeviceAsync("old-id")
            .ThrowsAsync(new DeviceSignatureRefusedException(E2EEErrorCodes.DeviceNotActive));
        var sut = await InitializedSutAsync();

        await sut.RemovePreviousInstallationCommand.ExecuteAsync(null);

        await _dialogs.Received(1).ShowMessageDialogAsync(
            "Settings_E2EE_RemovePreviousInstallation_Failed_Title", "Settings_E2EE_SignatureRefused_Message");
        Assert.Equal("old-id", _stored.ReplacedDeviceId);
        Assert.True(sut.IsPreviousInstallationVisible);
    }

    [Fact]
    public async Task DecliningTheConfirmation_RevokesNothing()
    {
        _dialogs.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        var sut = await InitializedSutAsync();

        await sut.RemovePreviousInstallationCommand.ExecuteAsync(null);

        await _deviceMgmt.DidNotReceive().RevokeDeviceAsync(Arg.Any<string>());
        Assert.Equal("old-id", _stored.ReplacedDeviceId);
    }

    [Fact]
    public void TheKeyLossSignOut_TellsTheUserWhatComesNext()
    {
        var sut = CreateSut();

        _auth.SignedOutUnexpectedly += Raise.Event<EventHandler<AuthSignedOutEventArgs>>(
            this, new AuthSignedOutEventArgs(AuthSignOutReason.DeviceKeysLost));

        Assert.Equal("E2EE_Onboarding_KeyLost", sut.LoginErrorMessage);
    }

    private async Task<AccountSettingsViewModel> InitializedSutAsync()
    {
        var sut = CreateSut();
        await sut.InitializeAsync();
        return sut;
    }

    private AccountSettingsViewModel CreateSut()
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        var keys = Substitute.For<IDeviceKeyService>();
        var sync = Substitute.For<ISyncClientService>();
        return new AccountSettingsViewModel(
            NullLogger<SettingsViewModel>.Instance, _settings, _dialogs,
            Substitute.For<global::Wpf.Ui.ISnackbarService>(), _auth, sync, _loc, _deviceMgmt,
            keys, Substitute.For<IMemoryService>(),
            Substitute.For<IPolicyService>(),
            new E2EEOnboardingViewModel(
                _deviceMgmt, keys, Substitute.For<IE2EEService>(),
                sync, _settings, NullLogger<E2EEOnboardingViewModel>.Instance),
            Substitute.For<IAccountDataService>(), Substitute.For<IFileDialogService>(),
            Substitute.For<ICreditStatusService>(), Substitute.For<ILocalDataResetService>());
    }
}
