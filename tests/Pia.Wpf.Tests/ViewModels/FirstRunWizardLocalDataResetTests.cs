namespace Pia.Tests.ViewModels;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

public class FirstRunWizardLocalDataResetTests
{
    private readonly IAuthService _auth = Substitute.For<IAuthService>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly ILocalDataResetService _reset = Substitute.For<ILocalDataResetService>();

    [Fact]
    public async Task WizardSignIn_WhenClearFirstChosen_NeverReachesAuth()
    {
        _reset.HasSyncedLocalDataAsync().Returns(true);
        Answer(DialogChoice.Secondary);
        var sut = CreateSut();

        await sut.LoginWithGoogleCommand.ExecuteAsync(null);

        await _auth.DidNotReceive().LoginAsync(Arg.Any<string>());
        _reset.Received(1).ScheduleReset();
        await _reset.Received(1).RestartAsync();
        Assert.False(sut.IsLoggingIn);
    }

    [Fact]
    public async Task WizardPasswordSignIn_WhenCancelled_NeverReachesAuth()
    {
        _reset.HasSyncedLocalDataAsync().Returns(true);
        Answer(DialogChoice.Dismissed);
        var sut = CreateSut();
        sut.LoginEmailInput = "a@b.c";
        sut.LoginPassword = "pw";

        await sut.LoginWithPasswordCommand.ExecuteAsync(null);

        await _auth.DidNotReceive().LoginWithPasswordAsync(Arg.Any<string>(), Arg.Any<string>());
        Assert.False(sut.IsLoggingIn);
    }

    [Theory]
    [InlineData(null, false, true)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(null, true, false)]
    public void ShouldMarkWizardCompletedOnClose(bool? dialogResult, bool resetPending, bool expected) =>
        Assert.Equal(expected, App.ShouldMarkWizardCompletedOnClose(dialogResult, resetPending));

    private void Answer(DialogChoice choice) =>
        _dialogs.ShowChoiceDialogAsync(
            "LocalReset_SignIn_Title", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(choice);

    private FirstRunWizardViewModel CreateSut()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        var policy = Substitute.For<IPolicyService>();
        policy.IsLoginProviderAllowed(Arg.Any<string>()).Returns(true);
        var sync = Substitute.For<ISyncClientService>();
        var deviceMgmt = Substitute.For<IDeviceManagementService>();
        var deviceKeys = Substitute.For<IDeviceKeyService>();

        return new FirstRunWizardViewModel(
            settings, Substitute.For<IMemoryService>(), Substitute.For<IVoiceInputService>(),
            loc, _auth, Substitute.For<IProviderService>(), sync, deviceMgmt, policy,
            new E2EEOnboardingViewModel(
                deviceMgmt, deviceKeys, Substitute.For<IE2EEService>(), sync, settings,
                NullLogger<E2EEOnboardingViewModel>.Instance),
            new E2EESetupStepViewModel(
                deviceMgmt, deviceKeys, sync, Substitute.For<IOutputService>(),
                NullLogger<E2EESetupStepViewModel>.Instance),
            NullLogger<FirstRunWizardViewModel>.Instance, _dialogs, _reset);
    }
}
