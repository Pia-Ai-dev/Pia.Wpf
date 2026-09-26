namespace Pia.Tests.ViewModels;

using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Pia.ViewModels;
using Xunit;

public class AccountSettingsAccountDataTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IAuthService _auth = Substitute.For<IAuthService>();
    private readonly ISyncClientService _sync = Substitute.For<ISyncClientService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly IAccountDataService _accountData = Substitute.For<IAccountDataService>();
    private readonly IFileDialogService _fileDialogs = Substitute.For<IFileDialogService>();

    public AccountSettingsAccountDataTests()
    {
        _settings.GetSettingsAsync().Returns(new AppSettings());
        _loc[Arg.Any<string>()].Returns(ci => ci.Arg<string>());
    }

    [Fact]
    public async Task DeleteAccount_WhenTheDialogIsDeclined_DeletesNothing()
    {
        _dialogs.ShowAccountDeletionDialogAsync(Arg.Any<AccountDeletionViewModel>()).Returns(false);

        await CreateSut().DeleteAccountCommand.ExecuteAsync(null);

        await _accountData.DidNotReceiveWithAnyArgs().DeleteAsync(default, Ct);
        await _auth.DidNotReceive().LogoutAsync();
        await _sync.DidNotReceive().StopBackgroundSyncAndWaitAsync();
    }

    [Fact]
    public async Task DeleteAccount_ClosingTheDialog_CancelsARunningExport()
    {
        var exportToken = new TaskCompletionSource<CancellationToken>();
        _fileDialogs.PromptSaveFile(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(@"C:\exports\pia.zip");
        _accountData.ExportToFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var token = ci.Arg<CancellationToken>();
            exportToken.SetResult(token);
            return Task.Delay(Timeout.Infinite, token);
        });
        _dialogs.ShowAccountDeletionDialogAsync(Arg.Any<AccountDeletionViewModel>()).Returns(ci =>
        {
            _ = ci.Arg<AccountDeletionViewModel>().ExportCommand.ExecuteAsync(null);
            return false;
        });

        await CreateSut().DeleteAccountCommand.ExecuteAsync(null);

        Assert.True((await exportToken.Task).IsCancellationRequested);
    }

    [Fact]
    public async Task DeleteAccount_OnceTheServerDeleted_SignsOutAndStopsSyncing()
    {
        ConfirmDialogWith(password: "");
        _accountData.DeleteAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(AccountDeletionOutcome.Deleted);

        await CreateSut().DeleteAccountCommand.ExecuteAsync(null);

        Received.InOrder(() =>
        {
            _sync.StopBackgroundSyncAndWaitAsync();
            _accountData.DeleteAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
            _auth.LogoutAsync();
        });
        _sync.DidNotReceive().StartBackgroundSync();
    }

    [Theory]
    [InlineData(AccountDeletionOutcome.InvalidPassword)]
    [InlineData(AccountDeletionOutcome.Failed)]
    public async Task DeleteAccount_WhenTheServerRefuses_StaysSignedInAndResumesSync(AccountDeletionOutcome outcome)
    {
        _auth.Provider.Returns("local");
        _sync.IsSyncActive.Returns(true);
        ConfirmDialogWith(password: "wrong");
        _accountData.DeleteAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(outcome);

        await CreateSut().DeleteAccountCommand.ExecuteAsync(null);

        await _auth.DidNotReceive().LogoutAsync();
        _sync.Received(1).StartBackgroundSync();
    }

    [Fact]
    public async Task DeleteAccount_WhenTheRequestThrows_StaysSignedInAndResumesSync()
    {
        _sync.IsSyncActive.Returns(true);
        ConfirmDialogWith(password: "");
        _accountData.DeleteAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("offline"));

        await CreateSut().DeleteAccountCommand.ExecuteAsync(null);

        await _auth.DidNotReceive().LogoutAsync();
        _sync.Received(1).StartBackgroundSync();
    }

    [Fact]
    public async Task DeleteAccount_WhenRefused_LeavesAStoppedSyncStopped()
    {
        _sync.IsSyncActive.Returns(false);
        ConfirmDialogWith(password: "");
        _accountData.DeleteAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(AccountDeletionOutcome.Failed);

        await CreateSut().DeleteAccountCommand.ExecuteAsync(null);

        _sync.DidNotReceive().StartBackgroundSync();
    }

    [Fact]
    public async Task DeleteAccount_ForALocalAccount_AsksForAndSendsThePassword()
    {
        _auth.Provider.Returns("local");
        var dialog = ConfirmDialogWith(password: "s3cret");

        await CreateSut().DeleteAccountCommand.ExecuteAsync(null);

        Assert.True(dialog()!.RequiresPassword);
        await _accountData.Received(1).DeleteAsync("s3cret", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAccount_ForASingleSignOnAccount_SendsNoPassword()
    {
        _auth.Provider.Returns("microsoft");
        var dialog = ConfirmDialogWith(password: "");

        await CreateSut().DeleteAccountCommand.ExecuteAsync(null);

        Assert.False(dialog()!.RequiresPassword);
        await _accountData.Received(1).DeleteAsync(null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExportAccountData_WritesToTheChosenFile()
    {
        _fileDialogs.PromptSaveFile(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(@"C:\exports\pia.zip");

        await CreateSut().ExportAccountDataCommand.ExecuteAsync(null);

        await _accountData.Received(1).ExportToFileAsync(@"C:\exports\pia.zip", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExportAccountData_WhenThePickerIsCancelled_ExportsNothing()
    {
        _fileDialogs.PromptSaveFile(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns((string?)null);

        await CreateSut().ExportAccountDataCommand.ExecuteAsync(null);

        await _accountData.DidNotReceiveWithAnyArgs().ExportToFileAsync(default!, Ct);
    }

    private Func<AccountDeletionViewModel?> ConfirmDialogWith(string password)
    {
        AccountDeletionViewModel? shown = null;
        _dialogs.ShowAccountDeletionDialogAsync(Arg.Any<AccountDeletionViewModel>()).Returns(ci =>
        {
            shown = ci.Arg<AccountDeletionViewModel>();
            shown.IsUnderstood = true;
            shown.Password = password;
            return true;
        });
        return () => shown;
    }

    private AccountSettingsViewModel CreateSut()
    {
        // AccountSettingsViewModel demands a captured context; inline keeps the assertions synchronous.
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        var deviceMgmt = Substitute.For<IDeviceManagementService>();
        var deviceKeys = Substitute.For<IDeviceKeyService>();

        return new AccountSettingsViewModel(
            NullLogger<SettingsViewModel>.Instance, _settings, _dialogs,
            Substitute.For<global::Wpf.Ui.ISnackbarService>(), _auth, _sync, _loc, deviceMgmt,
            deviceKeys, Substitute.For<IMemoryService>(), Substitute.For<IPolicyService>(),
            new E2EEOnboardingViewModel(
                deviceMgmt, deviceKeys, Substitute.For<IE2EEService>(), _sync, _settings,
                NullLogger<E2EEOnboardingViewModel>.Instance),
            _accountData, _fileDialogs);
    }
}
