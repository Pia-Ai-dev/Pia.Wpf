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

public class AccountSettingsLocalDataResetTests
{
    private readonly IAuthService _auth = Substitute.For<IAuthService>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly ISyncClientService _sync = Substitute.For<ISyncClientService>();
    private readonly IAccountDataService _accountData = Substitute.For<IAccountDataService>();
    private readonly ILocalDataResetService _reset = Substitute.For<ILocalDataResetService>();
    private readonly IDeviceManagementService _deviceMgmt = Substitute.For<IDeviceManagementService>();

    public AccountSettingsLocalDataResetTests()
    {
        _settings.GetSettingsAsync().Returns(new AppSettings());
        _loc.Culture.Returns(CultureInfo.GetCultureInfo("en"));
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _reset.UploadPendingChangesAsync().Returns(true);
        _auth.LoginAsync("google").Returns((false, (string?)"nope"));
    }

    private void Answer(string title, DialogChoice choice) =>
        _dialogs.ShowChoiceDialogAsync(
            title, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(choice);

    [Fact]
    public async Task SignIn_WhenClearFirstChosen_NeverReachesAuth()
    {
        _reset.HasSyncedLocalDataAsync().Returns(true);
        Answer("LocalReset_SignIn_Title", DialogChoice.Secondary);
        var sut = CreateSut();

        await sut.LoginWithGoogleCommand.ExecuteAsync(null);

        await _auth.DidNotReceive().LoginAsync(Arg.Any<string>());
        _reset.Received(1).ScheduleReset();
        Assert.False(sut.IsSyncLoggingIn);
    }

    [Fact]
    public async Task PasswordSignIn_WhenCancelled_NeverReachesAuth()
    {
        _reset.HasSyncedLocalDataAsync().Returns(true);
        Answer("LocalReset_SignIn_Title", DialogChoice.Dismissed);
        var sut = CreateSut();
        sut.LoginEmail = "a@b.c";
        sut.LoginPassword = "pw";

        await sut.LoginWithPasswordCommand.ExecuteAsync(null);

        await _auth.DidNotReceive().LoginWithPasswordAsync(Arg.Any<string>(), Arg.Any<string>());
        Assert.False(sut.IsSyncLoggingIn);
    }

    [Fact]
    public async Task SignIn_WithNoLocalData_SignsIn()
    {
        _reset.HasSyncedLocalDataAsync().Returns(false);
        var sut = CreateSut();

        await sut.LoginWithGoogleCommand.ExecuteAsync(null);

        await _auth.Received(1).LoginAsync("google");
    }

    [Fact]
    public async Task SignOutButton_Keep_UploadsThenLogsOut()
    {
        Answer("LocalReset_SignOut_Title", DialogChoice.Secondary);
        var sut = CreateSut();

        await sut.SyncLogoutCommand.ExecuteAsync(null);

        Received.InOrder(() =>
        {
            _reset.UploadPendingChangesAsync();
            _auth.LogoutAsync();
        });
        _reset.DidNotReceive().ScheduleReset();
    }

    [Fact]
    public async Task RevokedSignOut_PromptsWithoutUploading()
    {
        Answer("Settings_E2EE_Gone_Title", DialogChoice.Secondary);
        Answer("LocalReset_SignOut_Title", DialogChoice.Primary);
        CreateSut();

        var notice = new CurrentDeviceRevokedEventArgs(true);
        _sync.PendingDeviceRevocation.Returns(notice);
        _sync.CurrentDeviceRevoked += Raise.Event<EventHandler<CurrentDeviceRevokedEventArgs>>(this, notice);

        await _reset.DidNotReceive().UploadPendingChangesAsync();
        _reset.Received(1).ScheduleReset();
        await _auth.Received(1).LogoutAsync();
        await _reset.Received(1).RestartAsync();
    }

    [Fact]
    public async Task DeletedAccount_SignsOutThenOffersClear()
    {
        _dialogs.ShowAccountDeletionDialogAsync(Arg.Any<AccountDeletionViewModel>()).Returns(true);
        _accountData.DeleteAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(AccountDeletionOutcome.Deleted);
        Answer("LocalReset_Deleted_Title", DialogChoice.Secondary);
        var sut = CreateSut();

        await sut.DeleteAccountCommand.ExecuteAsync(null);

        await _auth.Received(1).LogoutAsync();
        await _dialogs.Received(1).ShowChoiceDialogAsync(
            "LocalReset_Deleted_Title", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        await _dialogs.DidNotReceive().ShowChoiceDialogAsync(
            "LocalReset_SignOut_Title", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        _reset.DidNotReceive().ScheduleReset();
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
            _accountData, Substitute.For<IFileDialogService>(),
            Substitute.For<ICreditStatusService>(), _reset);
    }
}
